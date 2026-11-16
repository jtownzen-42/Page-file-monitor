using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;

namespace PagefileMonitor
{
    public interface IConfigurationBackend { Snapshot Read(); void Write(Snapshot desired); }
    public sealed class WindowsConfigurationBackend : IConfigurationBackend
    {
        public Snapshot Read()=>WindowsMetrics.ReadSnapshot();
        public void Write(Snapshot desired)
        {
            if(!Security.IsAdmin()) throw new UnauthorizedAccessException("Administrator rights required");
            var scope=new ManagementScope(@"\\.\root\cimv2",new ConnectionOptions {EnablePrivileges=true,Impersonation=ImpersonationLevel.Impersonate}); scope.Connect();
            using(var search=new ManagementObjectSearcher(scope,new ObjectQuery("SELECT * FROM Win32_ComputerSystem")))
            using(var results=search.Get()) foreach(ManagementObject cs in results) using(cs) {cs["AutomaticManagedPagefile"]=desired.Automatic;cs.Put();}
            if(desired.Automatic) return;
            using(var cls=new ManagementClass(scope,new ManagementPath("Win32_PageFileSetting"),null))
            {
                using(var existing=cls.GetInstances()) foreach(ManagementObject item in existing) using(item)
                    if(!desired.Entries.Any(x=>string.Equals(x.Path,(string)item["Name"],StringComparison.OrdinalIgnoreCase))) item.Delete();
                foreach(var entry in desired.Entries) using(var item=cls.CreateInstance())
                { item["Name"]=entry.Path;item["InitialSize"]=entry.InitialMB;item["MaximumSize"]=entry.MaximumMB;item.Put(); }
            }
        }
    }
    public static class Security
    {
        public static bool IsAdmin()=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        public static string Root=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"PagefileMonitor");
        public static bool Mutating(string command)=>new[]{"apply","finish-move","restore-original","restore-previous","system-managed","policy","confirm-heavy","acknowledge","review-exhaustion"}.Contains(command);
        public static void Authorize(string command,bool administrator)
        { if(Mutating(command) && !administrator) throw new UnauthorizedAccessException("This operation requires an elevated administrator client."); }
    }
    public sealed class ChangeManager
    {
        readonly string root;
        readonly IConfigurationBackend backend;
        public Policy Policy {get;private set;}
        public ChangeJournal Journal {get;private set;}
        public ChangeManager(string folder,IConfigurationBackend configuration)
        {
            root=folder;backend=configuration;Directory.CreateDirectory(BackupDir);
            Policy=Load<Policy>("policy.json")??new Policy(); Journal=Load<ChangeJournal>("journal.json");
        }
        string BackupDir=>Path.Combine(root,"backups");
        T Load<T>(string file) where T:class => File.Exists(Path.Combine(root,file))?Json.Read<T>(File.ReadAllText(Path.Combine(root,file))):null;
        public void SavePolicy()=>Json.Save(Path.Combine(root,"policy.json"),Policy);
        void SaveJournal()=>Json.Save(Path.Combine(root,"journal.json"),Journal);
        public void Baseline(Snapshot snapshot)
        {
            if(snapshot.Error!=null) throw new InvalidOperationException("Cannot back up incomplete configuration");
            if(!File.Exists(Path.Combine(BackupDir,"original.json"))) Backup(snapshot,"original");
        }
        string Backup(Snapshot snapshot,string name=null)
        {
            string file=(name??DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))+".json";
            Json.Save(Path.Combine(BackupDir,file),snapshot);
            string text="Pagefile Monitor configuration backup\r\nUTC: "+DateTimeOffset.FromUnixTimeSeconds(snapshot.Time).ToString("O")+
                "\r\nAutomatic management: "+snapshot.Automatic+"\r\nConfigured entries:\r\n"+string.Join("\r\n",snapshot.Entries.Select(x=>x.ToString()))+
                "\r\nActive allocations:\r\n"+string.Join("\r\n",snapshot.Active.Select(x=>x.Path+" = "+x.AllocatedMB+" MB"))+
                "\r\nCrash dump type: "+snapshot.DumpType+"\r\nComplete snapshot (including raw values and volume identities):\r\n"+Json.Write(snapshot)+
                "\r\nRestore with PagefileMonitor.Recovery.exe. Restoration may require a restart; this backup does not reserve disk space.\r\n";
            Json.SaveText(Path.ChangeExtension(Path.Combine(BackupDir,file),".txt"),text);
            if(!File.Exists(Path.Combine(BackupDir,file)) || !File.Exists(Path.ChangeExtension(Path.Combine(BackupDir,file),".txt"))) throw new IOException("Backup not durable");
            return file;
        }
        public static bool ConfigurationMatches(Snapshot a,Snapshot b)
        {
            if(a.Error!=null || a.Automatic!=b.Automatic) return false;
            if(a.Automatic) return true;
            return a.Entries.Count==b.Entries.Count && b.Entries.All(t=>a.Entries.Any(x=>string.Equals(x.Path,t.Path,StringComparison.OrdinalIgnoreCase)&&x.InitialMB==t.InitialMB&&x.MaximumMB==t.MaximumMB));
        }
        static bool ActiveMatches(Snapshot now,Snapshot desired)
        {
            if(now.Error!=null) return false;
            if(desired.Automatic) return now.Active.Count>0 && now.Active.All(x=>!x.Temporary);
            return desired.Entries.All(t=>now.Active.Any(a=>string.Equals(a.Path,t.Path,StringComparison.OrdinalIgnoreCase)&&!a.Temporary&&a.AllocatedMB>0&&(t.InitialMB==0&&t.MaximumMB==0 || a.AllocatedMB>=t.InitialMB&&a.AllocatedMB<=t.MaximumMB)))
                && now.Active.All(a=>desired.Entries.Any(t=>string.Equals(a.Path,t.Path,StringComparison.OrdinalIgnoreCase)));
        }
        // Windows shifts its reported boot time by seconds whenever the clock is corrected; that is not a restart.
        // A real restart moves the boot time well forward and leaves an uptime shorter than the age of the change.
        public static bool Restarted(Snapshot now,ChangeJournal journal)=>
            Math.Abs(now.Boot-journal.Boot)>30 && (now.UptimeSeconds<=0 || now.UptimeSeconds<now.Time-journal.Time+30);
        public string Observe(Snapshot now)
        {
            if(now.Error!=null) return "Configuration snapshot incomplete";
            if(Journal!=null && Journal.Phase=="writing")
            {
                Policy.AutoGrow=false;Policy.PauseReason="Interrupted configuration transaction. Review recovery before continuing.";SavePolicy();return Policy.PauseReason;
            }
            if(Journal!=null && Journal.Phase=="pending-restart" && Restarted(now,Journal))
            {
                if(ConfigurationMatches(now,Journal.Desired) && ActiveMatches(now,Journal.Desired) && now.Fingerprint()==Journal.ExpectedFingerprint)
                {
                    Journal.Phase=Journal.FinalTarget!=null?"stage-one-verified":"verified";
                    Policy.ApprovedFingerprint=now.Fingerprint();SavePolicy();SaveJournal();
                    return Journal.FinalTarget!=null?"Destination verified. Review and finish relocation.":"Settings verified after restart";
                }
                Journal.Phase="verification-failed";Journal.Error="Active allocations do not match the requested configuration.";SaveJournal();
                Policy.AutoGrow=false;Policy.PauseReason=Journal.Error;SavePolicy();return Journal.Error;
            }
            string expected=Journal!=null && Journal.Pending?Journal.ExpectedFingerprint:Policy.ApprovedFingerprint;
            if(expected!=null && now.Fingerprint()!=expected)
            { Policy.AutoGrow=false;Policy.PauseReason="Outside configuration change detected; review and acknowledge.";SavePolicy();return Policy.PauseReason; }
            if(Policy.ApprovedFingerprint==null) {Policy.ApprovedFingerprint=now.Fingerprint();SavePolicy();}
            return Journal!=null && Journal.Phase=="pending-restart"?"Restart required; active allocation has not yet been verified.":"Monitoring";
        }
        public void Apply(Recommendation r,string id,bool automated=false)
        {
            if(!r.Ready || r.Id!=id) throw new InvalidOperationException("Recommendation is provisional or changed. Refresh and review it.");
            if(Journal!=null && Journal.Pending) throw new InvalidOperationException("A change is already pending");
            if(!string.IsNullOrEmpty(Policy.PauseReason)) throw new InvalidOperationException(Policy.PauseReason);
            var current=backend.Read();
            if(current.Fingerprint()!=r.SourceFingerprint)throw new InvalidOperationException("Configuration changed since the recommendation. Refresh before applying.");
            if(automated)
            { string why; if(!RecommendationEngine.CanAutoGrow(Policy,current,r,Journal,Units.Now,out why))throw new InvalidOperationException(why); }
            else Policy.FirstReviewed=true;
            var desired=Json.Copy(current);desired.Automatic=false;
            desired.Entries=r.Relocation?RecommendationEngine.StageOne(current,r.Target):Json.Copy(r.Target);
            string reason;if(!RecommendationEngine.HasSpace(current,desired.Entries,out reason,false,r.Relocation))throw new InvalidOperationException("Allocation stage blocked: "+reason);
            Commit(current,desired,r.Relocation?Json.Copy(r.Target):null);
        }
        public void FinishMove()
        {
            if(Journal==null || Journal.Phase!="stage-one-verified" || Journal.FinalTarget==null)throw new InvalidOperationException("No verified relocation to finish");
            var current=backend.Read();
            if(!ActiveMatches(current,Journal.Desired) || !ConfigurationMatches(current,Journal.Desired))throw new InvalidOperationException("Destination is no longer verified");
            var desired=Json.Copy(current);desired.Entries=Json.Copy(Journal.FinalTarget);desired.Automatic=false;
            string why;if(!RecommendationEngine.HasSpace(current,desired.Entries,out why))throw new InvalidOperationException(why);
            Commit(current,desired,null);
        }
        public void Restore(bool original)
        {
            string file=original?"original.json":Journal?.Backup;
            if(string.IsNullOrEmpty(file) || file!=Path.GetFileName(file))throw new InvalidOperationException("No valid backup is available");
            var backup=Json.Read<Snapshot>(File.ReadAllText(Path.Combine(BackupDir,file)));
            var current=backend.Read();
            if(current.Error!=null)throw new InvalidOperationException(current.Error);
            if(!backup.Automatic)
            {
                foreach(var entry in backup.Entries)
                {
                    string drive=RecommendationEngine.Drive(entry.Path);
                    var old=backup.Volumes.FirstOrDefault(v=>v.Drive==drive);var present=current.Volumes.FirstOrDefault(v=>v.Drive==drive);
                    if(old==null || present==null || old.Identity!=present.Identity)throw new InvalidOperationException("Backup drive identity changed: "+drive);
                }
                string reason;if(!RecommendationEngine.HasSpace(current,backup.Entries,out reason,true))throw new InvalidOperationException(reason);
            }
            else ValidateAutomaticSpace(current);
            var desired=Json.Copy(current);desired.Automatic=backup.Automatic;desired.Entries=Json.Copy(backup.Entries);
            Policy.AutoGrow=false;Policy.PauseReason=null;Commit(current,desired,null);
        }
        static void ValidateAutomaticSpace(Snapshot current)
        {
            if(!current.Volumes.Any(v=>v.Free>=Units.GiB && (v.Eligible || v.Drive==current.WindowsDrive)))throw new InvalidOperationException("Insufficient available space to return to automatic management");
        }
        public void SystemManaged()
        {
            var current=backend.Read();if(current.Error!=null)throw new InvalidOperationException(current.Error);
            ValidateAutomaticSpace(current);var desired=Json.Copy(current);desired.Automatic=true;
            Policy.AutoGrow=false;Policy.PauseReason=null;Commit(current,desired,null);
        }
        void Commit(Snapshot current,Snapshot desired,List<PageEntry> final)
        {
            if(current.Error!=null)throw new InvalidOperationException(current.Error);
            Baseline(current);string backup=Backup(current);
            Journal=new ChangeJournal {Id=Guid.NewGuid().ToString("N"),Phase="writing",Backup=backup,Time=Units.Now,Boot=current.Boot,Desired=desired,FinalTarget=final};SaveJournal();
            try
            {
                backend.Write(desired);var after=backend.Read();
                if(!ConfigurationMatches(after,desired))throw new InvalidOperationException("Configuration read-back failed");
                Journal.ExpectedFingerprint=after.Fingerprint();Journal.Phase="pending-restart";
                Policy.LastChange=Units.Now;Policy.ApprovedFingerprint=after.Fingerprint();SavePolicy();SaveJournal();
            }
            catch(Exception ex)
            {
                Journal.Error=ex.Message;
                try { backend.Write(current);if(!ConfigurationMatches(backend.Read(),current))throw new InvalidOperationException("Rollback read-back failed");Journal.Phase="rolled-back"; }
                catch(Exception rollback) {Journal.Phase="rollback-failed";Journal.Error+="; "+rollback.Message;}
                Policy.AutoGrow=false;Policy.PauseReason=Journal.Error;SavePolicy();SaveJournal();throw new InvalidOperationException(Journal.Phase+": "+Journal.Error);
            }
        }
    }
}
