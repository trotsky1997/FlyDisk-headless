// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace FlyDisk.Engine;

/// <summary>必须实际刷到稳定介质；不能以普通 Write 完成替代此契约。</summary>
public interface IDurableBlockSource : IBlockSource { void Flush(); }

/// <summary>
/// SSD 持久化后确认的有界环形 WAL。记录保持提交顺序，后台单消费者回写。
/// 双检查点 + SHA256；每次释放空间前两份检查点都持久化，防回退检查点引用已复用的记录。
/// 未提交尾部可忽略；已提交记录任何校验错误都拒绝恢复，不回源掩盖错误。
/// 不拥有底层句柄。Dispose 排空失败会抛出，调用者不得因此归还源盘。
/// </summary>
public sealed class WriteBackJournal : IDurableBlockSource
{
    const int Page = 4096, Start = Page * 2, MaxPayload = 1 << 20, MaxRecords = 65536;
    const long Magic = 0x314c41574b534446, RecordMagic = 0x315243574b534446;
    readonly object gate = new(), sourceGate = new();
    readonly IDurableBlockSource source;
    readonly FileStream file;
    readonly long identity, capacity;
    readonly string directory;
    readonly bool sparse;
    readonly LinkedList<Entry> entries = new();
    readonly int waitMilliseconds;
    Thread? worker;
    bool closing, disposed;
    Exception? failure;
    long head, tail, generation, nextSequence = 1, dirtyBytes, acknowledgedBytes, writtenBytes, waits, replayed;
    // 仅供模拟故障测试；生产不设置。异常视为设备故障，不吞掉。
    internal Action<string>? FaultPoint;
    sealed record Entry(long Position, long Span, long Sequence, long Offset, int Count, byte[] Hash);
    public int BytesPerSector => source.BytesPerSector;
    public long SizeBytes => source.SizeBytes;
    public bool HasPendingWrites { get { lock (gate) return entries.Count != 0; } }
    public static string PathFor(string directory) => Path.Combine(directory, "writeback.wal");
    public static string MarkerFor(string directory) => Path.Combine(directory, "writeback.required");

