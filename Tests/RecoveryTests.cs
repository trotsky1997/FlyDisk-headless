// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using FlyDisk.Headless;
using System.Text.Json;

static class RecoveryTests
{
    internal static void Run(Action<string,Action> test,Action<bool,string> check)
    {
        var clean=new RecoveryObservation("Stopped","stopped",false,false,false,false,true,0);
        test("Supervisor respects clean manual stop and transitional service states",()=>{
            check(RecoverySupervisor.Decide(clean)==RecoveryAction.None,"Manual stop was overridden");
            check(RecoverySupervisor.Decide(clean with{Service="StopPending",PendingIntent=true})==RecoveryAction.Wait,"Stop-pending was forcibly bypassed");
            check(RecoverySupervisor.Decide(clean with{Service="Stopped",ProcessAlive=true,RecoveryRequired=true})==RecoveryAction.Wait,"Live old owner was bypassed");
        });
        test("Supervisor restarts crashed owners through normal WAL startup and resumes its own interrupted stop",()=>{
            check(RecoverySupervisor.Decide(clean with{RecoveryRequired=true})==RecoveryAction.Start,"Crash was not scheduled for journal startup");
            check(RecoverySupervisor.Decide(clean with{PendingIntent=true})==RecoveryAction.Start,"Interrupted recovery intent was lost");
        });
        test("Supervisor detects live but unhealthy exports and opens a bounded circuit",()=>{
            var bad=clean with{Service="Running",Phase="degraded",ProcessAlive=true,RecoveryRequired=true,Unhealthy=true};
            check(RecoverySupervisor.Decide(bad)==RecoveryAction.Restart,"Live missing export not recovered");
            check(RecoverySupervisor.Decide(bad with{Attempts=3})==RecoveryAction.ReturnNative,"Circuit did not return native access");
            check(RecoverySupervisor.Decide(clean with{RecoveryRequired=true,Attempts=3})==RecoveryAction.Manual,"Corrupt/repeated recovery loop continued");
            check(RecoverySupervisor.Decide(bad with{ConfigurationMatches=false})==RecoveryAction.Manual,"Changed config was automatically replayed");
        });
        test("Heartbeat reports degraded exports honestly and recovers only after a healthy probe",()=>{
            string dir=Path.Combine(Path.GetTempPath(),"flydisk-health-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try{
                var store=new StateStore(dir,new HostConfig());store.Write("running",null,true);
                store.Heartbeat(()=>new{ReadCommands=7},health:()=>"missing export");
                var s=JsonSerializer.Deserialize<HostState>(File.ReadAllText(store.PathName),StateStore.Json)!;
                check(s.Phase=="degraded" && s.RecoveryRequired && s.Error=="missing export","Heartbeat hid export loss");
                store.Heartbeat(()=>new{ReadCommands=8},health:()=>null);
                s=JsonSerializer.Deserialize<HostState>(File.ReadAllText(store.PathName),StateStore.Json)!;
                check(s.Phase=="running" && s.Error==null,"Healthy export remained degraded");
                store.Write("stopping",null,true);store.Heartbeat(()=>throw new Exception("Disposed backend accessed"),health:()=>throw new Exception());
            }finally{Directory.Delete(dir,true);}
        });
    }
}
