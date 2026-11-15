// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using ISCSI;
using ISCSI.Client;
using ISCSI.Server;
using System.Net;
using System.Net.Sockets;
using FlyDisk.Engine;
using FlyDisk.Models;

static class IscsiFailureTests
{
    internal static void ProbeRunningTarget()
    {
        var client=new ISCSIClient("iqn.flydisk:reset-crash-acceptance");
        try{
            if(!client.Connect(IPAddress.Loopback,3260,5000)||!client.Login("iqn.flydisk:headless-disk"))throw new IOException("Cannot log in to isolated target.");
            byte[] before=client.Read(0,0,8,512);
            client.SendPDU(new ColdResetRequest());
            var response=client.WaitForPDU(0xabc) as RejectPDU;
            if(response==null || response.Reason!=RejectReason.CommandNotSupported || !client.PingTarget() || !client.Read(0,0,8,512).SequenceEqual(before))
                throw new IOException("Reset probe changed disk access.");
            Console.WriteLine("PASS 0x02 cold-reset request rejected; target remains readable before explicit crash injection");
        }finally{client.Disconnect();}
    }
    public static void Run(Action<string,Action> test,Action<bool,string> check)
    {
        test("ISCSI 1.5.6 rejects cold reset but does not immediately close a healthy session",()=>{
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            var cfg=new DiskConfig{EnableMemoryCache=false,EnableSsdCache=false};
            var source=new FakeSource(1<<20,512);
            var cache=new CacheService(cfg,new BlockSourceInfo{Model="TEST",SerialNumber="TMF",SizeBytes=source.SizeBytes,BytesPerSector=512});
            var disk=new CachedPhysicalDisk(source,cache);
            var server=new ISCSIServer();string iqn="iqn.flydisk:test-reset";
            server.AddTarget(new ISCSITarget(iqn,new List<DiskAccessLibrary.Disk>{disk}));
            var client=new ISCSIClient("iqn.flydisk:test-initiator");
            try{
                server.Start(new IPEndPoint(IPAddress.Loopback,port));
                check(client.Connect(IPAddress.Loopback,port,3000)&&client.Login(iqn),"Synthetic login failed");
                client.SendPDU(new ColdResetRequest());
                var rejected=client.WaitForPDU(0xabc) as RejectPDU;
                check(rejected!=null && rejected.Reason==RejectReason.CommandNotSupported,"Reset was not rejected by baseline dependency");
                check(client.PingTarget(),"Unsupported PDU immediately destroyed the session");
                check(client.Read(0,0,8,512).SequenceEqual(source.Data.Take(4096)),"Read failed after rejected reset");
            }finally{client.Disconnect();server.Stop();disk.Close();cache.Release();}
        });
    }
    sealed class ColdResetRequest : ISCSIPDU
    {
        public ColdResetRequest(){OpCode=(ISCSIOpCodeName)2;ImmediateDelivery=true;Final=true;OpCodeSpecificHeader[0]=7;InitiatorTaskTag=0xabc;}
    }
}
