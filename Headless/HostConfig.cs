// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using FlyDisk.Models;

namespace FlyDisk.Headless;

public sealed record HostConfig
{
    public int SchemaVersion { get; init; } = 1;
    public string DiskIdentity { get; init; } = "";
    public long ExpectedSizeBytes { get; init; }
    public string CacheVolume { get; init; } = "";
    public long CacheSizeBytes { get; init; } = 32L << 30;
    public bool EnableMemoryCache { get; init; }
    public bool EnableWriteBack { get; init; }
    // 总预算包含日志、读缓存和元数据；不给写缓冲额外叠加一份容量。
    public long JournalBudgetBytes => CacheSizeBytes / 2 / 4096 * 4096;
    public long ReadCacheBudgetBytes => (CacheSizeBytes - JournalBudgetBytes - Math.Max(64L << 20, CacheSizeBytes / 128)) / (64L << 20) * (64L << 20);
    public long ReadMemoryBudgetBytes => Math.Max(64L << 20,
        (long)((MemoryCacheMaxBytes - (128L << 20) - ReadCacheBudgetBytes / 4096 * 128) / 1.0625) / (64L << 20) * (64L << 20));
    public long MemoryCacheMaxBytes { get; init; } = 2L << 30;
    public bool EnableAdaptivePrefetch { get; init; }
    public int PrefetchMaxBytes { get; init; } = 1 << 20;
    public bool RefreshChangedBlocks { get; init; } = true;
    public bool RiskAccepted { get; init; }
    public string Language { get; init; } = "zh-CN";
    public int ListenPort { get; init; } = 3260;
    public const string Iqn = "iqn.flydisk:headless-disk";

    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(DiskIdentity) ||
            !DiskIdentity.Contains('|') || string.IsNullOrWhiteSpace(DiskIdentity.Split('|').Last()) ||
            ExpectedSizeBytes <= 0 || ExpectedSizeBytes % 512 != 0)
            throw new ArgumentException("Invalid schema, stable disk identity or exact disk capacity.");
        if (CacheVolume.Length != 2 || !char.IsAsciiLetter(CacheVolume[0]) || CacheVolume[1] != ':')
            throw new ArgumentException("CacheVolume must be a drive letter such as C:.");
        // Bound the index allocation as well as the container size.
        if (CacheSizeBytes < ServiceConstants.SlabSize || CacheSizeBytes > (1L << 40) ||
            CacheSizeBytes % ServiceConstants.SlabSize != 0)
            throw new ArgumentException("CacheSizeBytes must be 64 MiB aligned, between 64 MiB and 1 TiB.");
        if (ListenPort is < 1024 or > 65535) throw new ArgumentException("Invalid unprivileged iSCSI port.");
        if (Language is not ("zh-CN" or "en-US")) throw new ArgumentException("Language must be zh-CN or en-US.");
        if (MemoryCacheMaxBytes < (64L << 20) || MemoryCacheMaxBytes > (16L << 30) || MemoryCacheMaxBytes % (64L << 20) != 0)
            throw new ArgumentException("Memory cache must be 64 MiB aligned, between 64 MiB and 16 GiB.");
        if (PrefetchMaxBytes < 65536 || PrefetchMaxBytes > (1 << 20) || PrefetchMaxBytes % 4096 != 0)
            throw new ArgumentException("Prefetch limit must be 4 KiB aligned, from 64 KiB to 1 MiB.");
        if (EnableAdaptivePrefetch && !EnableMemoryCache) throw new ArgumentException("Adaptive prefetch requires bounded L1 cache.");
        if (!RiskAccepted) throw new ArgumentException("Set RiskAccepted=true after reviewing the whole-disk takeover behavior.");
        if (EnableWriteBack && (CacheSizeBytes < (256L << 20) || MemoryCacheMaxBytes < (512L << 20) ||
            MemoryCacheMaxBytes < ReadCacheBudgetBytes / 4096 * 128 + (256L << 20)))
            throw new ArgumentException("Write-back needs sufficient total memory for bounded indexes and journal buffers.");
    }

    public static HostConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Empty configuration.");
        config.Validate();
        return config;
    }
}
