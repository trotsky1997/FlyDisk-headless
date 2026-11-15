// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using FlyDisk.Engine;
using FlyDisk.Headless;

static class WriteBackTests
{
    public static void Benchmark(string parent)
    {
        string dir=Path.Combine(parent,"flydisk-wal-benchmark-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var source=new Source();
            using var journal=new WriteBackJournal(source,dir,42,16<<20,false);
            byte[] data=new byte[128<<10];new Random(4592).NextBytes(data);
            var clock=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<64;i++)journal.Write((i%8)*data.Length,data,0,data.Length);
            journal.Flush();clock.Stop();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{Bytes=64L*data.Length,Seconds=clock.Elapsed.TotalSeconds,State=journal.Snapshot()}));
        } finally {Directory.Delete(dir,true);}
    }
    public static void CrashFixture(string dir)
    {
        using var s = new FileSource(Path.Combine(dir,"disk.bin"));
        var j = new WriteBackJournal(s,dir,42,65536,false);
        j.Write(0,Enumerable.Repeat((byte)183,4096).ToArray(),0,4096);
        File.WriteAllText(Path.Combine(dir,"ack"),"durable");
        Thread.Sleep(Timeout.Infinite);
    }
    public static void Run(Action<string,Action> test, Action<bool,string> check, Action<Action> throws)
    {
        string root = Path.Combine(Path.GetTempPath(), "flydisk-wal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Dir() => Path.Combine(root, Guid.NewGuid().ToString("N"));
        byte[] Data(byte value, int n = 4096) => Enumerable.Repeat(value,n).ToArray();
        try
        {
            test("WAL actual process kill preserves acknowledged write before HDD destage", () => {
                string dir=Dir();Directory.CreateDirectory(dir);
                var info=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
                info.ArgumentList.Add("--wal-crash-fixture");info.ArgumentList.Add(dir);
                using var child=System.Diagnostics.Process.Start(info)!;
                try {
                    var until=DateTime.UtcNow.AddSeconds(15);
                    while(!File.Exists(Path.Combine(dir,"ack")) && !child.HasExited && DateTime.UtcNow<until)Thread.Sleep(20);
                    check(File.Exists(Path.Combine(dir,"ack")),"Child never acknowledged write");child.Kill();child.WaitForExit();
                    using var s=new FileSource(Path.Combine(dir,"disk.bin"));byte[] b=new byte[4096];s.Read(0,b,0,b.Length);check(b[0]==0,"Fixture already destaged");
                    using var j=new WriteBackJournal(s,dir,42,65536,false);j.Drain();s.Read(0,b,0,b.Length);check(b.All(x=>x==183),"Killed process lost durable log");
                } finally {if(!child.HasExited){child.Kill();child.WaitForExit();}}
            });
            test("WAL ack precedes HDD write and survives reopen", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);
                j.Write(0,Data(7),0,4096);j.Flush();check(s.Stable[0]==0,"HDD was synchronously written");j.CloseWithoutDrain();
                using var recovered=new WriteBackJournal(s,dir,42,65536,false);byte[] b=new byte[4096];recovered.Read(0,b,0,b.Length);
                check(b.SequenceEqual(Data(7)),"Pending data invisible");recovered.Drain();check(s.Stable[0]==7,"Replay lost acknowledged write");
            });
            test("SCSI FUA and SYNCHRONIZE CACHE 10/16 persist WAL and report failure", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);
                var cfg=new FlyDisk.Models.DiskConfig{EnableMemoryCache=false,EnableSsdCache=false};
                var cache=new CacheService(cfg,new BlockSourceInfo{Model="TEST",SerialNumber="SCSI",SizeBytes=s.SizeBytes,BytesPerSector=512});
                var disk=new CachedPhysicalDisk(j,cache);var target=new DurableScsiTarget(disk,j);
                try {
                    byte[] write=new byte[10];write[0]=0x2a;write[1]=8;write[8]=1;
                    check(target.ExecuteCommand(write,(SCSI.LUNStructure)(ushort)0,Data(81,512),out _)==SCSI.SCSIStatusCodeName.Good,"FUA rejected");
                    check(s.Stable[0]==0,"FUA waited for HDD");
                    foreach(byte op in new byte[]{0x35,0x91}){byte[] c=new byte[op==0x35?10:16];c[0]=op;check(target.ExecuteCommand(c,(SCSI.LUNStructure)(ushort)0,Array.Empty<byte>(),out _)==SCSI.SCSIStatusCodeName.Good,"Flush rejected");}
                    s.FailFlush=true;throws(()=>j.DrainOne());byte[] flush=new byte[10];flush[0]=0x35;
                    check(target.ExecuteCommand(flush,(SCSI.LUNStructure)(ushort)0,Array.Empty<byte>(),out _)==SCSI.SCSIStatusCodeName.CheckCondition,"Faulted journal reported successful flush");
                } finally {disk.Close();cache.Release();j.CloseWithoutDrain();}
                s.FailFlush=false;s.PowerLoss();using var recovered=new WriteBackJournal(s,dir,42,65536,false);recovered.Drain();check(s.Stable[0]==81,"FUA write lost on recovery");
            });
            test("WAL latest overlapping sectors overlay before destage", () => {
                var s=new Source();using var j=new WriteBackJournal(s,Dir(),42,65536,false);
                j.Write(0,Data(4),0,4096);j.Write(512,Data(9,512),0,512);j.DrainOne();
                byte[] b=new byte[4096];j.Read(0,b,0,b.Length);check(b[0]==4 && b[512]==9 && b[1024]==4,"Old completion erased newer write");
                j.Drain();check(s.Stable.SequenceEqual(s.Live),"Source not flushed");
            });
            test("WAL fully covered reads never touch HDD; gaps and overlaps remain correct", () => {
                var s=new Source();using var j=new WriteBackJournal(s,Dir(),42,131072,false);
                j.Write(4096,Data(2),0,4096);j.Write(0,Data(1),0,4096);j.Write(512,Data(9,512),0,512);
                s.FailRead=true;byte[] b=new byte[8192];j.Read(0,b,0,b.Length);
                check(b.Take(512).All(x=>x==1) && b.Skip(512).Take(512).All(x=>x==9) && b.Skip(1024).Take(3072).All(x=>x==1) && b.Skip(4096).All(x=>x==2),"Covered overlay mismatch");
                throws(()=>j.Read(0,new byte[12288],0,12288));
                s.FailRead=false;j.Read(0,new byte[12288],0,12288);
            });
            test("WAL full ring applies bounded backpressure without overwrite", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,24576,false,20);
                j.Write(0,Data(1),0,4096);j.Write(4096,Data(2),0,4096);throws(()=>j.Write(8192,Data(3),0,4096));j.CloseWithoutDrain();
                using var r=new WriteBackJournal(s,dir,42,24576,false);r.Drain();check(s.Stable[0]==1 && s.Stable[4096]==2 && s.Stable[8192]==0,"Full ring overwrote or acknowledged new data");
            });
            test("WAL blocked producer resumes after destaging", () => {
                var s=new Source();using var j=new WriteBackJournal(s,Dir(),42,24576,false,5000);
                j.Write(0,Data(1),0,4096);j.Write(4096,Data(2),0,4096);
                var producer=Task.Run(()=>j.Write(8192,Data(3),0,4096));Thread.Sleep(80);check(!producer.IsCompleted,"Producer bypassed full ring");
                j.StartWorker();check(producer.Wait(5000),"Producer remained blocked");j.Drain();check(s.Stable[8192]==3,"Queued write lost");
            });
            test("WAL wrap and mixed request sizes preserve randomized model", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,45056,false,100);
                var model=new byte[s.Live.Length];var rng=new Random(972);
                for(int k=0;k<150;k++){
                    int count=rng.Next(1,10)*512,at=rng.Next(0,200)*512;byte[] bytes=Data((byte)(k+1),count);
                    j.Write(at,bytes,0,count);Array.Copy(bytes,0,model,at,count);
                    byte[] b=new byte[model.Length];j.Read(0,b,0,b.Length);check(b.SequenceEqual(model),"Overlay diverged from model");
                    j.Drain();if(k%7==0){j.CloseWithoutDrain();j=new WriteBackJournal(s,dir,42,45056,false);}
                }
                j.Dispose();check(s.Stable.SequenceEqual(model),"Wrap damaged source");check(new FileInfo(WriteBackJournal.PathFor(dir)).Length<=45056,"Budget exceeded");
            });
            foreach(string point in new[]{"record-durable","checkpoint-first","checkpoint-second","source-durable"})
            test("WAL crash at "+point+" replays committed prefix", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);
                j.Write(0,Data(11),0,4096);
                j.FaultPoint=p=>{if(p==point)throw new IOException("power loss");};
                if(point=="source-durable")throws(()=>j.DrainOne());else throws(()=>j.Write(4096,Data(22),0,4096));
                j.CloseWithoutDrain();s.PowerLoss();using var r=new WriteBackJournal(s,dir,42,65536,false);r.Drain();
                check(s.Stable[0]==11,"Acknowledged prefix lost");
                if(point is "checkpoint-first" or "checkpoint-second")check(s.Stable[4096]==22,"Durable commit lost");
            });
            test("WAL failed HDD flush never frees a committed record", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);
                j.Write(0,Data(66),0,4096);s.FailFlush=true;throws(()=>j.DrainOne());throws(()=>j.Write(4096,Data(1),0,4096));j.CloseWithoutDrain();
                s.PowerLoss();s.FailFlush=false;using var r=new WriteBackJournal(s,dir,42,65536,false);r.Drain();check(s.Stable[0]==66,"Premature reclaim lost data");
            });
            test("WAL corrupt acknowledged payload refuses startup", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);j.Write(0,Data(1),0,4096);j.CloseWithoutDrain();
                using(var f=new FileStream(WriteBackJournal.PathFor(dir),FileMode.Open,FileAccess.Write)){f.Position=12288;f.WriteByte(42);f.Flush(true);}
                throws(()=>new WriteBackJournal(s,dir,42,65536,false));check(s.Stable[0]==0,"Corrupt replay mutated source");
            });
            test("WAL missing journal, wrong identity and resizing fail closed", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);j.Write(0,Data(1),0,4096);j.CloseWithoutDrain();
                throws(()=>new WriteBackJournal(s,dir,43,65536,false));throws(()=>new WriteBackJournal(s,dir,42,131072,false));
                File.Delete(WriteBackJournal.PathFor(dir));throws(()=>new WriteBackJournal(s,dir,42,65536,false));
            });
            test("WAL dirty source seen online persists conflict and blocks future replay", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);
                j.Write(0,Data(63),0,4096);throws(()=>j.AssertRecoverySourceWasOffline(true));j.CloseWithoutDrain();
                throws(()=>new WriteBackJournal(s,dir,42,65536,false));check(s.Stable[0]==0,"Conflicting source was overwritten");
            });
            foreach(string point in new[]{"checkpoint-first","checkpoint-second"})
            test("WAL crash during reclaim "+point+" preserves remaining writes", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);
                j.Write(0,Data(11),0,4096);j.Write(4096,Data(22),0,4096);
                j.FaultPoint=p=>{if(p==point)throw new IOException("reclaim crash");};throws(()=>j.DrainOne());j.CloseWithoutDrain();s.PowerLoss();
                using var r=new WriteBackJournal(s,dir,42,65536,false);r.Drain();check(s.Stable[0]==11 && s.Stable[4096]==22,"Reclaim crash lost data");
            });
            test("WAL torn redundant checkpoint recovers valid copy", () => {
                var s=new Source();string dir=Dir();var j=new WriteBackJournal(s,dir,42,65536,false);j.Write(0,Data(17),0,4096);j.CloseWithoutDrain();
                using(var f=new FileStream(WriteBackJournal.PathFor(dir),FileMode.Open,FileAccess.Write)){f.Write(new byte[4096]);f.Flush(true);}
                using var r=new WriteBackJournal(s,dir,42,65536,false);r.Drain();check(s.Stable[0]==17,"Single checkpoint corruption lost write");
            });
            test("WAL background repeated writes serialize source pointer and latest version", () => {
                var s=new Source();using var j=new WriteBackJournal(s,Dir(),42,4<<20,true);
                for(int n=1;n<100;n++){j.Write(0,Data((byte)n),0,4096);byte[] b=new byte[4096];j.Read(0,b,0,b.Length);check(b[0]==n,"Stale concurrent read");}
                j.Drain();check(s.Stable[0]==99,"Old version overwrote latest");
            });
            test("WAL 4GiB / 64GiB total budgets include metadata reserves", () => {
                var c=new HostConfig{EnableWriteBack=true,MemoryCacheMaxBytes=4L<<30,CacheSizeBytes=64L<<30};
                check(c.JournalBudgetBytes+c.ReadCacheBudgetBytes+(512L<<20)<=c.CacheSizeBytes,"SSD budget oversubscribed");
                check(c.ReadMemoryBudgetBytes*1.0625+c.ReadCacheBudgetBytes/4096*128+(128L<<20)<=c.MemoryCacheMaxBytes,"RAM budget oversubscribed");
            });
        }
        finally {if(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("flydisk-wal-tests-"))Directory.Delete(root,true);}
    }
    sealed class Source : IDurableBlockSource
    {
        public byte[] Live=new byte[1<<20],Stable=new byte[1<<20];public bool FailFlush,FailRead;
        public int BytesPerSector=>512;public long SizeBytes=>Live.Length;
        public void Write(long o,byte[] b,int p,int n)=>Buffer.BlockCopy(b,p,Live,(int)o,n);
        public void Read(long o,byte[] b,int p,int n){if(FailRead)throw new IOException("injected read failure");Buffer.BlockCopy(Live,(int)o,b,p,n);}
        public void Flush(){if(FailFlush)throw new IOException("injected flush failure");Live.CopyTo(Stable,0);}
        public void PowerLoss()=>Stable.CopyTo(Live,0);
        public void Dispose(){}
    }
    sealed class FileSource : IDurableBlockSource
    {
        readonly FileStream f;
        public FileSource(string path){f=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);if(f.Length==0){f.SetLength(1<<20);f.Flush(true);}}
        public int BytesPerSector=>512;public long SizeBytes=>f.Length;
        public void Read(long o,byte[] b,int p,int n){f.Position=o;f.ReadExactly(b.AsSpan(p,n));}
        public void Write(long o,byte[] b,int p,int n){f.Position=o;f.Write(b,p,n);}
        public void Flush()=>f.Flush(true);
        public void Dispose()=>f.Dispose();
    }
}
