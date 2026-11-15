# FlyDisk Headless — Windows 服务版

基于 [youke1686/FlyDisk](https://github.com/youke1686/FlyDisk) 0.1.7-beta 的 GPL-3.0-or-later 分支。
此版本是预览版：单盘、SSD 读取缓存、写透传，支持命令行与原生 Windows Service Control Manager（SCM），不依赖 WinForms、用户登录或远程桌面。

## 行为与修复

- `Core` 直接编译共享引擎源码，不引用 GUI；L2 校验器从窗口文件抽离，原 GUI 仍可编译。
- 精确匹配 `型号|序列号` 和字节容量；拒绝系统盘、分页文件盘、程序所在盘、只读盘、iSCSI 源盘以及源缓存同盘。
- 512e / 4Kn 自动选取 8 / 1 扇区组成一个 4 KiB 缓存块。
- 配置严格解析，未知字段、无效容量、缺失身份和未接受风险均拒绝；缓存上限按 64 MiB 对齐，预留至少 1 GiB 空间。
- 启动前写入持久化恢复检查点；启动失败尝试先停引擎，再恢复物理盘。不能证明安全时报告 `recovery-required`，不强行联机。
- **正常 `stop` 会将原盘归还 Windows**：登出自己的 iSCSI 会话、等待在途 IO、关闭缓存、确认没有残留克隆盘、作废 L2 身份戳，最后联机原盘。
- 每次启动对已有 L2 **全量逐块校验**，包括 USB 和此前已脱机的盘。完整校验发现内容变化时，默认仅将源盘的新内容刷新到 SSD 缓存，解决正常还盘后 NTFS 元数据变化导致无法重启的问题；源盘校验句柄始终只读。设 `RefreshChangedBlocks=false` 可严格拒绝任何差异。读失败、重复/越界记录、取消或扫描不完整均中止，不会跳过校验。
- `L2Verifier.ConfirmLedger()` 现在在引擎内强制检查校验/修复是否完整成功，不能仅依赖 UI 调用顺序；修复了长度计算中的中间整数溢出。
- 跨线程文件句柄锁替代线程归属的 Mutex；状态文件原子替换，进程 PID 与启动时间共同识别存活状态，避免残留文件误报运行中。
- SCM 启停定期更新等待提示；拒绝停服时保持 `StopPending` 并自动重试，不从线程池回调抛异常或强杀进程。安装默认手动启动，可选择延迟自动启动。

运行中仍采用上游的“整盘脱机 → 独占块设备 → 回环 iSCSI 重新呈现”的架构。它不是内核过滤驱动，不是普通文件夹缓存；启动、停服和校验会中断目标盘上的打开文件/共享访问。重要数据使用前先在可丢弃的测试盘验证并备份。

## 编译和验证

Windows x64，.NET 10 SDK：

```powershell
dotnet run --project Tests/FlyDisk.Headless.Tests.csproj -c Release
dotnet build FlyDisk/FlyDisk.csproj -c Release
dotnet publish Headless/FlyDisk.Headless.csproj -c Release -r win-x64 --self-contained true -o artifacts/headless
```

独立发布包内含 .NET 运行时，目标机器不需要另外安装 .NET 或 Windows Desktop Runtime。
发布时一并包含根目录 `LICENSE`、`THIRD-PARTY-NOTICES.md` 与 `FlyDisk/Licenses`。

`Tests` 使用内存模拟块设备和临时 L2 文件，不脱机真实磁盘。覆盖生命周期失败恢复、停止拒绝、取消、状态存活识别、512/4096 扇区、冷读合并、L2 命中、完整/部分写入、部分写失败、缓存重载和失效拒绝。
GitHub Actions 还在可销毁 Windows runner 中运行真实 SCM 安装、启动、停止拒绝、停止和卸载测试；该服务夹具不操作物理盘。
这些测试不替代真实硬盘的 iSCSI 挂载、NTFS/SMB 连续 IO、USB 拔插、关机和断电测试。

## 安装

把**整个发布目录**解压至 `C:\Program Files\FlyDiskHeadless`。在管理员 PowerShell / 管理员 SSH 中操作：

```powershell
Set-Location 'C:\Program Files\FlyDiskHeadless'
.\FlyDisk.Headless.exe list
```

复制 `config.example.json`，填入 `list` 的精确 `Identity` 和 `Disk.SizeBytes`，选择另一块物理盘上的缓存卷。`CacheSizeBytes=34359738368` 表示 **32 GiB**，容器按需增长，不会预先占满32 GiB。理解整盘接管与中断行为后，设置 `RiskAccepted=true`。

```powershell
.\FlyDisk.Headless.exe preflight --config .\config.example.json
.\FlyDisk.Headless.exe install --config .\config.example.json
.\FlyDisk.Headless.exe start
.\FlyDisk.Headless.exe status
```

`preflight` 不脱机磁盘；它检查配置、目标盘、空间与监听端口。实际缓存内容验证和挂载发生在 `start`。
`install` 注册服务但不启动。服务名 `FlyDiskHeadless`，账号 LocalSystem，依赖 `MSiSCSI`，可在 `services.msc` 中管理。
程序目录允许普通用户读取/执行；服务数据目录仅允许管理员和 SYSTEM 读写。服务配置复制到 `%ProgramData%\FlyDiskHeadless\config.json`；安装以后编辑此副本才会影响下一次启动。

需要开机启动时，在初次安装命令末尾添加 `--automatic`，或停服后按 Windows 服务管理方式改为“自动（延迟启动）”。推荐先用手动启动完成测试。全量 L2 校验可能较慢；CLI 等待两分钟后会返回“仍在进行”，不会终止服务，可继续用 `status` 查看。

```powershell
.\FlyDisk.Headless.exe stop
.\FlyDisk.Headless.exe uninstall
```

卸载要求服务已停止、没有未完成的恢复，不删除缓存、配置或用户数据。关闭 SCM 服务属性窗口后才可能完成被标记删除的服务清理。

## 前台、状态与恢复

```powershell
.\FlyDisk.Headless.exe run --config .\config.example.json
# Ctrl+C 请求安全停止；被拒绝后进程继续保留，关闭占用后可再次 Ctrl+C。
.\FlyDisk.Headless.exe status
.\FlyDisk.Headless.exe recover
```

`status` 返回 JSON：SCM 状态、进程存活、状态文件是否过期，以及 `Phase`、错误、`RecoveryRequired` 和运行期缓存统计。字段和 CLI 错误为稳定的英文运维接口；底层引擎消息按配置选择中文/英文。运行时每5秒更新一次快照。

`recover` 用于崩溃/强杀后原盘仍脱机：必须取得同一把进程锁，精确验证身份，确认没有目标端口监听、iSCSI 会话或同身份克隆盘，再作废缓存并联机原盘。任何检查不确定都会拒绝。它不会初始化、格式化、删除卷或强改盘符，也不会停止系统 MSiSCSI 服务。

存在未完成的恢复检查点时，新的 `start` 会拒绝并保留原记录，必须先 `recover`；同时禁止更换目标盘或缓存配置，防止旧盘的恢复信息被新一轮预检覆盖。

断电、强杀、Windows 强制结束服务仍可能留下脱机源盘；这不是无条件自动容错。系统关机回调只尝试正常停机，不强行放开正在使用的设备。Windows 的关机时间限制仍然存在。遇到 `stop-refused` 先关闭目标盘上的文件/应用/共享占用；SCM 服务保持 `StopPending` 并每5秒重试，前台模式则再次 Ctrl+C。不要同时运行上游 GUI 与 headless，不要在运行期改配置或移动缓存。

数据位置：

| 内容 | 位置 |
| --- | --- |
| headless 配置、原子状态、单实例锁 | `%ProgramData%\FlyDiskHeadless\` |
| 引擎诊断日志（沿用上游路径） | `%ProgramData%\FlyDisk\debug-security.log` |
| L2 容器、索引 | `<缓存卷>\FlyDisk.Cache\<身份散列>\` |

服务等待提示采用微软的 [ServiceBase.RequestAdditionalTime](https://learn.microsoft.com/dotnet/api/system.serviceprocess.servicebase.requestadditionaltime)；它适用于启动/停止阶段，不延长系统关机的硬性截止时间。

## 范围

目前只支持一个 headless 服务管理一块本地盘，不提供远程块设备、多盘编排、写回缓存或自动修复损坏缓存。没有声称提高底层吞吐量；本分支优化的是无人值守管理、依赖体积与故障恢复，并保留上游已有的合并回源和读取缓存能力。

## English overview

This GPL-3.0-or-later preview fork separates FlyDisk's engine from WinForms and adds a Windows ServiceBase host plus `install/start/stop/status/recover/uninstall` commands. It uses a single local disk, loopback-only iSCSI, SSD read caching and write-through writes. Normal stop returns the original disk online after draining IO, detaching iSCSI, checking that no clone remains, and invalidating the cache ledger. Existing cache contents are fully verified on every start. Uncertain recovery fails closed and reports its state instead of forcing a disk online. Crashes/power loss can still require manual `recover`.

Publish self-contained for win-x64, copy the whole package to `C:\Program Files\FlyDiskHeadless`, edit the example with the exact identity and size from `list`, accept the takeover behavior, then run the commands above from an elevated terminal. Installation defaults to manual startup and does not take over a disk; `--automatic` chooses delayed auto-start. Tests use synthetic block devices, temporary files, and a disposable CI service fixture. Actual physical-disk mounting, SMB IO, USB hotplug and power-loss behavior still require hardware acceptance tests.
