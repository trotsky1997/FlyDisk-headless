// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlyDisk.Engine;

internal static class SparseFile
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern uint GetCompressedFileSizeW(string path,out uint high);
    internal static long AllocatedBytes(string path)
    {
        Marshal.SetLastPInvokeError(0);
        uint low=GetCompressedFileSizeW(path,out uint high);
        if(low==uint.MaxValue && Marshal.GetLastPInvokeError()!=0)throw new Win32Exception(Marshal.GetLastPInvokeError());
        return checked((long)(((ulong)high << 32) | low));
    }
    const uint SetSparse = 0x000900c4, SetZeroData = 0x000980c8;
    [StructLayout(LayoutKind.Sequential)] struct ZeroRange { public long Start, End; }
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle file,uint code,IntPtr input,uint inputSize,IntPtr output,uint outputSize,out uint returned,IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle file,uint code,ref ZeroRange input,uint inputSize,IntPtr output,uint outputSize,out uint returned,IntPtr overlap);
    internal static void Enable(FileStream file)
    {
        if(!DeviceIoControl(file.SafeFileHandle,SetSparse,IntPtr.Zero,0,IntPtr.Zero,0,out _,IntPtr.Zero))
            throw new IOException("Sparse cache files are required on this volume.",new Win32Exception(Marshal.GetLastWin32Error()));
    }
    internal static void Release(FileStream file,long start,long end)
    {
        end=Math.Min(end,file.Length);
        if(start>=end)return;
        var range=new ZeroRange{Start=start,End=end};
        if(!DeviceIoControl(file.SafeFileHandle,SetZeroData,ref range,16,IntPtr.Zero,0,out _,IntPtr.Zero))
            throw new IOException("Sparse extent release failed.",new Win32Exception(Marshal.GetLastWin32Error()));
    }
}
