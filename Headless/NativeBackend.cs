// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;

namespace FlyDisk.Headless;

public sealed class NativeBackend(HostConfig settings, bool recoveryOnly = false) : IAcceleratorBackend
{
    private PhysicalDiskInfo? selected;
    private DiskConfig? config;
    private TargetService? engine;
    public L2VerifyProgress? VerifyProgress { get; private set; }
    public L2VerifyResult? Verification { get; private set; }
    public L2RepairResult? CacheRefresh { get; private set; }

    private PhysicalDiskInfo Resolve()
    {
        var matches = PhysicalDiskHandle.Enumerate().Where(d =>
            PhysicalDiskHandle.DescribeIdentity(d) == settings.DiskIdentity).ToArray();
        if (matches.Length != 1) throw new IOException("Expected exactly one disk with the configured identity; refusing ambiguous/absent disk.");
        var disk = matches[0];
        if (disk.SizeBytes != settings.ExpectedSizeBytes || disk.BytesPerSector is not (512 or 4096) ||
            disk.IsReadOnly || disk.BusType == 9 || !PhysicalDiskHandle.HasStableIdentity(disk))
            throw new IOException("Disk capacity, sector size, bus or write protection failed validation.");
        string reason = PhysicalDiskHandle.DescribeTargetDiskBlockReason(disk.DiskNumber);
        if (!string.IsNullOrEmpty(reason)) throw new IOException(reason);
        return disk;
    }

    public void Preflight()
    {
        settings.Validate();
        Locale.SetLanguage(settings.Language);
        AssertNoListener();
        selected = Resolve();
        int cacheDisk = PhysicalDiskHandle.GetDiskNumberOfVolume(settings.CacheVolume + "\\");
        if (cacheDisk < 0 || cacheDisk == selected.DiskNumber) throw new IOException("Cache must reside on another physical disk.");
        long hash = SsdCacheService.ComputeIdentity(BlockSourceInfo.FromDisk(selected, selected.IsOnline));
        config = new DiskConfig
        {
            WarningAccepted = true, Language = settings.Language,
            EnableAutoIscsiLogin = true, ListenAddress = "127.0.0.1", ListenPort = settings.ListenPort,
            TargetIqn = HostConfig.Iqn, SectorsPerBlock = 4096 / selected.BytesPerSector,
            EnableMemoryCache = settings.EnableMemoryCache, MemoryCacheMaxBytes = settings.EnableWriteBack ? settings.ReadMemoryBudgetBytes : settings.MemoryCacheMaxBytes,
            EnableWriteBack = settings.EnableWriteBack, WriteJournalBytes = settings.JournalBudgetBytes,
            EnableAdaptivePrefetch = settings.EnableAdaptivePrefetch, PrefetchMaxBytes = settings.PrefetchMaxBytes, EnableSsdCache = true,
            SsdCachePath = SsdCacheService.GetDiskCacheDirectory(settings.CacheVolume, hash),
            SsdCacheMaxBytes = settings.EnableWriteBack ? settings.ReadCacheBudgetBytes : settings.CacheSizeBytes
        };
        RejectReparsePoints(config.SsdCachePath);
        if (!settings.EnableWriteBack && (File.Exists(WriteBackJournal.PathFor(config.SsdCachePath)) || File.Exists(WriteBackJournal.MarkerFor(config.SsdCachePath))))
            throw new IOException("Persistent write-back state exists; disabling it cannot bypass recovery.");
        if (!recoveryOnly)
        {
            bool container = File.Exists(Path.Combine(config.SsdCachePath, "cache.dat"));
            bool index = File.Exists(Path.Combine(config.SsdCachePath, "index.bin"));
            if (container != index && !settings.EnableWriteBack) throw new IOException("Incomplete cache pair; refusing to discard cache automatically.");
            long footprint = (container ? new FileInfo(Path.Combine(config.SsdCachePath, "cache.dat")).Length : 0) +
                (index ? new FileInfo(Path.Combine(config.SsdCachePath, "index.bin")).Length : 0) +
                (File.Exists(WriteBackJournal.PathFor(config.SsdCachePath)) ? new FileInfo(WriteBackJournal.PathFor(config.SsdCachePath)).Length : 0);
            // 上游槽位计算在查询失败时回退配置值；服务预检必须严格失败，不能因此放行满盘。
            long budget = checked(new DriveInfo(settings.CacheVolume).AvailableFreeSpace + footprint - (1L << 30));
            if (budget < settings.CacheSizeBytes + 4096) throw new IOException("Insufficient cache volume free space.");
            if (SsdCacheService.ComputeSlotCountFor(config) < SsdCacheService.ConfiguredSlotCountFor(config))
                throw new IOException("Insufficient SSD space after the 1 GiB reserve.");
        }
    }

