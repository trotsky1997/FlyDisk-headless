// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

namespace FlyDisk.Headless;

public sealed record HostState(DateTimeOffset UpdatedUtc, int Pid, long ProcessStartUtcTicks,
    string DiskIdentity, long ExpectedSizeBytes, string Phase, string? Error, bool RecoveryRequired, object? Stats,
    string? ConfigurationHash = null);

public sealed class StateStore(string directory, HostConfig config)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private readonly object gate = new();
    private readonly long processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private HostState? last;
    public string PathName => Path.Combine(directory, "status.json");

    public static string ConfigHash(HostConfig value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    public void AssertRecoveryConfigMatches()
    {
        if (!File.Exists(PathName)) return;
        var state = JsonSerializer.Deserialize<HostState>(File.ReadAllText(PathName), Json) ?? throw new IOException("Unreadable recovery checkpoint.");
        if (state.RecoveryRequired && (state.DiskIdentity != config.DiskIdentity || state.ExpectedSizeBytes != config.ExpectedSizeBytes ||
            state.ConfigurationHash != ConfigHash(config)))
            throw new IOException("Pending recovery belongs to another configuration. Restore its original config and recover before changing disks or cache settings.");
    }

    public void AssertReadyForStart() => AssertReadyForStart(false);
    public void AssertReadyForStart(bool allowJournalRecovery)
    {
        AssertRecoveryConfigMatches();
        if (File.Exists(PathName) && JsonSerializer.Deserialize<HostState>(File.ReadAllText(PathName), Json) is { RecoveryRequired: true } state)
        {
            if (!allowJournalRecovery || !config.EnableWriteBack || IsAlive(state))
                throw new IOException("An unfinished takeover is recorded. Run recover before starting; the recovery checkpoint has been preserved.");
            // 配置完全匹配且旧进程已死；Prepare 必须重放 WAL，再允许 export。
        }
    }

    public void Write(string phase, string? error, bool recoveryRequired, object? stats = null)
    {
        lock (gate)
        {
            var state = new HostState(DateTimeOffset.UtcNow, Environment.ProcessId, processStart,
                config.DiskIdentity, config.ExpectedSizeBytes, phase, error, recoveryRequired, stats, ConfigHash(config));
            string temporary = PathName + ".tmp";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, PathName, true);
            last = state;
        }
    }

    public void Heartbeat(Func<object?> snapshot, Func<object?>? preparingSnapshot = null, Func<string?>? health = null)
    {
        lock (gate)
        {
            // Snapshot is allowed only while the backend is stable, not while its handles are disposed.
            if (last?.Phase is "running" or "degraded")
            {
                object? stats = snapshot();
                string? error = health?.Invoke();
                Write(error == null ? "running" : "degraded", error, last.RecoveryRequired, stats);
            }
            else if (last?.Phase == "preparing" && preparingSnapshot != null)
                Write(last.Phase, last.Error, last.RecoveryRequired, preparingSnapshot());
        }
    }

    public static bool IsAlive(HostState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == state.ProcessStartUtcTicks;
        }
        catch { return false; }
    }
}
