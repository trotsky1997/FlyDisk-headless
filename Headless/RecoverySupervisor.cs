// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace FlyDisk.Headless;

public enum RecoveryAction { None, Wait, Restart, Start, ReturnNative, Manual }
public sealed record RecoveryObservation(string Service, string Phase, bool ProcessAlive,
    bool RecoveryRequired, bool Unhealthy, bool PendingIntent, bool ConfigurationMatches, int Attempts);

public static class RecoverySupervisor
{
    public static RecoveryAction Decide(RecoveryObservation s)
    {
        if(!s.ConfigurationMatches)return RecoveryAction.Manual;
        if(s.Service is "StartPending" or "StopPending" or "ContinuePending" or "PausePending")return RecoveryAction.Wait;
        if(s.Service=="Running")
        {
            if(s.Phase is not ("running" or "degraded") || !s.Unhealthy)return RecoveryAction.None;
            return s.Attempts>=3 ? RecoveryAction.ReturnNative : RecoveryAction.Restart;
        }
        if(s.Service!="Stopped")return RecoveryAction.Manual;
        if(s.ProcessAlive)return RecoveryAction.Wait;
        if(!s.RecoveryRequired && !s.PendingIntent)return RecoveryAction.None; // Explicit clean stop stays stopped.
        return s.Attempts>=3 ? RecoveryAction.Manual : RecoveryAction.Start;
    }

    sealed record Intent(string ConfigurationHash, DateTimeOffset WindowStart, int Attempts, bool Pending, string Outcome, string? Error);
    static string IntentPath => Path.Combine(ServiceManager.DataDirectory,"supervisor.json");
    static void Save(Intent intent)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(intent,StateStore.Json);
        using(var f=new FileStream(IntentPath+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){f.Write(bytes);f.Flush(true);}
        File.Move(IntentPath+".tmp",IntentPath,true);
    }
    internal static void CancelIntent()
    {
        if(File.Exists(IntentPath)){
            var i=JsonSerializer.Deserialize<Intent>(File.ReadAllText(IntentPath),StateStore.Json);
            if(i!=null)Save(i with{Pending=false,Outcome="manual-stop"});
        }
    }
    internal static object Run()
    {
        ServiceManager.RequireAdministrator();
        using var gate=new FileStream(Path.Combine(ServiceManager.DataDirectory,"supervisor.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var config=HostConfig.Load(ServiceManager.ConfigPath);
        string hash=StateStore.ConfigHash(config);
        Intent intent=File.Exists(IntentPath) ? JsonSerializer.Deserialize<Intent>(File.ReadAllText(IntentPath),StateStore.Json) ?? throw new IOException("Unreadable supervisor state.")
            : new(hash,DateTimeOffset.UtcNow,0,false,"idle",null);
        if(DateTimeOffset.UtcNow-intent.WindowStart>TimeSpan.FromHours(1) && !intent.Pending)intent=new(hash,DateTimeOffset.UtcNow,0,false,"idle",null);
        HostState ReadState()=>JsonSerializer.Deserialize<HostState>(File.ReadAllText(Path.Combine(ServiceManager.DataDirectory,"status.json")),StateStore.Json) ?? throw new IOException("Unreadable host checkpoint.");
        RecoveryObservation Observe()
        {
            var state=ReadState();
            bool unhealthy=state.Phase=="degraded" || DateTimeOffset.UtcNow-state.UpdatedUtc>TimeSpan.FromSeconds(30);
            return new(ServiceManager.GetStatus(),state.Phase,StateStore.IsAlive(state),state.RecoveryRequired,unhealthy,
                intent.Pending,state.ConfigurationHash==hash && intent.ConfigurationHash==hash,intent.Attempts);
        }
        var observed=Observe();var action=Decide(observed);
        if(action is RecoveryAction.Restart or RecoveryAction.ReturnNative){
            Thread.Sleep(10_000); // Confirm the fault across multiple heartbeats, not one transient sample.
            observed=Observe();action=Decide(observed);
        }
        if(action is RecoveryAction.None or RecoveryAction.Wait or RecoveryAction.Manual)
            return new{Action=action.ToString(),Observation=observed,State=intent};
        intent=intent with{Attempts=intent.Attempts+1,Pending=true,Outcome="recovering",Error=null};Save(intent);
        try{
            if(action is RecoveryAction.Restart or RecoveryAction.ReturnNative)ServiceManager.Stop(); // No kill, no forced raw-disk online.
            var stopped=ReadState();
            if(action==RecoveryAction.ReturnNative){
                if(stopped.RecoveryRequired)throw new IOException("Native fallback is not proven safe; recovery remains required.");
                intent=intent with{Pending=false,Outcome="circuit-open-native"};Save(intent);
                return new{Action="native-access-restored",State=intent};
            }
            // Startup performs identity/config checks and validates/replays WAL before export.
            ServiceManager.Start();
            var started=ReadState();
            if(started.Phase!="running")throw new IOException("Cache restart has not reached a healthy running state.");
            intent=intent with{Pending=false,Outcome="recovered",Error=null};Save(intent);
            return new{Action="recovered",State=intent};
        }catch(Exception ex){
            intent=intent with{Outcome="recovery-failed",Error=ex.Message};Save(intent);
            throw;
        }
    }
}
