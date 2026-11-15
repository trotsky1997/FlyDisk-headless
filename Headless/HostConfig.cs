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
        if (!RiskAccepted) throw new ArgumentException("Set RiskAccepted=true after reviewing the whole-disk takeover behavior.");
    }

    public static HostConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Empty configuration.");
        config.Validate();
        return config;
    }
}
