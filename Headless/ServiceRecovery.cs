// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FlyDisk.Headless;

internal static class ServiceRecovery
{
    [StructLayout(LayoutKind.Sequential)] struct RecoveryStep { public uint Type,Delay; }
    [StructLayout(LayoutKind.Sequential)] struct FailureActions {
        public uint ResetPeriod; public IntPtr RebootMessage,Command; public uint Count; public IntPtr Actions;
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenServiceW(IntPtr manager,string service,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool ChangeServiceConfig2W(IntPtr service,uint level,ref FailureActions info);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool ChangeServiceConfig2W(IntPtr service,uint level,ref int flag);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
    internal static void Configure(string name)
    {
        IntPtr manager=OpenSCManagerW(null,null,1);
        if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr service=IntPtr.Zero,steps=IntPtr.Zero;
        try{
            service=OpenServiceW(manager,name,0x12); // CHANGE_CONFIG + START, required for restart actions.
            if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            int size=Marshal.SizeOf<RecoveryStep>();steps=Marshal.AllocHGlobal(size*4);
            uint[] delays=[30000,60000,120000,0];
            for(int i=0;i<4;i++)Marshal.StructureToPtr(new RecoveryStep{Type=i==3?0u:1u,Delay=delays[i]},IntPtr.Add(steps,i*size),false);
            var actions=new FailureActions{ResetPeriod=3600,Count=4,Actions=steps};
            if(!ChangeServiceConfig2W(service,2,ref actions))throw new Win32Exception(Marshal.GetLastWin32Error());
            int flag=1;if(!ChangeServiceConfig2W(service,4,ref flag))throw new Win32Exception(Marshal.GetLastWin32Error());
        }finally{if(steps!=IntPtr.Zero)Marshal.FreeHGlobal(steps);if(service!=IntPtr.Zero)CloseServiceHandle(service);CloseServiceHandle(manager);}
    }
}
