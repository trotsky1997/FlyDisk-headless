// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
namespace FlyDisk.Headless;

/// <summary>可注入后端，故障回归测试不需要真实硬盘或管理员权限。</summary>
public interface IAcceleratorBackend
{
    void Preflight();
    void Prepare(CancellationToken cancellationToken);
    void Start();
    void Stop();
    void Restore();
    object? Snapshot();
}

public sealed class Lifecycle(IAcceleratorBackend backend, Action<string, string?, bool> checkpoint)
{
    private readonly object gate = new();
    public string Phase { get; private set; } = "idle";
    public bool RecoveryRequired { get; private set; }

    private void Set(string phase, string? error = null)
    {
        Phase = phase;
        checkpoint(phase, error, RecoveryRequired);
    }

    public void Start(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (Phase != "idle") throw new InvalidOperationException("Lifecycle has already started.");
            Set("preflight");
            try { backend.Preflight(); cancellationToken.ThrowIfCancellationRequested(); }
            catch (Exception ex) { Set("failed", ex.Message); throw; }
            // 持久化检查点必须先于任何脱机动作；失败就绝不触盘。
            RecoveryRequired = true;
            Set("preparing");
            try
            {
                backend.Prepare(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Set("mounting");
                backend.Start();
                Set("running");
            }
            catch (Exception startError)
            {
                try
                {
                    backend.Stop(); // 拒绝停止时绝不继续联机原盘。
                    backend.Restore();
                    RecoveryRequired = false;
                }
                catch (Exception recoveryError)
                {
                    Set("recovery-required", startError.Message + " | recovery: " + recoveryError.Message);
                    throw new AggregateException(startError, recoveryError);
                }
                Set("failed-restored", startError.Message);
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!RecoveryRequired) return;
            Set("stopping");
            try { backend.Stop(); }
            catch (Exception ex) { Set("stop-refused", ex.Message); throw; }
            try
            {
                backend.Restore();
                RecoveryRequired = false;
                Set("stopped");
            }
            catch (Exception ex) { Set("recovery-required", ex.Message); throw; }
        }
    }

    public void Recover()
    {
        lock (gate)
        {
            backend.Preflight();
            RecoveryRequired = true;
            Set("recovering");
            try { backend.Restore(); RecoveryRequired = false; Set("stopped"); }
            catch (Exception ex) { Set("recovery-required", ex.Message); throw; }
        }
    }
}
