// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
// Never opens physical disks, installs services or changes disk attributes.
using System.Diagnostics;
using System.Text.Json;
using FlyDisk.Engine;
using FlyDisk.Headless;
using FlyDisk.Models;

if (args.SequenceEqual(["--service-fixture"]))
{
    System.ServiceProcess.ServiceBase.Run(DiskService.Create(new ServiceFixture()));
    return;
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection"); }
HostConfig Valid() => new() { DiskIdentity = "TEST|123", ExpectedSizeBytes = 8 << 20, CacheVolume = "C:", RiskAccepted = true };

Test("invalid config never accepted", () => {
    foreach (var bad in new[] {Valid() with {RiskAccepted=false}, Valid() with {DiskIdentity="TEST|"},
        Valid() with {ExpectedSizeBytes=7}, Valid() with {CacheSizeBytes=1}, Valid() with {CacheVolume="C:\\other"},
        Valid() with {ListenPort=0}, Valid() with {SchemaVersion=2}}) Throws(bad.Validate);
    Valid().Validate();
});
Test("VHD VPD83 identity parser rejects malformed and port-scoped identifiers", () => {
    // Construct a standards-sized single NAA descriptor independently of the captured sample.
    byte[] descriptor=new byte[44];BitConverter.GetBytes(16).CopyTo(descriptor,0);BitConverter.GetBytes(44).CopyTo(descriptor,4);
    BitConverter.GetBytes(1).CopyTo(descriptor,8);BitConverter.GetBytes(1).CopyTo(descriptor,12);BitConverter.GetBytes(3).CopyTo(descriptor,16);
    BitConverter.GetBytes((ushort)16).CopyTo(descriptor,20);Convert.FromHexString("6002248070BE9C38CE6C6EDD5DB7A9A2").CopyTo(descriptor,28);
    Check(StorageDeviceIdentity.ReadBinaryDeviceId(descriptor)=="6002248070BE9C38CE6C6EDD5DB7A9A2","NAA not recognized");
    Check(StorageDeviceIdentity.ReadBinaryDeviceId(descriptor.AsSpan(0,43))=="","Truncated descriptor accepted");
    descriptor[24]=1;Check(StorageDeviceIdentity.ReadBinaryDeviceId(descriptor)=="","Port identity accepted");
    descriptor[24]=0;descriptor[20]=255;Check(StorageDeviceIdentity.ReadBinaryDeviceId(descriptor)=="","Oversized entry accepted");
});
Test("preflight failure never takes disk offline or restores unrelated disk", () => {
    var b = new FakeBackend { Fail = "preflight" }; var host = new Lifecycle(b, (_,_,_)=>{});
    Throws(()=>host.Start(default)); Check(b.Calls.SequenceEqual(["preflight"]), "Unexpected disk mutation");
});
Test("storage-provider bus types support both Windows JSON representations", () => {
    using var values=JsonDocument.Parse("[9,15,\"iSCSI\",\"File Backed Virtual\",null]");
    var list=values.RootElement.EnumerateArray().ToArray();
    Check(NativeBackend.IsIscsiBus(list[0]) && NativeBackend.IsIscsiBus(list[2]),"iSCSI not recognized");
    Check(!NativeBackend.IsIscsiBus(list[1]) && !NativeBackend.IsIscsiBus(list[3]),"Virtual source misidentified");
    Throws(()=>NativeBackend.IsIscsiBus(list[4]));
});
Test("journal failure precedes first disk mutation", () => {
    var b = new FakeBackend(); var host = new Lifecycle(b, (p,_,_)=> {if(p=="preparing") throw new IOException();});
    Throws(()=>host.Start(default)); Check(b.Calls.SequenceEqual(["preflight"]), "Mutated without journal");
});
foreach (string failure in new[] {"prepare", "start"}) Test(failure + " failure restores disk", () => {
    var b = new FakeBackend {Fail=failure}; var host = new Lifecycle(b, (_,_,_)=>{});
    Throws(()=>host.Start(default)); Check(b.Calls.TakeLast(2).SequenceEqual(["stop","restore"]), "Recovery order");
    Check(host.Phase=="failed-restored" && !host.RecoveryRequired, "Incorrect recovery status");
});
Test("normal stop drains before restoring", () => {
    var b=new FakeBackend(); var host=new Lifecycle(b, (_,_,_)=>{}); host.Start(default); host.Stop(); host.Stop();
    Check(b.Calls.SequenceEqual(["preflight","prepare","start","stop","restore"]), "Bad ordering or repeated restore");
});
Test("refused stop keeps ownership and never restores; retry succeeds", () => {
    var b=new FakeBackend(); var host=new Lifecycle(b, (_,_,_)=>{}); host.Start(default); b.Fail="stop";
    Throws(host.Stop); Check(!b.Calls.Contains("restore") && host.RecoveryRequired, "Unsafe online");
    b.Fail=""; host.Stop(); Check(host.Phase=="stopped", "Cannot retry");
});
Test("failed-start cleanup refusal never restores", () => {
    var b=new FakeBackend{Fail="start", FailStop=true}; var host=new Lifecycle(b, (_,_,_)=>{});
    Throws(()=>host.Start(default)); Check(!b.Calls.Contains("restore") && host.Phase=="recovery-required", "Unsafe fallback");
});
Test("failed restoration remains explicitly recoverable", () => {
    var b=new FakeBackend(); var host=new Lifecycle(b, (_,_,_)=>{}); host.Start(default); b.Fail="restore";
    Throws(host.Stop); Check(host.RecoveryRequired && host.Phase=="recovery-required", "Lost recovery checkpoint");
});
Test("cancelled startup does not mount", () => {
    using var cts=new CancellationTokenSource(); var b=new FakeBackend {AfterPrepare=()=>cts.Cancel()};
    var host=new Lifecycle(b, (_,_,_)=>{}); Throws(()=>host.Start(cts.Token));
    Check(!b.Calls.Contains("start") && b.Calls.Last()=="restore", "Cancellation failed");
});
Test("verification rejects every incomplete/error outcome", () => {
    Check(new L2VerifyResult {PlannedBlocks=1,ScannedBlocks=1,MatchedBlocks=1}.IsClean, "Valid result rejected");
    foreach(var bad in new[] {new L2VerifyResult{Cancelled=true},new L2VerifyResult{Aborted=true},
        new L2VerifyResult{ReadFailedBlocks=1},new L2VerifyResult{MismatchedBlocks=1},
        new L2VerifyResult{DuplicateBlocks=1},new L2VerifyResult{OutOfRangeBlocks=1},
        new L2VerifyResult{PlannedBlocks=1},new L2VerifyResult{PlannedBlocks=1,ScannedBlocks=1}})
        Check(!NativeBackend.VerificationPassed(bad), "False verification success");
    using var verifier=new L2Verifier(new DiskConfig(),-1); Throws(verifier.ConfirmLedger);
});
Test("automatic refresh requires complete verified differences and no read/structure errors", () => {
    var result=new L2VerifyResult{PlannedBlocks=3,ScannedBlocks=3,MatchedBlocks=2,MismatchedBlocks=1};
    Check(NativeBackend.CanRefreshChangedBlocks(result),"Complete changed blocks not refreshable");
    result.ReadFailedBlocks=1;Check(!NativeBackend.CanRefreshChangedBlocks(result),"Read error accepted");
    result.ReadFailedBlocks=0;result.ScannedBlocks=2;Check(!NativeBackend.CanRefreshChangedBlocks(result),"Incomplete scan accepted");
    result.ScannedBlocks=3;result.DuplicateBlocks=1;Check(!NativeBackend.CanRefreshChangedBlocks(result),"Duplicate mapping accepted");
    result.DuplicateBlocks=0;result.Cancelled=true;Check(!NativeBackend.CanRefreshChangedBlocks(result),"Cancelled scan accepted");
});

string root=Path.Combine(Path.GetTempPath(), "flydisk-headless-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Test("unknown config key fails rather than silently using defaults", () => {
        string file=Path.Combine(root,"bad.json"); File.WriteAllText(file, JsonSerializer.Serialize(Valid()).TrimEnd('}')+",\"Typo\":1}");
        Throws(()=>HostConfig.Load(file));
    });
    Test("status distinguishes live process, stale PID and PID reuse", () => {
        var store=new StateStore(root,Valid()); store.Write("running",null,true);
        var state=JsonSerializer.Deserialize<HostState>(File.ReadAllText(store.PathName),StateStore.Json)!;
        Check(StateStore.IsAlive(state),"Live process missing");
        Check(!StateStore.IsAlive(state with {ProcessStartUtcTicks=0}),"PID reuse accepted");
        Check(!StateStore.IsAlive(state with {Pid=int.MaxValue}),"Dead PID alive");
        Throws(()=>new StateStore(root,Valid() with {DiskIdentity="OTHER|456"}).AssertRecoveryConfigMatches());
        Throws(()=>new StateStore(root,Valid() with {CacheVolume="E:"}).AssertRecoveryConfigMatches());
        store.AssertRecoveryConfigMatches();
        store.Write("stopped",null,false); Check(!File.Exists(store.PathName+".tmp"),"Incomplete atomic status");
    });

    Test("restart cannot overwrite an unfinished recovery checkpoint", () => {
        var store=new StateStore(root,Valid()); store.Write("recovery-required","injected",true);
        string before=File.ReadAllText(store.PathName);
        Throws(store.AssertReadyForStart);
        Check(File.ReadAllText(store.PathName)==before,"Pending recovery overwritten");
        store.Write("stopped",null,false); store.AssertReadyForStart();
    });

    Test("exact slot LRU matches independent reference under random touches/removals",()=>{
        var lru=new SlotLru(37);var reference=new List<int>();var rng=new Random(927);
        for(int i=0;i<10000;i++){
            int slot=rng.Next(37);
            if(rng.Next(3)==0){lru.Remove(slot);reference.Remove(slot);}
            else {lru.Touch(slot);reference.Remove(slot);reference.Add(slot);}
            Check(lru.Count==reference.Count && lru.OldestFirst().SequenceEqual(reference),"LRU order diverged");
            if(i%17==0 && reference.Count>0){Check(lru.PopOldest()==reference[0],"Wrong eviction");reference.RemoveAt(0);}
        }
    });
    Test("SSD evicts actual LRU, reclaims invalidation and persists recency",()=>{
        var cfg=new DiskConfig{EnableMemoryCache=false,EnableSsdCache=true,SsdCachePath=Path.Combine(root,"lru"),SsdCacheMaxBytes=64L<<20};
        var info=new BlockSourceInfo{Model="TEST",SerialNumber="LRU",SizeBytes=128L<<20,BytesPerSector=512};
        var data=new byte[4096];var cache=new CacheService(cfg,info);
        try{
            for(int i=0;i<16384;i++){BitConverter.GetBytes(i).CopyTo(data,0);cache.SetBlock(i,data,0);}
            Check(cache.TryGetBlock(0,data,0),"Missing hot block");cache.SetBlock(16384,data,0);
            Check(cache.SsdCache!.ContainsBlock(0) && !cache.SsdCache.ContainsBlock(1),"Not LRU");
            cache.InvalidateRange(2,1);cache.SetBlock(16385,data,0);
            Check(cache.SsdCache.ContainsBlock(3),"Invalidation failed to recycle slot");
            cache.FreezeForShutdown();cache.CommitLedger(LedgerCommit.Clean);
        }finally{cache.Release();}
        cache=new CacheService(cfg,info);
        try{cache.SetBlock(16386,data,0);Check(!cache.SsdCache!.ContainsBlock(3) && cache.SsdCache.ContainsBlock(0),"Restart lost recency");}
        finally{cache.Release();}
    });
    Test("L1 enforces hard byte cap and replaces oldest including same-second hits",()=>{
        var cfg=new DiskConfig{EnableMemoryCache=true,MemoryCacheMaxBytes=64L<<20,EnableSsdCache=false,EvictionThreshold=0.99,StopCachingThreshold=1};
        var info=new BlockSourceInfo{Model="TEST",SerialNumber="L1",SizeBytes=128L<<20,BytesPerSector=512};
        var cache=new CacheService(cfg,info);var bytes=new byte[4096];
        try{
            for(int i=0;i<16384;i++)cache.SetBlock(i,bytes,0);
            Check(cache.TryGetBlock(0,bytes,0),"L1 absent");cache.SetBlock(16384,bytes,0);
            Check(cache.ContainsBlock(0) && !cache.ContainsBlock(1),"L1 did not evict oldest");
            Check(cache.FillSnapshot(new()).TotalCacheSize<=64L<<20,"L1 exceeded hard cap");
        }finally{cache.Release();}
    });
    foreach(int sector in new[]{512,4096}) Test($"{sector}: adaptive prefetch grows, backs off, preserves writes and EOF",()=>{
        var cfg=new DiskConfig{EnableMemoryCache=true,MemoryCacheMaxBytes=64L<<20,EnableSsdCache=false,EnableAdaptivePrefetch=true,PrefetchMaxBytes=1<<20,EvictionThreshold=.99,StopCachingThreshold=1};
        var raw=new FakeSource(8<<20,sector);var info=new BlockSourceInfo{Model="TEST",SerialNumber="PREFETCH",SizeBytes=raw.SizeBytes,BytesPerSector=sector};
        var cache=new CacheService(cfg,info);var disk=new CachedPhysicalDisk(raw,cache,cfg);
        try{
            for(int i=0;i<16;i++)Check(disk.ReadSectors(i*65536/sector,65536/sector).SequenceEqual(raw.Data.Skip(i*65536).Take(65536)),"Sequential data differs");
            Check(disk.ReadAhead!.FetchedBlocks>0 && disk.ReadAhead.UsefulBlocks>0 && disk.ReadAhead.WindowBytes>65536,"Prefetch did not adapt");
            long fetched=disk.ReadAhead.FetchedBlocks;disk.ReadSectors((6<<20)/sector,4096/sector);
            Check(disk.ReadAhead.FetchedBlocks==fetched && disk.ReadAhead.WindowBytes==65536,"Random access still prefetches");
            var changed=Enumerable.Repeat((byte)217,sector).ToArray();disk.WriteSectors((1<<20)/sector,changed);
            Check(disk.ReadSectors((1<<20)/sector,4096/sector).SequenceEqual(raw.Data.Skip(1<<20).Take(4096)),"Write left stale prefetch");
            disk.ReadSectors(raw.SizeBytes/sector-16,8);disk.ReadSectors(raw.SizeBytes/sector-8,8);
            Check(disk.ReadAhead.Failures==0,"Prefetch crossed disk end");
        }finally{disk.Close();cache.Release();}
    });
    Test("speculative read failure does not fail demand read and enters cooldown",()=>{
        var cfg=new DiskConfig{EnableMemoryCache=true,MemoryCacheMaxBytes=64L<<20,EnableAdaptivePrefetch=true,EnableSsdCache=false,EvictionThreshold=.99,StopCachingThreshold=1};
        var raw=new FakeSource(4<<20,512);var cache=new CacheService(cfg,new BlockSourceInfo{Model="TEST",SerialNumber="FAIL",SizeBytes=raw.SizeBytes,BytesPerSector=512});
        var disk=new CachedPhysicalDisk(raw,cache,cfg);
        try{disk.ReadSectors(0,128);raw.FailReadAt=131072;
            Check(disk.ReadSectors(128,128).SequenceEqual(raw.Data.Skip(65536).Take(65536)),"Speculation failed demand");
            Check(disk.ReadAhead!.Failures==1,"Failure not reported");raw.FailReadAt=null;
            long before=disk.ReadAhead.FetchedBlocks;disk.ReadSectors(256,128);Check(before==disk.ReadAhead.FetchedBlocks,"Cooldown ignored");
        }finally{disk.Close();cache.Release();}
    });

    foreach(int sector in new[]{512,4096})
    {
        string cacheDir=Path.Combine(root,"cache-"+sector);
        var cfg=new DiskConfig {EnableMemoryCache=false,EnableSsdCache=true,SsdCachePath=cacheDir,
            SsdCacheMaxBytes=64L<<20,SectorsPerBlock=4096/sector};
        var info=new BlockSourceInfo {Model="SYNTHETIC",SerialNumber="TEST",SizeBytes=(8<<20)+sector,BytesPerSector=sector,WasOnline=false};
        var raw=new FakeSource((int)info.SizeBytes,sector);
        var cache=new CacheService(cfg,info); var disk=new CachedPhysicalDisk(raw,cache);
        try
        {
            Test($"{sector}: cold read merges 1 MiB, warm read uses L2 with L1 disabled",()=>{
                byte[] first=disk.ReadSectors(0,(1<<20)/sector); int reads=raw.Reads;
                Check(reads==1,"Cold read was not merged");
                Check(disk.ReadSectors(0,(1<<20)/sector).SequenceEqual(first),"Warm bytes changed");
                Check(raw.Reads==reads && cache.SsdCache!.HitBlocksTotal>0,"L2 did not serve warm read");
            });
            Test($"{sector}: aligned write-through refreshes cached block",()=>{
                var data=Enumerable.Repeat((byte)77,4096).ToArray(); disk.WriteSectors(0,data); int reads=raw.Reads;
                Check(raw.Data.AsSpan(0,4096).SequenceEqual(data),"Write not delivered to source");
                Check(disk.ReadSectors(0,4096/sector).SequenceEqual(data) && raw.Reads==reads,"Cache not refreshed");
            });
            if(sector==512) Test("partial-sector-block write invalidates old block",()=>{
                var data=Enumerable.Repeat((byte)99,512).ToArray(); disk.WriteSectors(1,data); int reads=raw.Reads;
                Check(disk.ReadSectors(0,8).SequenceEqual(raw.Data.Take(4096)),"Partial write stale cache");
                Check(raw.Reads>reads,"Partial block not invalidated");
            });
            Test($"{sector}: failed partial source write invalidates entire request",()=>{
                disk.ReadSectors(0,8192/sector); raw.FailWrite=true;
                Throws(()=>disk.WriteSectors(0,Enumerable.Repeat((byte)33,8192).ToArray())); raw.FailWrite=false;
                Check(disk.ReadSectors(0,8192/sector).SequenceEqual(raw.Data.Take(8192)),"Failed write left stale bytes");
            });
            Test($"{sector}: range guards and end-of-disk reads",()=>{
                Throws(()=>disk.ReadSectors(-1,1)); Throws(()=>disk.ReadSectors(raw.SizeBytes/sector,1));
                var last=disk.ReadSectors(raw.SizeBytes/sector-1,1);
                Check(last.SequenceEqual(raw.Data.TakeLast(sector)),"Tail differs");
            });
            disk.Close(); cache.FreezeForShutdown(); cache.CommitLedger(LedgerCommit.Clean);
            Test($"{sector}: closed backend rejects queued IO",()=>Throws(()=>disk.ReadSectors(0,1)));
        }
        finally {cache.Release();}
        Test($"{sector}: clean L2 ledger survives cache restart",()=>{
            var next=new CacheService(cfg,info);
            try {var lun=new CachedPhysicalDisk(raw,next); int reads=raw.Reads;
                Check(lun.ReadSectors(0,4096/sector).SequenceEqual(raw.Data.Take(4096)),"Reloaded L2 wrong");
                Check(raw.Reads==reads,"Reloaded L2 missed");
                lun.Close();next.FreezeForShutdown();next.CommitLedger(LedgerCommit.Clean);
            } finally {next.Release();}
        });
        Test($"{sector}: invalidated ledger refuses reuse",()=>{
            Check(SsdCacheService.InvalidateLedger(cfg),"Invalidation failed");
            Throws(()=>{var invalid=new CacheService(cfg,info);invalid.Release();});
        });
    }
}
finally
{
    // Only the generated, verified test directory may be deleted.
    string expected=Path.Combine(Path.GetTempPath(),Path.GetFileName(root));
    if(Path.GetFullPath(root)==Path.GetFullPath(expected) && Path.GetFileName(root).StartsWith("flydisk-headless-tests-"))
        Directory.Delete(root,true);
}
Console.WriteLine($"ALL {passed} TESTS PASSED");