    public WriteBackJournal(IDurableBlockSource source, string directory, long identity, long budgetBytes,
        bool startWorker = true, int waitMilliseconds = 30000, bool sparse = false)
    {
        this.source = source; this.identity = identity; this.waitMilliseconds = waitMilliseconds; this.directory = directory; this.sparse = sparse;
        capacity = (budgetBytes - Start) / Page * Page;
        if (capacity < 4 * Page || budgetBytes % Page != 0) throw new ArgumentException("Invalid WAL budget.");
        Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, "writeback.online-conflict")))
            throw new IOException("Source was online with pending WAL; offline data reconciliation is required.");
        string path = PathFor(directory), marker = MarkerFor(directory);
        bool existing = File.Exists(path);
        if (!existing && File.Exists(marker)) throw new IOException("Required write-back journal is missing; source must stay offline.");
        file = new FileStream(path, existing ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite,
            // Use explicit durability barriers below. WriteThrough would force
            // payload, header and each checkpoint write separately, then flush
            // the same data again. No successful WRITE bypasses Flush(true).
            FileShare.None, 1, FileOptions.RandomAccess);
        try
        {
            if (sparse) SparseFile.Enable(file);
            if (existing) Load();
            else { file.SetLength(Start); Checkpoint(); }
            if (sparse) ReleaseFreeExtents();
            if (!File.Exists(marker))
            {
                using var m = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
                m.Write(System.Text.Encoding.UTF8.GetBytes($"WAL1 {identity:X16} {source.SizeBytes} {source.BytesPerSector}\n")); m.Flush(true);
            }
            if (startWorker) StartWorker();
        }
        catch { file.Dispose(); throw; }
    }

    static long Align(long n) => checked((n + Page - 1) / Page * Page);
    // Only free space outside the checkpointed live interval. In particular,
    // recovery must validate all committed records before calling this method.
    void ReleaseFreeExtents()
    {
        if (head == tail) { SparseFile.Release(file, Start, file.Length); return; }
        if (head - tail == capacity) return;
        long first = tail % capacity, last = head % capacity;
        if (first < last)
        {
            SparseFile.Release(file, Start, Start + first);
            SparseFile.Release(file, Start + last, Start + capacity);
        }
        else SparseFile.Release(file, Start + last, Start + first);
    }
    void At(long at, byte[] data) { file.Position = at; file.Write(data); }
    byte[] ReadAt(long at, int count) { byte[] b = new byte[count]; file.Position = at; file.ReadExactly(b); return b; }
    static void Put(byte[] b, int at, long value) => BitConverter.GetBytes(value).CopyTo(b, at);
    static long Get(byte[] b, int at) => BitConverter.ToInt64(b, at);
    static void Seal(byte[] b) => SHA256.HashData(b.AsSpan(0, Page - 32)).CopyTo(b.AsSpan(Page - 32));
    static bool Valid(byte[] b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(b.AsSpan(0, Page - 32)), b.AsSpan(Page - 32));

    void Checkpoint()
    {
        byte[] b = new byte[Page];
        Put(b, 0, Magic); Put(b, 8, ++generation); Put(b, 16, identity); Put(b, 24, SizeBytes);
        Put(b, 32, BytesPerSector); Put(b, 40, capacity); Put(b, 48, head); Put(b, 56, tail); Put(b, 64, nextSequence);
        Seal(b);
        // 两份分别 Flush，只有两份都落稳后才能复用旧区间。
        At((generation & 1) * Page, b); file.Flush(true); FaultPoint?.Invoke("checkpoint-first");
        At((1 - (generation & 1)) * Page, b); file.Flush(true); FaultPoint?.Invoke("checkpoint-second");
    }

    void Load()
    {
        if (file.Length < Start || file.Length > capacity + Start) throw new IOException("Invalid WAL length.");
        var headers = new[] { ReadAt(0, Page), ReadAt(Page, Page) }.Where(b => Valid(b) && Get(b, 0) == Magic).ToArray();
        if (headers.Length == 0) throw new IOException("Both WAL checkpoints are corrupt.");
        var b = headers.OrderByDescending(b => Get(b, 8)).First();
        if (Get(b, 16) != identity || Get(b, 24) != SizeBytes || Get(b, 32) != BytesPerSector || Get(b, 40) != capacity)
            throw new IOException("WAL identity/geometry/budget mismatch; refusing reinterpretation.");
        generation = Get(b, 8); head = Get(b, 48); tail = Get(b, 56); nextSequence = Get(b, 64);
        if (generation <= 0 || tail < 0 || head < tail || head - tail > capacity || head % Page != 0 || tail % Page != 0 || nextSequence < 1)
            throw new IOException("Invalid WAL checkpoint bounds.");
        long p = tail, previous = 0;
        while (p < head)
        {
            byte[] h = ReadAt(Start + p % capacity, Page);
            if (!Valid(h) || Get(h, 0) != RecordMagic || Get(h, 8) != p) throw new IOException("Committed WAL header corrupt.");
            long span = Get(h, 16), seq = Get(h, 24), offset = Get(h, 32), count = Get(h, 40);
            if (span < Page || span % Page != 0 || span > head - p || span > capacity - p % capacity)
                throw new IOException("Invalid WAL extent.");
            if (count == 0)
            {
                if (span != capacity - p % capacity || offset != -1 || seq != 0) throw new IOException("Invalid WAL wrap marker.");
            }
            else
            {
                if (count < 0 || count > MaxPayload || span != Page + Align(count) || offset < 0 || offset > SizeBytes - count ||
                    count % BytesPerSector != 0 || offset % BytesPerSector != 0 || seq <= previous || seq >= nextSequence)
                    throw new IOException("Invalid committed WAL record.");
                var e = new Entry(p, span, seq, offset, (int)count, h.AsSpan(48, 32).ToArray());
                ReadPayload(e); // 全部校验通过后，调用方才准许重放/暴露磁盘。
                entries.AddLast(e); dirtyBytes += count; previous = seq;
                if (entries.Count > MaxRecords) throw new IOException("WAL record count exceeds bound.");
            }
            p = checked(p + span);
        }
    }

    byte[] ReadPayload(Entry e)
    {
        byte[] b = ReadAt(Start + e.Position % capacity + Page, e.Count);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(b), e.Hash)) throw new IOException("Committed WAL payload checksum mismatch.");
        return b;
    }

    void Check()
    {
        if (disposed || closing) throw new IOException("Write-back journal is closing.");
        if (failure != null) throw new IOException("Write-back journal faulted; acknowledged data retained.", failure);
    }
    void ValidateRange(long offset, byte[] b, int start, int count)
    {
        if (offset < 0 || count < 0 || offset > SizeBytes - count || start < 0 || start > b.Length - count ||
            offset % BytesPerSector != 0 || count % BytesPerSector != 0) throw new ArgumentOutOfRangeException(nameof(offset));
    }

    public void Write(long offset, byte[] b, int start, int count)
    {
        ValidateRange(offset, b, start, count);
        for (int done = 0; done < count;)
        {
            int n = Math.Min(MaxPayload, count - done);
            Append(offset + done, b.AsSpan(start + done, n).ToArray()); done += n;
        }
    }

    void Append(long offset, byte[] data)
    {
        lock (gate)
        {
            Check(); long span = Page + Align(data.Length);
            if (span > capacity) throw new IOException("Write exceeds journal capacity.");
            long deadline = Environment.TickCount64 + waitMilliseconds;
            while (true)
            {
                long remainder = capacity - head % capacity;
                long padding = remainder < span ? remainder : 0;
                if (entries.Count == 0 && head == tail && padding != 0)
                {
                    // 空环可以整体越过尾部，不产生不可回收的孤立 padding。
                    try { head = tail = checked(head + padding); Checkpoint(); padding = 0; }
                    catch (Exception ex) { failure = ex; throw new IOException("WAL wrap checkpoint failed.", ex); }
                }
                if (capacity - (head - tail) >= span + padding && entries.Count < MaxRecords) break;
                waits++;
                int left = (int)Math.Clamp(deadline - Environment.TickCount64, 0, waitMilliseconds);
                if (left == 0) throw new IOException("Write-back journal full: backpressure timeout; no unpersisted write acknowledged.");
                Monitor.PulseAll(gate); Monitor.Wait(gate, Math.Min(left, 100)); Check();
            }
            try
            {
                long remainder = capacity - head % capacity;
                if (remainder < span)
                {
                    byte[] wrap = Header(head, remainder, 0, -1, 0, new byte[32]);
                    At(Start + head % capacity, wrap); head = checked(head + remainder);
                }
                var e = new Entry(head, span, nextSequence++, offset, data.Length, SHA256.HashData(data));
                At(Start + head % capacity + Page, data);
                At(Start + head % capacity, Header(head, span, e.Sequence, offset, data.Length, e.Hash));
                file.Flush(true); FaultPoint?.Invoke("record-durable");
                head = checked(head + span); Checkpoint();
                entries.AddLast(e); dirtyBytes += data.Length; acknowledgedBytes += data.Length;
                Monitor.PulseAll(gate);
            }
            catch (Exception ex) { failure = ex; Monitor.PulseAll(gate); throw new IOException("WAL commit failed; recovery required.", ex); }
        }
    }

    static byte[] Header(long position, long span, long seq, long offset, int count, byte[] hash)
    {
        byte[] b = new byte[Page]; Put(b, 0, RecordMagic); Put(b, 8, position); Put(b, 16, span);
        Put(b, 24, seq); Put(b, 32, offset); Put(b, 40, count); hash.CopyTo(b, 48); Seal(b); return b;
    }

    public void Read(long offset, byte[] b, int start, int count)
    {
        ValidateRange(offset, b, start, count);
        lock (gate)
        {
            Check();
            // A fully journal-covered read must not wait behind an unrelated
            // HDD destage/flush. Hold gate throughout coverage + overlay so no
            // contributing record can be reclaimed between the two passes.
            long covered = offset, end = offset + count;
            var ranges = new List<(long From, long To)>();
            foreach (var e in entries)
            {
                long from = Math.Max(offset, e.Offset), to = Math.Min(end, e.Offset + e.Count);
                if (from < to) ranges.Add((from, to));
            }
            ranges.Sort((a,b) => a.From.CompareTo(b.From));
            foreach (var range in ranges)
            {
                if (range.From > covered) break;
                covered = Math.Max(covered, range.To);
                if (covered == end) break;
            }
            // 持 gate 防止回收记录；sourceGate 防止原始句柄共享文件指针竞争。
            if (covered != end) { lock (sourceGate) source.Read(offset, b, start, count); }
            foreach (var e in entries)
            {
                long from = Math.Max(offset, e.Offset), to = Math.Min(offset + count, e.Offset + e.Count);
                if (from >= to) continue;
                byte[] data = ReadPayload(e);
                Buffer.BlockCopy(data, (int)(from - e.Offset), b, start + (int)(from - offset), (int)(to - from));
            }
        }
    }

    public void StartWorker()
    {
        lock (gate)
        {
            Check(); if (worker != null) return;
            worker = new Thread(Worker) { IsBackground = true, Name = "FlyDisk WAL destager" }; worker.Start();
        }
    }
    public void AssertRecoverySourceWasOffline(bool wasOnline)
    {
        lock (gate)
        {
            if (!wasOnline || entries.Count == 0) return;
            using var marker = new FileStream(Path.Combine(directory,"writeback.online-conflict"),FileMode.Create,
                FileAccess.Write,FileShare.None,1,FileOptions.WriteThrough);
            marker.Write(System.Text.Encoding.UTF8.GetBytes("Pending WAL was found after the source had been online. Do not replay automatically.\n"));
            marker.Flush(true);
            failure = new IOException("Pending WAL source was online; refusing to overwrite possible external changes.");
            throw (IOException)failure;
        }
    }
    void Worker()
    {
        try
        {
            while (true)
            {
                lock (gate)
                {
                    while (!closing && failure == null && entries.Count == 0) Monitor.Wait(gate);
                    if (closing || failure != null) return;
                }
                DrainBatch(64);
            }
        }
        catch (Exception ex) { lock (gate) { failure = ex; Monitor.PulseAll(gate); } }
    }

    // 单消费者；启动重放时不启动后台线程。
    internal bool DrainOne() => DrainBatch(1);
    bool DrainBatch(int limit)
    {
        var batch = new List<(Entry Entry, byte[] Data)>();
        lock (gate)
        {
            Check(); int bytes = 0;
            foreach (var entry in entries)
            {
                if (batch.Count >= limit || (batch.Count > 0 && bytes + entry.Count > MaxPayload)) break;
                batch.Add((entry, ReadPayload(entry))); bytes += entry.Count;
            }
            if (batch.Count == 0) return false;
        }
        try
        {
            lock (sourceGate)
            {
                // 相邻写合并，所有写仍保持日志顺序；一个批次只刷一次 HDD。
                for (int i = 0; i < batch.Count;)
                {
                    int end = i + 1, bytes = batch[i].Entry.Count;
                    while (end < batch.Count && batch[end].Entry.Offset == batch[i].Entry.Offset + bytes)
                        bytes += batch[end++].Entry.Count;
                    byte[] data = batch[i].Data;
                    if (end != i + 1)
                    {
                        data = new byte[bytes]; int at = 0;
                        for (int k = i; k < end; k++) { batch[k].Data.CopyTo(data, at); at += batch[k].Data.Length; }
                    }
                    source.Write(batch[i].Entry.Offset, data, 0, data.Length); i = end;
                }
                source.Flush();
            }
            FaultPoint?.Invoke("source-durable");
            lock (gate)
            {
                if (entries.First?.Value != batch[0].Entry) throw new IOException("Multiple WAL consumers are forbidden.");
                var following = entries.First;
                for (int n = 0; n < batch.Count; n++) following = following!.Next;
                tail = following?.Value.Position ?? head;
                Checkpoint(); // 必须先持久化回收位置，再允许生产者复用空间。
                // Include the entire free interval so partial filesystem
                // allocation units from adjacent retired batches are reclaimed
                // once the full unit is free. Never touch the live interval.
                if (sparse) ReleaseFreeExtents();
                foreach (var item in batch) { entries.RemoveFirst(); dirtyBytes -= item.Entry.Count; writtenBytes += item.Entry.Count; replayed++; }
                Monitor.PulseAll(gate);
            }
            return true;
        }
        catch (Exception ex) { lock (gate) { failure = ex; Monitor.PulseAll(gate); } throw; }
    }

    public void Flush()
    {
        lock (gate) { Check(); file.Flush(true); }
        // 每笔成功写已经持久化在 SSD；SCSI FUA/Flush 不要求提前回收该稳定介质上的日志。
    }
    public void Drain()
    {
        if (worker == null) { while (DrainBatch(64)) { } return; }
        lock (gate) { while (entries.Count != 0) { Check(); Monitor.PulseAll(gate); Monitor.Wait(gate, 100); } Check(); }
    }
    public object Snapshot() { lock (gate) return new { Enabled = true, JournalBudgetBytes = capacity + Start, DirtyBytes = dirtyBytes,
        RingUsedBytes = head - tail, PendingRecords = entries.Count, AcknowledgedBytes = acknowledgedBytes,
        DestagedBytes = writtenBytes, BackpressureWaits = waits, ReplayedRecords = replayed, Error = failure?.Message }; }
    public void Dispose()
    {
        if (disposed) return; Drain(); CloseWithoutDrain();
    }
    internal void CloseWithoutDrain() // 仅用于模拟进程崩溃；线上释放必须用 Dispose。
    {
        lock (gate) { closing = true; Monitor.PulseAll(gate); }
        worker?.Join(); lock (gate) { file.Dispose(); disposed = true; }
    }
}