    internal static void RejectReparsePoints(string path)
    {
        for (DirectoryInfo? dir = new(Path.GetFullPath(path)); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse-point directory is not permitted: " + dir.FullName);
    }

    private void AssertNoListener()
    {
        // 查询失败必须抛出，不能把“未知”当作“没有连接”。
        var network = IPGlobalProperties.GetIPGlobalProperties();
        if (network.GetActiveTcpListeners().Any(e => e.Port == settings.ListenPort) ||
            network.GetActiveTcpConnections().Any(c => c.LocalEndPoint.Port == settings.ListenPort && c.State == TcpState.Established))
            throw new IOException("The target port is still owned or connected; refusing physical disk restoration/takeover.");
    }

    public void Prepare(CancellationToken cancellationToken)
    {
        if (config == null) throw new InvalidOperationException("Preflight required.");
        cancellationToken.ThrowIfCancellationRequested();
        selected = Resolve();
        IscsiInitiator.CleanupStale(config.TargetIqn, config.ListenAddress, config.ListenPort);
        AssertNoExport();
        bool sourceWasOnline = selected.IsOnline;
        if (selected.IsOnline)
        {
            using var volumes = VolumeQuiesce.Lock(selected.DiskNumber);
            if (!PhysicalDiskHandle.TryOffline(selected.DiskNumber, out string offlineError)) throw new IOException(offlineError);
        }
        ReplayWriteBack(sourceWasOnline);
        if (settings.EnableWriteBack) ResetCleanReadCache();
        if (SsdCacheService.LedgerExists(config))
        {
            using var verifier = new L2Verifier(config, selected.DiskNumber);
            verifier.Open();
            var result = verifier.Verify(p => VerifyProgress = p, cancellationToken);
            Verification = result;
            if (!VerificationPassed(result))
            {
                if (!settings.RefreshChangedBlocks || !CanRefreshChangedBlocks(result))
                    throw new IOException("L2 verification failed: " + JsonSerializer.Serialize(result, StateStore.Json));
                // 正常还盘后 NTFS 会更新元数据；完整比对后仅刷新 L2，源盘句柄始终只读。
                var refresh = verifier.Repair(p => VerifyProgress = p, cancellationToken);
                CacheRefresh = refresh;
                if (refresh.Cancelled || refresh.ReadFailedBlocks != 0 || refresh.WriteFailedBlocks != 0 || refresh.RepairedBlocks != result.MismatchedBlocks)
                    throw new IOException("L2 refresh failed: " + JsonSerializer.Serialize(refresh, StateStore.Json));
            }
            cancellationToken.ThrowIfCancellationRequested();
            verifier.ConfirmLedger();
        }
        cancellationToken.ThrowIfCancellationRequested();
        // 先明确脱机，再重探测，让原生引擎从脱机状态起步，规避冗余路径问题。
        selected = Resolve();
        if (!PhysicalDiskHandle.TryOffline(selected.DiskNumber, out string error)) throw new IOException(error);
        selected = Resolve();
        if (selected.IsOnline) throw new IOException("Source disk did not become offline.");
    }

    public static bool VerificationPassed(L2VerifyResult result) => result.IsClean;

    public static bool CanRefreshChangedBlocks(L2VerifyResult r) => !r.Cancelled && !r.Aborted &&
        r.MismatchedBlocks > 0 && r.ReadFailedBlocks == 0 && r.OutOfRangeBlocks == 0 && r.DuplicateBlocks == 0 &&
        r.ScannedBlocks == r.PlannedBlocks && r.MatchedBlocks + r.MismatchedBlocks == r.PlannedBlocks;

    public void Start()
    {
        engine = new TargetService(config ?? throw new InvalidOperationException("Preflight required."));
        var result = engine.Start(selected ?? throw new InvalidOperationException("Prepare required."));
        if (!result.Ok) throw new IOException(result.Message);
    }

    public void Stop()
    {
        if (engine == null) return;
        var result = engine.Stop();
        if (!result.Ok || engine.IsRunning || engine.IsStopping) throw new IOException(result.Message);
    }

    public void Restore()
    {
        if (config == null) throw new InvalidOperationException("Preflight required.");
        if (engine is { IsRunning: true } or { IsStopping: true }) throw new IOException("Engine still owns the disk.");
        AssertNoListener();
        IscsiInitiator.CleanupStale(config.TargetIqn, config.ListenAddress, config.ListenPort);
        // Windows 可能异步移除克隆设备。最多等 10 秒，失败就保持脱机并明确报告。
        Exception? pending = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { selected = Resolve(); AssertNoExport(); pending = null; break; }
            catch (Exception ex) { pending = ex; Thread.Sleep(1000); }
        }
        if (pending != null) throw new IOException("Cannot prove the iSCSI clone is gone.", pending);
        ReplayWriteBack();
        if (settings.EnableWriteBack) ResetCleanReadCache();
        if (File.Exists(Path.Combine(config.SsdCachePath, "cache.dat")) && !SsdCacheService.InvalidateLedger(config))
            throw new IOException("Could not invalidate L2 before returning the source disk to Windows.");
        if (!PhysicalDiskHandle.TryOnline(selected!.DiskNumber, out string error)) throw new IOException(error);
        if (!Resolve().IsOnline) throw new IOException("Physical disk still offline after restoration.");
    }