sealed class FakeBackend : IAcceleratorBackend
{
    public string Fail=""; public bool FailStop; public Action? AfterPrepare;
    public List<string> Calls=[];
    void Call(string name){Calls.Add(name);if(Fail==name || (name=="stop"&&FailStop))throw new IOException("injected "+name);}
    public void Preflight()=>Call("preflight");
    public void Prepare(CancellationToken token){Call("prepare");AfterPrepare?.Invoke();}
    public void Start()=>Call("start"); public void Stop()=>Call("stop"); public void Restore()=>Call("restore");
    public object? Snapshot()=>null;
}
sealed class FakeSource : IBlockSource
{
    public byte[] Data; public int Reads; public bool FailWrite; public long? FailReadAt;
    public FakeSource(int size,int sector){Data=new byte[size];new Random(42).NextBytes(Data);BytesPerSector=sector;}
    public int BytesPerSector{get;} public long SizeBytes=>Data.Length;
    public void Read(long offset,byte[] target,int targetOffset,int count){Reads++;if(FailReadAt.HasValue && offset>=FailReadAt.Value)throw new IOException("injected read");Array.Copy(Data,offset,target,targetOffset,count);}
    public void Write(long offset,byte[] source,int sourceOffset,int count){Array.Copy(source,sourceOffset,Data,offset,FailWrite?BytesPerSector:count);if(FailWrite)throw new IOException("partial source write");}
    public void Dispose(){}
}

// Only the test executable exposes this fixture; production CLI has no guard-bypass mode.
sealed class ServiceFixture : IHeadlessHost
{
    private readonly string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"FlyDiskHeadlessServiceTest");
    public void Start(){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"started"),"yes");}
    public void Stop(){if(File.Exists(Path.Combine(directory,"refuse-stop")))throw new IOException("Injected busy backend");File.WriteAllText(Path.Combine(directory,"stopped"),"yes");}
}
