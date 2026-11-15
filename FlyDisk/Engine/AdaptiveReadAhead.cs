// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
namespace FlyDisk.Engine;

/// <summary>Bounded sequential detector. All IO stays on the target's serialized worker.</summary>
public sealed class AdaptiveReadAhead
{
    private readonly int maximum;
    private readonly HashSet<long> pending = new();
    private long previousEnd = -1;
    private int streak, window = 65536, backoff;
    private long fetched, useful, wasted, failures;
    public long FetchedBlocks => Interlocked.Read(ref fetched);
    public long UsefulBlocks => Interlocked.Read(ref useful);
    public long WastedBlocks => Interlocked.Read(ref wasted);
    public long Failures => Interlocked.Read(ref failures);
    public int WindowBytes => Volatile.Read(ref window);
    public AdaptiveReadAhead(int maximumBytes) => maximum = Math.Clamp(maximumBytes, 65536, 1 << 20);
    public int Observe(long start, long end, Func<long, bool> cached)
    {
        int consumed = 0;
        foreach (long block in new List<long>(pending))
            if (block * 4096 < end && (block + 1) * 4096 > start)
            {
                pending.Remove(block);
                if (cached(block)) { consumed++; Interlocked.Increment(ref useful); }
                else Interlocked.Increment(ref wasted);
            }
        if (start == previousEnd)
        {
            streak++;
            if (consumed > 0 && streak % 4 == 0) window = Math.Min(maximum, window * 2);
        }
        else
        {
            Interlocked.Add(ref wasted, pending.Count); pending.Clear();
            streak = 1; window = 65536;
        }
        previousEnd = end;
        if (backoff > 0) { backoff--; return 0; }
        return streak >= 2 ? window : 0;
    }
    public void Record(long block)
    {
        if (pending.Count >= maximum / 4096)
        { Interlocked.Add(ref wasted, pending.Count); pending.Clear(); }
        if (pending.Add(block)) Interlocked.Increment(ref fetched);
    }
    public void Failed() { Interlocked.Increment(ref failures); backoff = 8; Reset(); }
    public void Reset() { previousEnd = -1; streak = 0; window = 65536; Interlocked.Add(ref wasted, pending.Count); pending.Clear(); }
}