    private void AssertNoExport()
    {
        AssertNoListener();
        // 原生枚举不能证明断线但残留的 iSCSI 设备已移除；由存储提供程序再核对。
        string script = "$ErrorActionPreference='Stop'; $service=Get-Service MSiSCSI; $s=@(); " +
            "if($service.Status -eq 'Running'){$s=@(Get-IscsiSession | Where-Object TargetNodeAddress -eq '" + HostConfig.Iqn + "')} " +
            "elseif($service.Status -ne 'Stopped'){throw 'MSiSCSI is transitioning'}; " +
            "[pscustomobject]@{Sessions=$s.Count;Disks=@(Get-Disk | Select-Object Number,SerialNumber,UniqueId,Size,BusType)} | ConvertTo-Json -Depth 4 -Compress";
        string output = External.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        using var json = JsonDocument.Parse(output);
        if (json.RootElement.GetProperty("Sessions").GetInt32() != 0) throw new IOException("iSCSI sessions remain.");
        string serial = selected!.SerialNumber.Trim();
        var matching = json.RootElement.GetProperty("Disks").EnumerateArray().Where(d =>
            (selected.VirtualDeviceId.Length > 0
                ? string.Equals((d.GetProperty("UniqueId").GetString() ?? "").Trim(), selected.VirtualDeviceId, StringComparison.OrdinalIgnoreCase) ||
                  (d.GetProperty("SerialNumber").GetString() ?? "").Trim() == serial
                : (d.GetProperty("SerialNumber").GetString() ?? "").Trim() == serial) &&
            d.GetProperty("Size").GetInt64() == settings.ExpectedSizeBytes).ToArray();
        if (matching.Length != 1 || matching[0].GetProperty("Number").GetInt32() != selected.DiskNumber ||
            IsIscsiBus(matching[0].GetProperty("BusType")))
            throw new IOException("Source/clone disk enumeration is ambiguous.");
    }

    public static bool IsIscsiBus(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetInt32() == 9,
        JsonValueKind.String => string.Equals(value.GetString(), "iSCSI", StringComparison.OrdinalIgnoreCase),
        _ => throw new IOException("Storage provider returned an unknown bus type.")
    };

    private void ReplayWriteBack(bool wasOnlineBeforePrepare = false)
    {
        if (config == null || !settings.EnableWriteBack) return;
        selected = Resolve();
        bool wasOnline = wasOnlineBeforePrepare || selected.IsOnline;
        if (selected.IsOnline)
        {
            // 不能在已挂载的旧文件系统上重放缓存元数据。
            using var volumes = VolumeQuiesce.Lock(selected.DiskNumber);
            if (!PhysicalDiskHandle.TryOffline(selected.DiskNumber, out string error)) throw new IOException(error);
        }
        using var raw = PhysicalDiskHandle.Open(selected.DiskNumber);
        var journal = new WriteBackJournal(raw, config.SsdCachePath,
            SsdCacheService.ComputeIdentity(raw.ToBlockSourceInfo()), settings.JournalBudgetBytes, false);
        try { journal.AssertRecoverySourceWasOffline(wasOnline); journal.Drain(); }
        finally { journal.CloseWithoutDrain(); } // 非后台恢复：失败必须保留日志且释放独占检查句柄。
    }

    private void ResetCleanReadCache()
    {
        // WAL 已完整重放并 Flush 后，HDD 才是权威副本。只删除可重建的读缓存，绝不删除 WAL。
        foreach (string name in new[] { "cache.dat", "index.bin", "index.tmp" })
            File.Delete(Path.Combine(config!.SsdCachePath, name));
    }

    public object? Snapshot() => new { Cache = engine?.CreateSnapshot(), WriteBack = engine?.WriteBackSnapshot,
        TotalMemoryBudgetBytes = settings.MemoryCacheMaxBytes, TotalSsdBudgetBytes = settings.CacheSizeBytes,
        VerificationProgress = VerifyProgress, Verification, CacheRefresh };
}

internal static class External
{
    public static string Run(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start " + executable);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // 不杀死磁盘相关工具；超时只报告，调用方不能据此继续恢复/脱机。
        if (!process.WaitForExit(60_000)) throw new TimeoutException(executable + " is still running.");
        if (process.ExitCode != 0) throw new IOException(executable + ": " + error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }
}
