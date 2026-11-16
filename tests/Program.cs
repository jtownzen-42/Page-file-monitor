using PagefileMonitor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class Tests
{
 static int failures,passed;static long now=Units.Now;
 static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex);}}
 static void Assert(bool condition,string message="Assertion failed"){if(!condition)throw new Exception(message);}
 static void Throws(Action action){try{action();}catch(Exception){return;}throw new Exception("Expected rejection");}
 static PageEntry Entry(string drive,uint mb)=>new PageEntry{Path=drive+@"\pagefile.sys",InitialMB=mb,MaximumMB=mb};
 static Snapshot SystemState()=>new Snapshot{Time=now,Boot=now-8*86400,WindowsDrive="C:",Automatic=false,DumpType=3,
  Entries=new List<PageEntry>{Entry("C:",16384)},Active=new List<ActivePage>{new ActivePage{Path=@"C:\pagefile.sys",AllocatedMB=16384}},
  Volumes=new List<Volume>{new Volume{Drive="C:",Identity="disk-c",Eligible=true,Size=250*Units.GiB,Free=100*Units.GiB},new Volume{Drive="I:",Identity="disk-i",Eligible=true,Size=250*Units.GiB,Free=150*Units.GiB}}};
 static Evidence Covered()=>new Evidence{First=now-8*86400,Last=now,ValidSeconds=86400,Peak=42*Units.GiB,BootPeak=42*Units.GiB,Physical=32*Units.GiB,Light=1800,Normal=7200,Heavy=1800};
 static Policy Policy()=>new Policy{HeavyConfirmed=true,HeavyConfirmedSeconds=1800};
 static void Temp(Action<string> action){string dir=Path.Combine(Path.GetTempPath(),"PagefileMonitor-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);try{action(dir);}finally{Directory.Delete(dir,true);}}
 sealed class Fake:IConfigurationBackend
 {
  public Snapshot Current=SystemState();public bool FailOnce;public int Writes;
  public Snapshot Read()=>Json.Copy(Current);
  public void Write(Snapshot desired){Writes++;Current.Automatic=desired.Automatic;Current.Entries=Json.Copy(desired.Entries);if(FailOnce){FailOnce=false;throw new IOException("Simulated interrupted write");}}
  public void Restart(){Current.Boot+=100;Current.Active=Current.Entries.Select(x=>new ActivePage{Path=x.Path,AllocatedMB=x.InitialMB}).ToList();}
 }
 static int Main(string[] args)
 {
  if(args.Contains("--live"))
  {
   Test("Named-pipe status provides a valid live sample",()=>{var reply=PipeProtocol.Send(new Request{Command="status"});Assert(reply.Ok&&reply.Status.Sample.Commit>0);var memory=WindowsMetrics.ReadMemory(0);Assert(Math.Abs(memory.Commit-reply.Status.Sample.Commit)<2*Units.GiB,"Memory readings differ by over 2 GiB");});
   Test("Named-pipe history export is available",()=>{var reply=PipeProtocol.Send(new Request{Command="history"});Assert(reply.Ok&&reply.Data.Contains("peak_committed_bytes"));});
   Test("Non-admin or read-only pipe rejects settings writes",()=>{var reply=PipeProtocol.Send(new Request{Command="policy",Enabled=true,CeilingMB=65536});Assert(!reply.Ok);});
   Console.WriteLine($"RESULT: {passed} passed, {failures} failed");return failures==0?0:1;
  }
  Test("License requires publisher configuration",()=>{string why;Assert(!new CommerceConfig().Ready);Assert(!Licensing.Accept(new CommerceConfig(),new GumroadReceipt{success=true,purchase=new GumroadPurchase()},false,out why));
   // A 1.2.0 commerce.json with numeric Lemon Squeezy IDs must not read as configured.
   Assert(!Json.Read<CommerceConfig>("{\"StoreId\":1,\"ProductId\":2,\"VariantId\":3,\"CheckoutUrl\":\"\"}").Ready);});
  Test("License checks product, refund, dispute, subscription and activation limit",()=>{
   var c=new CommerceConfig{GumroadProductId="prod=="};string why;
   Func<GumroadReceipt> valid=()=>new GumroadReceipt{success=true,uses=1,purchase=new GumroadPurchase{product_id="prod==",refunded=false,disputed=false,chargebacked=false}};
   Assert(Licensing.Accept(c,valid(),false,out why) && why==null);
   Assert(Licensing.Accept(c,valid(),true,out why));
   foreach(var alter in new Action<GumroadReceipt>[] {x=>x.success=false,x=>x.success=null,x=>x.purchase=null,x=>x.purchase.product_id="other",x=>x.purchase.product_id=null,x=>x.purchase.refunded=true,x=>x.purchase.chargebacked=true,x=>x.purchase.disputed=true,x=>x.purchase.subscription_ended_at="2026-01-01T00:00:00Z",x=>x.purchase.subscription_cancelled_at="2026-01-01T00:00:00Z",x=>x.purchase.subscription_failed_at="2026-01-01T00:00:00Z"}){var r=valid();alter(r);Assert(!Licensing.Accept(c,r,false,out why) && !string.IsNullOrEmpty(why));}
   var won=valid();won.purchase.disputed=true;won.purchase.dispute_won=true;Assert(Licensing.Accept(c,won,false,out why));
   var third=valid();third.uses=Licensing.MaxActivations;Assert(Licensing.Accept(c,third,true,out why));
   var fourth=valid();fourth.uses=Licensing.MaxActivations+1;Assert(!Licensing.Accept(c,fourth,true,out why));Assert(Licensing.Accept(c,fourth,false,out why),"Re-checks must not count activations");
  });
  Test("Only an explicit Gumroad answer can reject a license",()=>{
   var c=new CommerceConfig{GumroadProductId="prod=="};GumroadReceipt r;string why;
   const string good="{\"success\":true,\"uses\":2,\"purchase\":{\"product_id\":\"prod==\",\"refunded\":false,\"disputed\":false,\"dispute_won\":null,\"chargebacked\":false,\"subscription_ended_at\":null,\"email\":\"ignored\"}}";
   Assert(Licensing.Check(c,200,good,false,out r,out why)==LicenseCheck.Valid && r.uses==2);
   Assert(Licensing.Check(c,404,"{\"success\":false,\"message\":\"That license does not exist for the provided product.\"}",false,out r,out why)==LicenseCheck.Rejected);
   Assert(Licensing.Check(c,200,good.Replace("\"refunded\":false","\"refunded\":true"),false,out r,out why)==LicenseCheck.Rejected);
   foreach(var status in new[]{0,408,429,500,502,503})Assert(Licensing.Check(c,status,good,false,out r,out why)==LicenseCheck.Unreachable && r==null);
   foreach(var body in new[]{null,"","<html>Sign in to this network</html>","{}","[]","{\"error\":\"blocked\"}"}){Assert(Licensing.Check(c,200,body,false,out r,out why)==LicenseCheck.Unreachable,"Body: "+body);Assert(Licensing.Check(c,403,body,false,out r,out why)==LicenseCheck.Unreachable);}
   Assert(Licensing.Entitled(LicenseCheck.Valid) && Licensing.Entitled(LicenseCheck.Unreachable) && !Licensing.Entitled(LicenseCheck.Rejected));
  });
  Test("Checkout opens only an https Gumroad address",()=>{
   Uri uri;Func<string,string,bool> ok=(id,url)=>new CommerceConfig{GumroadProductId=id,CheckoutUrl=url}.TryCheckout(out uri);
   Assert(ok("p","https://example.gumroad.com/l/abc") && ok("p","https://gumroad.com/l/abc"));
   foreach(var url in new[]{null,"","http://example.gumroad.com/l/abc","https://gumroad.com.evil.example/l/abc","https://evilgumroad.com/l/abc","https://example.lemonsqueezy.com/buy/1","file:///C:/x.exe","gumroad.com/l/abc"})Assert(!ok("p",url),"Accepted "+url);
   Assert(!ok("","https://example.gumroad.com/l/abc"));
  });
  Test("Unconfigured installation stays Free",()=>Assert(!Licensing.Validate()));
  Test("Sample replay and backward clock cannot inflate coverage",()=>Temp(dir=>{
   using(var store=new Store(Path.Combine(dir,"test.sqlite"))){var s=new Sample{Time=now,Boot=now-100,Commit=40*Units.GiB,BootPeak=45*Units.GiB,Physical=32*Units.GiB,Mark="heavy"};
    var rows=new List<Tuple<Sample,int>>{Tuple.Create(s,1)};store.Record(rows);store.Record(rows);s.Time--;store.Record(rows);
    Assert(store.Evidence().ValidSeconds==1 && store.Evidence().Heavy==1);}
   using(var store=new Store(Path.Combine(dir,"test.sqlite"))){store.Record(new List<Tuple<Sample,int>>{Tuple.Create(new Sample{Time=now,Mark="heavy"},1)});Assert(store.Evidence().ValidSeconds==1);}
  }));
  Test("Release update verifies files and preserves store configuration",()=>Temp(dir=>{
   var src=Path.Combine(dir,"source");var dst=Path.Combine(dir,"installed");Directory.CreateDirectory(src);Directory.CreateDirectory(dst);
   foreach(var f in ReleaseUpdate.Files){File.WriteAllText(Path.Combine(src,f),f=="commerce.json"?"{}":"new");File.WriteAllText(Path.Combine(dst,f),f=="commerce.json"?"{\"GumroadProductId\":\"prod==\"}":"old");}
   bool verified=false;ReleaseUpdate.Apply(src,dst,Path.Combine(dir,"backup"),()=>verified=true);
   Assert(verified && File.ReadAllText(Path.Combine(dst,"PagefileMonitor.exe"))=="new");Assert(Json.Read<CommerceConfig>(File.ReadAllText(Path.Combine(dst,"commerce.json"))).Ready);
  }));
  Test("Failed update verification restores previous binaries",()=>Temp(dir=>{
   var src=Path.Combine(dir,"source");var dst=Path.Combine(dir,"installed");Directory.CreateDirectory(src);Directory.CreateDirectory(dst);
   foreach(var f in ReleaseUpdate.Files){File.WriteAllText(Path.Combine(src,f),f=="commerce.json"?"{}":"new");if(f!="commerce.json")File.WriteAllText(Path.Combine(dst,f),"old");}
   Throws(()=>ReleaseUpdate.Apply(src,dst,Path.Combine(dir,"backup"),()=>{throw new IOException("Service failed");}));
   Assert(File.ReadAllText(Path.Combine(dst,"PagefileMonitor.exe"))=="old" && !File.Exists(Path.Combine(dst,"commerce.json")));
  }));
  Test("Null paths are rejected as invalid configurations",()=>{string reason;Assert(!RecommendationEngine.HasSpace(SystemState(),new List<PageEntry>{new PageEntry()},out reason));});
  Test("Sizing includes 20 percent headroom and rounds upward",()=>{var r=RecommendationEngine.Calculate(Covered(),SystemState(),Policy(),now);Assert(r.Ready);Assert(r.CapacityMB==19*1024);});
  Test("Minimum headroom and allocation",()=>{var e=Covered();e.Peak=e.BootPeak=20*Units.GiB;var r=RecommendationEngine.Calculate(e,SystemState(),Policy(),now);Assert(r.Headroom==4*Units.GiB);Assert(r.CapacityMB==2048);});
  Test("Boot peak catches pre-monitoring spike",()=>{var e=Covered();e.BootPeak=60*Units.GiB;var r=RecommendationEngine.Calculate(e,SystemState(),Policy(),now);Assert(r.CapacityMB==40*1024);});
  Test("All learning requirements enforced",()=>{
   foreach(var change in new Action<Evidence>[] {e=>e.First=now-86400,e=>e.ValidSeconds=80000,e=>e.Light=0,e=>e.Normal=0,e=>e.Heavy=0,e=>e.Last=now-500,e=>e.Physical=0})
   {var e=Covered();change(e);Assert(!RecommendationEngine.Calculate(e,SystemState(),Policy(),now).Ready);}
   Assert(!RecommendationEngine.Calculate(Covered(),SystemState(),new Policy(),now).Ready);
   Assert(!RecommendationEngine.Calculate(Covered(),SystemState(),new Policy{HeavyConfirmed=true},now).Ready,"A confirmation given without recorded heavy sessions must not count");
  });
  Test("Coverage credits real elapsed time despite timer drift",()=>{
   var c=new CoverageClock();bool gap;long credited=0,ms=0;
   // 1,012 ms ticks: the wall clock skips a second about every 83 samples.
   for(int i=0;i<=1000;i++){credited+=c.Credit(ms,ms/1000,out gap);Assert(!gap);ms+=1012;}
   Assert(credited>=1011&&credited<=1012,"credited "+credited);
  });
  Test("Coverage excludes stalls, suspends and clock adjustments",()=>{
   var c=new CoverageClock();bool gap;Assert(c.Credit(0,100,out gap)==0&&!gap);Assert(c.Credit(1000,101,out gap)==1&&!gap);
   Assert(c.Credit(7000,107,out gap)==0&&gap,"stall");Assert(c.Credit(8000,108,out gap)==1&&!gap);
   Assert(c.Credit(9000,90,out gap)==0&&gap,"backward clock");Assert(c.Credit(10000,91,out gap)==1&&!gap);
   Assert(c.Credit(11000,4000,out gap)==0&&gap&&c.LastJumpSeconds>300,"forward jump");
   Assert(c.Credit(12000,4000,out gap)==0&&!gap,"same second is not stored twice");Assert(c.Credit(13000,4001,out gap)==2&&!gap);
  });
  Test("Samples, details and events commit as one batch",()=>Temp(dir=>{using(var store=new Store(Path.Combine(dir,"test.sqlite"))){
   var rows=new List<Tuple<Sample,int>>{Tuple.Create(new Sample{Time=now,Commit=Units.GiB,Physical=Units.GiB},1)};
   var details=new List<Tuple<long,string,string>>{Tuple.Create(now,"counters","{\"C: latency seconds\":0.001,\"C: transfers/sec\":5}")};
   var events=new List<Tuple<long,string,string>>{Tuple.Create(now,"gap","user's gap"),Tuple.Create(now,"gap","user's gap")};
   store.Record(rows,details,events);store.Record(new List<Tuple<Sample,int>>(),null,events);
   Assert(store.Evidence().ValidSeconds==1&&store.Evidence().Gaps);Assert(store.EventsText().Split('\n').Length==1);
   // A rejected batch stores nothing, so the caller can retry it whole.
   var bad=new List<Tuple<long,string,string>>{Tuple.Create(now+1,"gap","second")};var poison=new List<Tuple<long,string,string>>{Tuple.Create(now,(string)null,"{}")};
   using(var sql=new Sqlite(store.Path))sql.Execute("CREATE TRIGGER reject BEFORE INSERT ON detail WHEN NEW.kind IS NULL BEGIN SELECT RAISE(ABORT,'rejected'); END;");
   Throws(()=>store.Record(new List<Tuple<Sample,int>>{Tuple.Create(new Sample{Time=now+1,Commit=Units.GiB},1)},poison,bad));
   Assert(store.Evidence().ValidSeconds==1&&store.EventsText().Split('\n').Length==1);
   store.Record(new List<Tuple<Sample,int>>{Tuple.Create(new Sample{Time=now+1,Commit=Units.GiB},1)},null,bad);Assert(store.Evidence().ValidSeconds==2);
  }}));
  Test("Exhaustion review unblocks only the events that were reviewed",()=>Temp(dir=>{using(var store=new Store(Path.Combine(dir,"test.sqlite"))){
   Assert(!store.Evidence().Exhaustion&&store.Evidence().LastExhaustion==0);
   store.Event("exhaustion","Windows reported resource exhaustion",now-5000);store.Event("exhaustion","Windows reported resource exhaustion",now-4000);
   var e=store.Evidence();Assert(e.Exhaustion&&e.ExhaustionCount==2&&e.LastExhaustion==now-4000);
   Assert(store.Evidence(now-5000).Exhaustion,"Reviewing an older event must not cover a newer one");
   Assert(!store.Evidence(now-4000).Exhaustion&&store.Evidence(now-4000).ExhaustionCount==2);
   store.Event("exhaustion","Windows reported resource exhaustion",now-10);Assert(store.Evidence(now-4000).Exhaustion,"A later event blocks again");
   store.Event("exhaustion","Windows reported resource exhaustion",now-91*86400);Assert(store.Evidence().ExhaustionCount==3);
  }}));
  Test("Clock correction of the boot time is not treated as a restart",()=>Temp(dir=>{
   var f=new Fake();var manager=new ChangeManager(dir,f);manager.Policy.HeavyConfirmed=true;manager.Policy.HeavyConfirmedSeconds=1800;var r=RecommendationEngine.Calculate(Covered(),f.Read(),manager.Policy,now);manager.Apply(r,r.Id);
   f.Current.Boot-=3;Assert(manager.Observe(f.Read()).StartsWith("Restart required"));Assert(manager.Journal.Phase=="pending-restart");
   // A large clock step without a restart: uptime is still longer than the age of the change.
   f.Current.Boot+=7200;f.Current.Time=manager.Journal.Time+600;f.Current.UptimeSeconds=5*86400;manager.Observe(f.Read());Assert(manager.Journal.Phase=="pending-restart");
   f.Current.UptimeSeconds=120;f.Current.Active=f.Current.Entries.Select(x=>new ActivePage{Path=x.Path,AllocatedMB=x.InitialMB}).ToList();manager.Observe(f.Read());Assert(manager.Journal.Phase=="verified");
  }));
  Test("Exhaustion blocks automatic recommendations",()=>{var e=Covered();e.Exhaustion=true;Assert(!RecommendationEngine.Calculate(e,SystemState(),Policy(),now).Ready);});
  Test("Unsupported dump modes block",()=>{foreach(int mode in new[]{2,7,10}){var s=SystemState();s.DumpType=mode;Assert(!RecommendationEngine.Calculate(Covered(),s,Policy(),now).Ready);}});
  Test("Complete dump backs RAM plus header",()=>{var s=SystemState();s.DumpType=1;var r=RecommendationEngine.Calculate(Covered(),s,Policy(),now);Assert(r.Target.First(x=>x.Path.StartsWith("C:")).InitialMB>=33025);});
  Test("Low-space C relocates and preserves boot dump file",()=>{var s=SystemState();s.Volumes[0].Free=12*Units.GiB;var r=RecommendationEngine.Calculate(Covered(),s,Policy(),now);Assert(r.Ready&&r.Relocation);Assert(r.Target.Any(t=>t.Path.StartsWith("C:")&&t.InitialMB==1024));Assert(r.Target.Any(t=>t.Path.StartsWith("I:")));});
  Test("No capacity is explicitly blocked",()=>{var s=SystemState();s.Volumes.ForEach(v=>v.Free=0);Assert(!RecommendationEngine.Calculate(Covered(),s,Policy(),now).Ready);});
  Test("Unknown disks are not eligible",()=>{var s=SystemState();s.Volumes.ForEach(v=>v.Eligible=false);Assert(!RecommendationEngine.Calculate(Covered(),s,Policy(),now).Ready);});
  Test("Existing suitable placement preferred",()=>{var s=SystemState();s.Volumes[1].LatencyMs=.01;var r=RecommendationEngine.Calculate(Covered(),s,Policy(),now);Assert(r.Target.Count==1&&r.Target[0].Path.StartsWith("C:"));});
  Test("Two-stage relocation retains old allocation",()=>{var s=SystemState();var final=new List<PageEntry>{Entry("C:",1024),Entry("I:",16384)};var stage=RecommendationEngine.StageOne(s,final);Assert(stage.First(x=>x.Path.StartsWith("C:")).InitialMB==16384);Assert(final[0].InitialMB==1024);});
  Test("Temporary unchanged C allowed, final insufficient reserve rejected",()=>{var s=SystemState();s.Volumes[0].Free=12*Units.GiB;string why;Assert(!RecommendationEngine.HasSpace(s,s.Entries,out why));Assert(RecommendationEngine.HasSpace(s,s.Entries,out why,false,true));});
  Test("Invalid paths and duplicates rejected",()=>{var s=SystemState();string why;Assert(!RecommendationEngine.HasSpace(s,new List<PageEntry>{new PageEntry{Path=@"\\host\share\pagefile.sys",InitialMB=1,MaximumMB=1}},out why));Assert(!RecommendationEngine.HasSpace(s,new List<PageEntry>{Entry("C:",1024),Entry("C:",1024)},out why));});
  Test("Auto-grow requires review, exact locations, cooldown, ceiling",()=>{
   var s=SystemState();var r=RecommendationEngine.Calculate(Covered(),s,Policy(),now);var p=Policy();string why;
   p.AutoGrow=true;p.FirstReviewed=true;p.ApprovedFingerprint=s.Fingerprint();p.ApprovedPaths.Add(@"C:\pagefile.sys");
   Assert(RecommendationEngine.CanAutoGrow(p,s,r,null,now,out why));
   p.LastChange=now;Assert(!RecommendationEngine.CanAutoGrow(p,s,r,null,now,out why));p.LastChange=0;
   p.CeilingMB=16384;Assert(!RecommendationEngine.CanAutoGrow(p,s,r,null,now,out why));p.CeilingMB=65536;
   Assert(!RecommendationEngine.CanAutoGrow(p,s,r,new ChangeJournal{Phase="pending-restart"},now,out why));
   p.ApprovedPaths.Clear();Assert(!RecommendationEngine.CanAutoGrow(p,s,r,null,now,out why));
  });
  Test("Auto-grow never reduces allocations",()=>{var s=SystemState();s.Entries[0]=Entry("C:",32768);var r=RecommendationEngine.Calculate(Covered(),s,Policy(),now);var p=Policy();p.AutoGrow=p.FirstReviewed=true;p.ApprovedFingerprint=s.Fingerprint();p.ApprovedPaths.Add(@"C:\pagefile.sys");string why;Assert(!RecommendationEngine.CanAutoGrow(p,s,r,null,now,out why));});
  Test("Every privileged command rejects non-administrator",()=>{foreach(string command in new[]{"apply","policy","restore-original","restore-previous","system-managed","finish-move","confirm-heavy","acknowledge","review-exhaustion"})Throws(()=>Security.Authorize(command,false));Security.Authorize("status",false);});
  Test("Backup, journal and post-boot verification",()=>Temp(dir=>{var f=new Fake();var manager=new ChangeManager(dir,f);manager.Policy.HeavyConfirmed=true;manager.Policy.HeavyConfirmedSeconds=1800;var r=RecommendationEngine.Calculate(Covered(),f.Read(),manager.Policy,now);manager.Apply(r,r.Id);Assert(File.Exists(Path.Combine(dir,"backups","original.json")));Assert(File.Exists(Path.Combine(dir,"backups","original.txt")));Assert(manager.Journal.Phase=="pending-restart");f.Restart();manager.Observe(f.Read());Assert(manager.Journal.Phase=="verified");manager.Restore(true);Assert(f.Current.Entries[0].InitialMB==16384);}));
  Test("Partial write attempts rollback",()=>Temp(dir=>{var f=new Fake{FailOnce=true};var manager=new ChangeManager(dir,f);manager.Policy.HeavyConfirmed=true;manager.Policy.HeavyConfirmedSeconds=1800;var r=RecommendationEngine.Calculate(Covered(),f.Read(),manager.Policy,now);Throws(()=>manager.Apply(r,r.Id));Assert(manager.Journal.Phase=="rolled-back");Assert(f.Writes==2);Assert(f.Current.Entries[0].InitialMB==16384);Assert(!manager.Policy.AutoGrow);}));
  Test("Outside edits pause automation",()=>Temp(dir=>{var f=new Fake();var manager=new ChangeManager(dir,f);manager.Observe(f.Read());manager.Policy.AutoGrow=true;f.Current.Entries[0].MaximumMB++;manager.Observe(f.Read());Assert(!manager.Policy.AutoGrow&&!string.IsNullOrEmpty(manager.Policy.PauseReason));}));
  Test("Stale review cannot apply",()=>Temp(dir=>{var f=new Fake();var manager=new ChangeManager(dir,f);manager.Policy.HeavyConfirmed=true;manager.Policy.HeavyConfirmedSeconds=1800;var r=RecommendationEngine.Calculate(Covered(),f.Read(),manager.Policy,now);f.Current.Automatic=true;Throws(()=>manager.Apply(r,r.Id));Assert(f.Writes==0);}));
  Test("Interrupted transaction remains blocked after reopening",()=>Temp(dir=>{var f=new Fake();Json.Save(Path.Combine(dir,"journal.json"),new ChangeJournal{Phase="writing",Backup="original.json"});var manager=new ChangeManager(dir,f);manager.Observe(f.Read());Assert(manager.Policy.PauseReason.Contains("Interrupted"));}));
  Test("Restore per-drive automatic entry",()=>Temp(dir=>{var f=new Fake();f.Current.Entries[0]=Entry("C:",0);var manager=new ChangeManager(dir,f);manager.Baseline(f.Read());f.Current.Entries[0]=Entry("C:",16384);manager.Restore(true);Assert(!f.Current.Automatic&&f.Current.Entries[0].MaximumMB==0);}));
  Test("Restore refuses a replaced volume",()=>Temp(dir=>{var f=new Fake();var manager=new ChangeManager(dir,f);manager.Baseline(f.Read());f.Current.Volumes[0].Identity="different-drive";Throws(()=>manager.Restore(true));Assert(f.Writes==0);}));
  Test("Original automatic setting is restored",()=>Temp(dir=>{var f=new Fake();f.Current.Automatic=true;var manager=new ChangeManager(dir,f);manager.Baseline(f.Read());f.Current.Automatic=false;manager.Restore(true);Assert(f.Current.Automatic);}));
  Test("Destination verified before stage two",()=>Temp(dir=>{var f=new Fake();f.Current.Volumes[0].Free=12*Units.GiB;var manager=new ChangeManager(dir,f);manager.Policy.HeavyConfirmed=true;manager.Policy.HeavyConfirmedSeconds=1800;var r=RecommendationEngine.Calculate(Covered(),f.Read(),manager.Policy,now);manager.Apply(r,r.Id);Throws(()=>manager.FinishMove());f.Restart();manager.Observe(f.Read());Assert(manager.Journal.Phase=="stage-one-verified");manager.FinishMove();Assert(f.Current.Entries.First(x=>x.Path.StartsWith("C:")).InitialMB==1024);}));
  Test("Failed allocation verification disables auto-grow",()=>Temp(dir=>{var f=new Fake();var manager=new ChangeManager(dir,f);manager.Policy.HeavyConfirmed=true;manager.Policy.HeavyConfirmedSeconds=1800;var r=RecommendationEngine.Calculate(Covered(),f.Read(),manager.Policy,now);manager.Apply(r,r.Id);f.Current.Boot+=100;manager.Observe(f.Read());Assert(manager.Journal.Phase=="verification-failed"&&!manager.Policy.AutoGrow);}));
  Test("SQLite logs roundtrip, aggregate maxima, excluded gaps",()=>Temp(dir=>{using(var store=new Store(Path.Combine(dir,"test.sqlite"))){var sample=new Sample{Time=now,Boot=now-20,Commit=40*Units.GiB,BootPeak=45*Units.GiB,Physical=32*Units.GiB,Mark="heavy"};store.Record(new List<Tuple<Sample,int>>{Tuple.Create(sample,1)});sample=Json.Copy(sample);sample.Time++;sample.Commit=41*Units.GiB;store.Record(new List<Tuple<Sample,int>>{Tuple.Create(sample,0)});var e=store.Evidence();Assert(e.Peak==41*Units.GiB&&e.BootPeak==45*Units.GiB&&e.Heavy==1&&e.ValidSeconds==1);Assert(store.HistoryCsv().Contains("peak_committed_bytes"));store.Event("gap","simulated");Assert(store.Evidence().Gaps);}}));
  Test("Raw pruning preserves minute maxima",()=>Temp(dir=>{using(var store=new Store(Path.Combine(dir,"test.sqlite"))){store.Record(new List<Tuple<Sample,int>>{Tuple.Create(new Sample{Time=now-8*86400,Commit=99*Units.GiB,BootPeak=99*Units.GiB,Physical=32*Units.GiB},1)});store.Prune(now);Assert(store.Evidence().Peak==99*Units.GiB);}}));
  Test("SQLite full-disk error leaves previous records",()=>Temp(dir=>{using(var sql=new Sqlite(Path.Combine(dir,"test.sqlite"))){sql.Execute("CREATE TABLE t(value TEXT);INSERT INTO t VALUES('kept');PRAGMA max_page_count=2;");Throws(()=>sql.Execute("INSERT INTO t VALUES(zeroblob(1000000));"));Assert(sql.Query("SELECT value FROM t;")[0]["value"]=="kept");}}));
  Test("Unicode JSON and quoted text retained",()=>Temp(dir=>{var file=Path.Combine(dir,"unicode.json");Json.Save(file,new Dictionary<string,string>{{"name","測定 user's process"}});Assert(Json.Read<Dictionary<string,string>>(File.ReadAllText(file))["name"].StartsWith("測定"));using(var sql=new Sqlite(Path.Combine(dir,"test.sqlite"))){sql.Execute("CREATE TABLE t(value TEXT);INSERT INTO t VALUES("+Sqlite.Q("測定 user's process")+");");Assert(sql.Query("SELECT value FROM t;")[0]["value"]=="測定 user's process");}}));
  Console.WriteLine($"RESULT: {passed} passed, {failures} failed");return failures==0?0:1;
 }
}
