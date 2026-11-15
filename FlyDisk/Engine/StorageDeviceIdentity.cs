// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace FlyDisk.Engine;

/// <summary>严格解析 VPD83 的设备关联 NAA/EUI 标识；不使用盘号、容量或分区签名作为身份。</summary>
public static class StorageDeviceIdentity
{
    public static string ReadBinaryDeviceId(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 12) return "";
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
        if (size > descriptor.Length || size < 12 || count is 0 or > 256) return "";
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        int position = 12;
        for (int i = 0; i < count; i++)
        {
            if (position > size - 16) return "";
            var current = descriptor[position..(int)size];
            uint codeSet = BinaryPrimitives.ReadUInt32LittleEndian(current);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(current[4..]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(current[8..]);
            ushort next = BinaryPrimitives.ReadUInt16LittleEndian(current[10..]);
            uint association = BinaryPrimitives.ReadUInt32LittleEndian(current[12..]);
            if (length > current.Length - 16) return "";
            if (codeSet == 1 && association == 0 && type is 2 or 3 && length is 8 or 16)
            {
                var identifier = current.Slice(16, length);
                if (identifier.IndexOfAnyExcept((byte)0) >= 0) candidates.Add(Convert.ToHexString(identifier));
            }
            if (i + 1 < count)
            {
                if (next < 16 + length || next > current.Length - 16) return "";
                position += next;
            }
        }
        return candidates.FirstOrDefault() ?? "";
    }
}
