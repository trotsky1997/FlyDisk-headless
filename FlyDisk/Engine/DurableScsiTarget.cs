// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.IO;
using DiskAccessLibrary;
using SCSI;

namespace FlyDisk.Engine;

/// <summary>上游 SYNCHRONIZE CACHE 是空操作；WAL 模式显式检查并刷稳定 SSD 日志。</summary>
public sealed class DurableScsiTarget(CachedPhysicalDisk disk, WriteBackJournal journal)
    : VirtualSCSITarget(new List<Disk> { disk })
{
    public override SCSIStatusCodeName ExecuteCommand(byte[] command, LUNStructure lun, byte[] data, out byte[] response)
    {
        // 每条 WRITE（包含 FUA）本身已经持久化，不依赖库解析 FUA 位。
        if (command.Length > 0 && command[0] is 0x35 or 0x91 && (ushort)lun == 0)
        {
            if (command.Length < (command[0] == 0x35 ? 10 : 16))
            {
                response = FormatSenseData(SenseDataParameter.GetIllegalRequestUnsupportedCommandCodeSenseData());
                return SCSIStatusCodeName.CheckCondition;
            }
            try { journal.Flush(); response = System.Array.Empty<byte>(); return SCSIStatusCodeName.Good; }
            catch (IOException) { response = FormatSenseData(SenseDataParameter.GetMediumErrorWriteFaultSenseData()); return SCSIStatusCodeName.CheckCondition; }
        }
        return base.ExecuteCommand(command, lun, data, out response);
    }
}
