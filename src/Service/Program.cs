using PagefileMonitor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace PagefileMonitorService
{
    public sealed class MonitorHost : IDisposable
    {
        // gate guards in-memory state only. Disk and WMI work happens outside it so a busy disk cannot delay sampling.
        readonly object gate=new object(),counterGate=new object();readonly string root;readonly bool readOnly;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly SemaphoreSlim wake=new SemaphoreSlim(0);
        Store store;ChangeManager changes;Counters counters;Task sampling,writing,server;
        readonly List<Tuple<Sample,int>> buffer=new List<Tuple<Sample,int>>();
        readonly List<Tuple<long,string,string>> details=new List<Tuple<long,string,string>>(),events=new List<Tuple<long,string,string>>();
        readonly Status status=new Status();string mark="unmarked";long lastEvent,boot;int refreshes;Dictionary<string,double> latency;
        volatile bool slowBusy,inventoryHealthy;bool samplingHealthy=true,storageHealthy=true;NamedPipeServerStream listener;
        public MonitorHost(string folder,bool readOnlyMode=false) {root=folder;readOnly=readOnlyMode;}
        public void Start(bool serve=true)
        {
            status.ServiceVersion=typeof(MonitorHost).Assembly.GetName().Version.ToString();status.ReadOnly=readOnly;
            Directory.CreateDirectory(root);store=new Store(Path.Combine(root,"telemetry.sqlite"));
            changes=new ChangeManager(root,new WindowsConfigurationBackend());
            var snapshot=WindowsMetrics.ReadSnapshot();status.Snapshot=snapshot;
            inventoryHealthy=snapshot.Error==null;
            if(snapshot.Error==null) {boot=snapshot.Boot;changes.Baseline(snapshot);status.Health=changes.Observe(snapshot);}
            else status.Health=snapshot.Error;
            try{counters=new Counters(snapshot.Volumes);}catch(Exception ex){status.Health="Counters unavailable: "+ex.Message;}
            lastEvent=Units.Now-14*86400;
            store.Event("start","Collector started; workload marker reset. Boot UTC seconds: "+snapshot.Boot);
            sampling=Task.Run(()=>Loop());writing=Task.Run(()=>Writer());if(serve)server=Task.Run(()=>Serve());
        }
        // Callers hold gate. Events are written with the next batch.
        void Event(string kind,string message)
        {
            if(events.Count>0 && events[events.Count-1].Item2==kind && events[events.Count-1].Item3==message && kind!="gap")return;
            events.Add(Tuple.Create(Units.Now,kind,message));
        }
        void Trim()
        {
            // Bounded memory while storage is unavailable; dropped samples earn no coverage.
            if(buffer.Count>600)buffer.RemoveRange(0,buffer.Count-600);
            if(details.Count>300)details.RemoveRange(0,details.Count-300);
            if(events.Count>300)events.RemoveRange(0,events.Count-300);
        }
        void Signal(){if(wake.CurrentCount==0)wake.Release();}
        async Task Loop()
        {
            var clock=Stopwatch.StartNew();var coverage=new CoverageClock();int tick=0;
            while(!cancel.IsCancellationRequested)
            {
                long started=clock.ElapsedMilliseconds;
                try
                {
                    string current;long bootTime;lock(gate){current=mark;bootTime=boot;}
                    var sample=WindowsMetrics.ReadMemory(bootTime);sample.Mark=current;
                    bool gap;int valid=coverage.Credit(started,sample.Time,out gap);
                    Dictionary<string,double?> read=null;List<ProcessSample> processes=null;
                    if(tick%5==0)lock(counterGate)if(counters!=null)read=counters.Read();
                    if(tick%30==0)processes=WindowsMetrics.ReadProcesses();
                    lock(gate)
                    {
                        if(gap)
                        {
                            Event("gap","Collection gap or wall-clock adjustment; duration excluded from coverage.");
                            // A marker set before a suspend or long stall no longer describes what is running.
                            if(coverage.LastJumpSeconds>300 && mark!="unmarked"){mark="unmarked";sample.Mark=mark;Event("marker","unmarked after "+coverage.LastJumpSeconds+" seconds without collection");}
                        }
                        status.Sample=sample;samplingHealthy=true;buffer.Add(Tuple.Create(sample,valid));
                        if(read!=null){status.Counters=read;details.Add(Tuple.Create(sample.Time,"counters",Json.Write(read)));}
                        if(processes!=null){status.Processes=processes;details.Add(Tuple.Create(sample.Time,"processes",Json.Write(processes)));}
                        Trim();
                    }
                }
                catch(Exception ex)
                {lock(gate){status.Health="Collection error: "+ex.Message;samplingHealthy=false;Event("collection-error",ex.Message);}}
                if(tick%10==0)Signal();
                if(tick>0 && (tick%60==0 || (tick%5==0 && !inventoryHealthy)) && !slowBusy){slowBusy=true;var refresh=Task.Run(()=>SlowRefresh());}
                tick++;
                try{await Task.Delay((int)Math.Max(20,1000-(clock.ElapsedMilliseconds-started)),cancel.Token);}catch(TaskCanceledException){}
            }
        }
        async Task Writer()
        {
            while(true)
            {
                try{await wake.WaitAsync(TimeSpan.FromSeconds(15),cancel.Token);}catch(OperationCanceledException){}
                bool final=cancel.IsCancellationRequested;
                Flush();
                if(final)break;
            }
        }
        void Flush()
        {
            List<Tuple<Sample,int>> rows;List<Tuple<long,string,string>> items,logged;long reviewed;
            lock(gate){rows=new List<Tuple<Sample,int>>(buffer);items=new List<Tuple<long,string,string>>(details);logged=new List<Tuple<long,string,string>>(events);buffer.Clear();details.Clear();events.Clear();reviewed=changes.Policy.ExhaustionReviewedThrough;}
            try{store.Record(rows,items,logged);}
            catch(Exception ex)
            {
                lock(gate)
                {
                    // The batch is one transaction, so nothing was stored; keep it for the next attempt.
                    buffer.InsertRange(0,rows);details.InsertRange(0,items);events.InsertRange(0,logged);
                    storageHealthy=false;status.Health="Telemetry storage error: "+ex.Message;Event("collection-error",ex.Message);Trim();
                }
                try{store.Prune(Units.Now);}catch(Exception){}
                return;
            }
            try
            {
                var evidence=store.Evidence(reviewed);
                lock(gate)
                {
                    storageHealthy=true;
                    if(status.Sample!=null)evidence.Physical=status.Sample.Physical;
                    status.Evidence=evidence;status.Recommendation=RecommendationEngine.Calculate(evidence,status.Snapshot,changes.Policy,Units.Now);
                    status.Policy=changes.Policy;status.Journal=changes.Journal;
                }
            }
            catch(Exception ex){lock(gate){storageHealthy=false;status.Health="Telemetry storage error: "+ex.Message;Event("collection-error",ex.Message);}}
        }
        void SlowRefresh()
        {
            try
            {
                bool pro=!readOnly && Licensing.Validate();
                var snapshot=WindowsMetrics.ReadSnapshot();
                // Averaging 12,000 counter rows is the most expensive read; drive latency changes slowly.
                if(latency==null || refreshes%10==0)latency=store.DiskLatencies();
                foreach(var v in snapshot.Volumes)if(latency.ContainsKey(v.Drive))v.LatencyMs=latency[v.Drive];
                long checkedAt=Units.Now;var exhaustion=WindowsMetrics.ExhaustionEvents(lastEvent);
                foreach(var time in exhaustion)store.Event("exhaustion","Windows reported resource exhaustion",time);
                lastEvent=checkedAt-60;
                store.Detail("snapshot",snapshot);
                if(refreshes%10==0)store.Prune(Units.Now);
                refreshes++;
                lock(gate)
                {
                    status.Pro=pro;
                    if(snapshot.Error==null && boot==0)boot=snapshot.Boot;
                    bool volumesChanged=string.Join(",",status.Snapshot.Volumes.Select(v=>v.Drive))!=string.Join(",",snapshot.Volumes.Select(v=>v.Drive));
                    status.Snapshot=snapshot;
                    if(volumesChanged)lock(counterGate){counters?.Dispose();counters=null;counters=new Counters(snapshot.Volumes);}
                    inventoryHealthy=snapshot.Error==null;
                    if(snapshot.Error==null)changes.Baseline(snapshot);
                    status.Health=changes.Observe(snapshot);
                    if(status.Evidence!=null)
                    {
                        status.Recommendation=RecommendationEngine.Calculate(status.Evidence,snapshot,changes.Policy,Units.Now);
                        string reason;
                        if(status.Pro && !readOnly && samplingHealthy && storageHealthy && inventoryHealthy && RecommendationEngine.CanAutoGrow(changes.Policy,snapshot,status.Recommendation,changes.Journal,Units.Now,out reason))
                        {changes.Apply(status.Recommendation,status.Recommendation.Id,true);Event("auto-grow","Growth configured; restart required.");}
                    }
                    status.Policy=changes.Policy;status.Journal=changes.Journal;
                }
            }
            catch(Exception ex){lock(gate){status.Health="Inventory/event refresh error: "+ex.Message;inventoryHealthy=false;Event("collection-error",ex.Message);}}
            finally{slowBusy=false;}
        }
        public Reply Handle(Request request,bool admin)
        {
            try
            {
                // Read-only exports query the database without holding up sampling.
                if(request.Command=="history")return new Reply {Ok=true,Data=store.HistoryCsv()};
                if(request.Command=="events")return new Reply {Ok=true,Data=store.EventsText()};
            }
            catch(Exception ex){return new Reply {Ok=false,Message=ex.Message};}
            lock(gate)
            {
                try
                {
                    Security.Authorize(request.Command,admin);
                    if(readOnly && Security.Mutating(request.Command))throw new InvalidOperationException("Read-only validation mode");
                    bool healthy=samplingHealthy && storageHealthy && inventoryHealthy;
                    switch(request.Command)
                    {
                        case "status":
                            var view=Json.Copy(status);
                            if(!healthy || view.Sample==null || Units.Now-view.Sample.Time>10)
                            {if(view.Recommendation!=null){view.Recommendation.Ready=false;view.Recommendation.Reasons.Add("Collection is incomplete or stale; wait for healthy measurements.");}}
                            return new Reply {Ok=true,Status=view};
                        case "mark":
                            if(!new[]{"unmarked","light","normal","heavy"}.Contains(request.Value))throw new ArgumentException("Invalid workload marker");
                            mark=request.Value;Event("marker",mark);break;
                        case "confirm-heavy":
                            long heavy=request.Enabled?store.Evidence().Heavy:0;
                            if(request.Enabled && heavy<1800)throw new InvalidOperationException("Mark at least 30 minutes of heavy workload before confirming that it is representative.");
                            changes.Policy.HeavyConfirmed=request.Enabled;changes.Policy.HeavyConfirmedSeconds=heavy;changes.SavePolicy();break;
                        case "review-exhaustion":
                            long latest=store.Evidence().LastExhaustion,seen;
                            if(latest==0)throw new InvalidOperationException("No resource-exhaustion events are recorded in the last 90 days.");
                            if(!long.TryParse(request.Value,out seen) || seen!=latest)throw new InvalidOperationException("The recorded events changed since they were displayed. Refresh and review them again.");
                            changes.Policy.ExhaustionReviewedThrough=latest;changes.SavePolicy();break;
                        case "acknowledge":
                            if(changes.Journal!=null && changes.Journal.Pending)throw new InvalidOperationException("Resolve the pending transaction first");
                            changes.Policy.PauseReason=null;changes.Policy.AutoGrow=false;changes.Policy.ApprovedFingerprint=WindowsMetrics.ReadSnapshot().Fingerprint();changes.SavePolicy();break;
                        case "apply":
                            if(!healthy)throw new InvalidOperationException("Telemetry is unhealthy; resolve collection errors first");
                            var current=WindowsMetrics.ReadSnapshot();var evidence=store.Evidence(changes.Policy.ExhaustionReviewedThrough);
                            if(status.Sample!=null)evidence.Physical=status.Sample.Physical;
                            foreach(var v in current.Volumes){var prior=status.Snapshot.Volumes.FirstOrDefault(x=>x.Drive==v.Drive);v.LatencyMs=prior?.LatencyMs;}
                            var r=RecommendationEngine.Calculate(evidence,current,changes.Policy,Units.Now);changes.Apply(r,request.Value);break;
                        case "finish-move":changes.FinishMove();break;
                        case "restore-original":changes.Restore(true);break;
                        case "restore-previous":changes.Restore(false);break;
                        case "system-managed":changes.SystemManaged();break;
                        case "policy":
                            if(request.Enabled && !status.Pro)throw new InvalidOperationException("Auto-grow requires an active Pro license. Free monitoring and recovery remain available.");
                            if(request.CeilingMB<2048 || request.CeilingMB>1048576)throw new ArgumentException("Ceiling must be between 2 and 1024 GiB");
                            var now=WindowsMetrics.ReadSnapshot();
                            if(request.Enabled && (!changes.Policy.FirstReviewed || now.Automatic || now.Error!=null || changes.Journal==null || changes.Journal.Phase!="verified" || now.Entries.Any(x=>x.InitialMB!=x.MaximumMB) || !string.IsNullOrEmpty(changes.Policy.PauseReason)))
                                throw new InvalidOperationException("First apply and verify a reviewed fixed allocation after restart.");
                            changes.Policy.AutoGrow=request.Enabled;changes.Policy.CeilingMB=request.CeilingMB;
                            changes.Policy.ApprovedPaths=now.Entries.Select(x=>x.Path).ToList();changes.Policy.ApprovedFingerprint=now.Fingerprint();changes.SavePolicy();break;
                        default:throw new ArgumentException("Unknown command");
                    }
                    status.Policy=changes.Policy;status.Journal=changes.Journal;Event("action",request.Command);
                    if(status.Evidence!=null && status.Snapshot!=null)
                    {
                        // Reflect a review or confirmation immediately instead of waiting for the next batch.
                        status.Evidence.Exhaustion=status.Evidence.LastExhaustion>changes.Policy.ExhaustionReviewedThrough;
                        status.Recommendation=RecommendationEngine.Calculate(status.Evidence,status.Snapshot,changes.Policy,Units.Now);
                    }
                    Signal();
                    return new Reply {Ok=true,Message=Security.Mutating(request.Command)?"Action completed. Page-file changes, if any, require restart and verification.":"Updated"};
                }
                catch(Exception ex){return new Reply {Ok=false,Message=ex.Message};}
            }
        }
        async Task Serve()
        {
            var acl=new PipeSecurity();
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                acl.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
            string owner=File.ReadAllText(Path.Combine(root,"owner.sid")).Trim();
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(owner),PipeAccessRights.ReadWrite,AccessControlType.Allow));
            while(!cancel.IsCancellationRequested)
            {
                try
                {
                    using(var pipe=new NamedPipeServerStream(PipeProtocol.Name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,acl))
                    {
                        listener=pipe;await pipe.WaitForConnectionAsync(cancel.Token);
                        bool admin=false;pipe.RunAsClient(()=>admin=Security.IsAdmin());
                        Request request=Json.Read<Request>(await PipeProtocol.Read(pipe,65536));
                        await PipeProtocol.Write(pipe,Json.Write(Handle(request,admin)));
                    }
                }
                catch(OperationCanceledException){break;}
                catch(ObjectDisposedException){if(cancel.IsCancellationRequested)break;}
                catch(Exception ex){lock(gate)Event("ipc-error",ex.GetType().Name);try{await Task.Delay(50,cancel.Token);}catch(OperationCanceledException){break;}}
            }
        }
        public Status ReadStatus(){lock(gate)return Json.Copy(status);}
        public void Dispose()
        {
            cancel.Cancel();listener?.Dispose();
            try{sampling?.Wait(15000);writing?.Wait(15000);server?.Wait(2000);}catch(AggregateException){}
            var wait=Stopwatch.StartNew();while(slowBusy && wait.ElapsedMilliseconds<20000)Thread.Sleep(100);
            lock(counterGate){counters?.Dispose();counters=null;}store?.Dispose();cancel.Dispose();wake.Dispose();
        }
    }
    sealed class MonitorService : ServiceBase
    {
        MonitorHost host;
        public MonitorService(){ServiceName="PagefileMonitor";CanStop=true;}
        protected override void OnStart(string[] args){host=new MonitorHost(Security.Root);host.Start();}
        protected override void OnStop(){host?.Dispose();}
    }
    static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if(args.Contains("--probe")){Console.WriteLine(Json.Write(new {Snapshot=WindowsMetrics.ReadSnapshot(),Memory=WindowsMetrics.ReadMemory(0)}));return 0;}
                if((args.Length==4 || args.Length==5) && args[0]=="--measure" && args[2]=="--seconds")
                {
                    int seconds=int.Parse(args[3]);if(seconds<10 || seconds>3600)throw new ArgumentException("Use 10–3600 seconds");
                    if(args[1]==null || args[1].Contains(".."))throw new ArgumentException("Invalid file path");
                    using(var host=new MonitorHost(Path.GetFullPath(args[1]),true))
                    using(var process=Process.GetCurrentProcess())
                    {
                        bool serve=args.Contains("--serve");
                        if(serve){Directory.CreateDirectory(Path.GetFullPath(args[1]));File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]),"owner.sid"),WindowsIdentity.GetCurrent().User.Value);}
                        var elapsed=Stopwatch.StartNew();var cpu=process.TotalProcessorTime;host.Start(serve);long maxWorking=0;
                        for(int i=0;i<seconds;i++){Thread.Sleep(1000);process.Refresh();maxWorking=Math.Max(maxWorking,process.WorkingSet64);}
                        Console.WriteLine(Json.Write(new {ElapsedSeconds=elapsed.Elapsed.TotalSeconds,AverageCpuPercent=(process.TotalProcessorTime-cpu).TotalSeconds/elapsed.Elapsed.TotalSeconds/Environment.ProcessorCount*100,PeakWorkingSetBytes=maxWorking,Status=host.ReadStatus()}));
                    }
                    return 0;
                }
                ServiceBase.Run(new MonitorService());return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        }
    }
}
