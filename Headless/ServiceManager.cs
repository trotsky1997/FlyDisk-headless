// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace FlyDisk.Headless;

internal static class ServiceManager
{
    public const string Name = "FlyDiskHeadless";
    public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Name);
    public static readonly string ConfigPath = Path.Combine(DataDirectory, "config.json");

    internal static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run from an elevated administrator terminal (or SSH administrator session). No UAC dialog is opened.");
    }

    internal static void SecureDirectory(string path, bool allowUsersRead)
    {
        NativeBackend.RejectReparsePoints(path);
        var directory = Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (allowUsersRead)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    internal static void Install(string configPath, bool automatic)
    {
        var config = HostConfig.Load(configPath);
        string executable = Environment.ProcessPath ?? throw new IOException("Executable path unavailable.");
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
        if (!string.Equals(Path.GetDirectoryName(executable), expected, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(executable), "FlyDisk.Headless.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Extract the entire published package into " + expected + " and run its FlyDisk.Headless.exe to install.");
        if (GetStatus() != "not-installed") throw new IOException("Service already exists. Stop and uninstall it before changing installation settings.");
        SecureDirectory(expected, true);
        SecureDirectory(DataDirectory, false);
        using var owner = HostProcess.AcquireOwnership();
        new StateStore(DataDirectory, config).AssertRecoveryConfigMatches();
        // 注册只保存配置，不接管磁盘；启用加速必须另发 start 命令。
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, StateStore.Json));
        string imagePath = "\"" + executable + "\" service";
        External.Run("sc.exe", ["create", Name, "binPath=", imagePath, "start=", automatic ? "delayed-auto" : "demand",
            "depend=", "MSiSCSI", "obj=", "LocalSystem", "DisplayName=", "FlyDisk Headless Cache"]);
        External.Run("sc.exe", ["description", Name, "FlyDisk block cache. Graceful stop returns the source disk to Windows. See status/recover commands after failures."]);
    }

    internal static string GetStatus()
    {
        using var controller = new ServiceController(Name);
        try { return controller.Status.ToString(); }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 }) { return "not-installed"; }
    }

    internal static void Start()
    {
        using var controller = new ServiceController(Name);
        if (controller.Status == ServiceControllerStatus.Running) return;
        controller.Start();
        Wait(controller, ServiceControllerStatus.Running);
    }

    internal static void Stop()
    {
        using var controller = new ServiceController(Name);
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        controller.Stop();
        Wait(controller, ServiceControllerStatus.Stopped);
    }

    private static void Wait(ServiceController controller, ServiceControllerStatus target)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            controller.Refresh();
            if (controller.Status == target) return;
            if (target == ServiceControllerStatus.Running && controller.Status == ServiceControllerStatus.Stopped)
                throw new IOException("Service startup failed. Inspect status.json for the error and recovery state.");
            Thread.Sleep(250);
        }
        throw new System.TimeoutException("SCM operation is still pending or was refused. The service was not killed. Inspect status.json and the Windows service state.");
    }

    internal static void Uninstall()
    {
        string status = GetStatus();
        if (status == "not-installed") return;
        if (status != "Stopped") throw new IOException("Stop the service first; uninstall never kills active disk IO.");
        using var owner = HostProcess.AcquireOwnership();
        string stateFile = Path.Combine(DataDirectory, "status.json");
        if (File.Exists(stateFile) && JsonSerializer.Deserialize<HostState>(File.ReadAllText(stateFile), StateStore.Json) is { RecoveryRequired: true })
            throw new IOException("Run recover successfully before uninstalling. The disk may still be offline.");
        External.Run("sc.exe", ["delete", Name]);
        // 保留配置与缓存，卸载绝不递归删除磁盘内容。
    }
}

internal interface IHeadlessHost
{
    void Start();
    void Stop();
}

internal sealed class DiskService(IHeadlessHost host) : ServiceBase
{
    // SCM 读取 ServiceName 发生在 OnStart 之前。
    public static DiskService Create(IHeadlessHost host) => new(host)
    { ServiceName = ServiceManager.Name, CanStop = true, CanShutdown = true, AutoLog = true };

    private void Await(Action action)
    {
        var operation = Task.Run(action);
        while (!operation.IsCompleted)
        {
            RequestAdditionalTime(15_000);
            Task.WhenAny(operation, Task.Delay(5_000)).GetAwaiter().GetResult();
        }
        operation.GetAwaiter().GetResult();
    }

    protected override void OnStart(string[] args) => Await(host.Start);
    protected override void OnStop()
    {
        // ServiceBase 的停止回调运行在线程池；外抛异常可能结束进程。
        // 后端拒绝时保持 STOP_PENDING 并重试，不谎报停止，也不强杀正在运行的 IO。
        while (true)
        {
            try { Await(host.Stop); return; }
            catch (Exception ex)
            {
                FlyDisk.Engine.LogService.DebugFile("headless stop pending: " + ex);
                RequestAdditionalTime(15_000);
                Thread.Sleep(5_000);
            }
        }
    }
    protected override void OnShutdown()
    {
        // 不强放在途 IO，不把仍在使用的源盘联机。拒绝会留下可诊断状态。
        try { host.Stop(); } catch (Exception ex) { FlyDisk.Engine.LogService.DebugFile("headless shutdown: " + ex); }
    }
}
