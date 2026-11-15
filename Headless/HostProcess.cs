// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace FlyDisk.Headless;

internal sealed class HostProcess(HostConfig config) : IHeadlessHost, IDisposable
{
    private FileStream? owner;
    private Timer? timer;
    private Lifecycle? lifecycle;
    private readonly CancellationTokenSource cancellation = new();

    internal static FileStream AcquireOwnership()
    {
        ServiceManager.SecureDirectory(ServiceManager.DataDirectory, false);
        // 文件句柄锁不依赖线程，SCM 的不同回调线程可以安全共享；进程退出时自动释放。
        return new FileStream(Path.Combine(ServiceManager.DataDirectory, "owner.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public void Start()
    {
        owner = AcquireOwnership();
        try
        {
            var backend = new NativeBackend(config);
            var store = new StateStore(ServiceManager.DataDirectory, config);
            store.AssertReadyForStart(config.EnableWriteBack);
            lifecycle = new Lifecycle(backend, (phase, error, recovery) => store.Write(phase, error, recovery));
            timer = new Timer(_ =>
            {
                try { store.Heartbeat(backend.Snapshot, () => new { VerificationProgress = backend.VerifyProgress, backend.Verification, backend.CacheRefresh }); }
                catch (Exception ex) { FlyDisk.Engine.LogService.DebugFile("headless heartbeat: " + ex); }
            }, null, 5000, 5000);
            lifecycle.Start(cancellation.Token);
        }
        catch { timer?.Dispose(); timer = null; owner.Dispose(); owner = null; throw; }
    }

    public void CancelStartup() => cancellation.Cancel();

    public void Stop()
    {
        // Write(stopping) 与 Heartbeat 共用锁，确保快照不会与后端释放交叉。
        lifecycle?.Stop();
        timer?.Dispose(); timer = null;
        owner?.Dispose(); owner = null;
    }

    public void Dispose()
    {
        timer?.Dispose();
        owner?.Dispose();
        cancellation.Dispose();
    }
}
