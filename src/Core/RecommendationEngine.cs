using System;
using System.Collections.Generic;
using System.Linq;

namespace PagefileMonitor
{
    public static class RecommendationEngine
    {
        public static long Reserve(Volume v) => Math.Max(20*Units.GiB,v.Size/10);
        public static string Drive(string path) => System.IO.Path.GetPathRoot(path).TrimEnd('\\').ToUpperInvariant();
        public static Recommendation Calculate(Evidence e, Snapshot s, Policy p, long now)
        {
            var r=new Recommendation { Peak=Math.Max(e.Peak,e.BootPeak),SourceFingerprint=s.Fingerprint() };
            r.Headroom=Math.Max(4*Units.GiB,(long)Math.Ceiling(r.Peak*.2));
            r.CapacityMB=Math.Max(2048,checked((long)Math.Ceiling((r.Peak+r.Headroom-e.Physical)/(double)Units.GiB)*1024));
            if(e.First==0 || now-e.First<7*86400) r.Reasons.Add("Learning: seven elapsed days required.");
            if(e.ValidSeconds<86400) r.Reasons.Add("Learning: 24 hours of valid samples required.");
            if(e.Light<1800 || e.Normal<7200 || e.Heavy<1800) r.Reasons.Add("Mark at least 30 minutes light, two hours normal and 30 minutes heavy workload.");
            if(!p.HeavyConfirmed || p.HeavyConfirmedSeconds<1800) r.Reasons.Add("Confirm that marked heavy sessions represent your demanding workloads.");
            if(e.Physical<=0 || r.Peak<=0 || s.Error!=null) r.Reasons.Add("Essential system measurements unavailable.");
            if(e.Last==0 || now-e.Last>120) r.Reasons.Add("Measurements are stale; resume collection.");
            if(e.Exhaustion) r.Reasons.Add("Windows reported "+(e.ExhaustionCount>0?e.ExhaustionCount+" ":"")+"resource-exhaustion event(s) in the last 90 days"+(e.LastExhaustion>0?"; latest "+DateTimeOffset.FromUnixTimeSeconds(e.LastExhaustion).ToString("yyyy-MM-dd HH:mm")+" UTC":"")+". Resolve the cause and record your review before applying this estimate.");
            if(s.DumpType!=0 && s.DumpType!=3 && s.DumpType!=1) r.Reasons.Add("Kernel/automatic/active dump backing is unresolved; automatic application is blocked.");
            if(s.CrashRegistry.ContainsKey("DedicatedDumpFile") && !string.IsNullOrWhiteSpace(Convert.ToString(s.CrashRegistry["DedicatedDumpFile"]))) r.Reasons.Add("Dedicated dump-file configuration requires independent review.");
            long bootMB=s.DumpType==1?((e.Physical+Units.GiB-1)/Units.GiB)*1024+257:s.DumpType==3?1024:0;
            // Complete dumps need the RAM capacity, rounded up, plus header/driver data.
            r.CapacityMB=Math.Max(r.CapacityMB,bootMB);
            var existing=new HashSet<string>(s.Active.Select(a=>Drive(a.Path)),StringComparer.OrdinalIgnoreCase);
            var candidates=s.Volumes.Where(v=>v.Eligible).OrderByDescending(v=>existing.Contains(v.Drive))
                .ThenBy(v=>v.LatencyMs.HasValue?0:1).ThenBy(v=>v.LatencyMs??0).ThenByDescending(v=>v.Free).ToList();
            foreach(var v in candidates)
            {
                var target=new List<PageEntry>();
                if(v.Drive!=s.WindowsDrive && bootMB>0) target.Add(Entry(s.WindowsDrive,bootMB));
                long main=r.CapacityMB-target.Sum(x=>(long)x.InitialMB);
                if(main>0) target.Add(Entry(v.Drive,main));
                else if(target.Count==0) target.Add(Entry(v.Drive,r.CapacityMB));
                string reason;
                if(!HasSpace(s,target,out reason)) continue;
                r.Target=target; break;
            }
            if(r.Target.Count==0) r.Reasons.Add("No eligible SSD layout satisfies the allocation and free-space reserve.");
            foreach(var v in s.Volumes)
                r.ProjectedFree[v.Drive]=v.Free+s.Active.Where(a=>Drive(a.Path)==v.Drive).Sum(a=>(long)a.AllocatedMB)*Units.MiB-r.Target.Where(a=>Drive(a.Path)==v.Drive).Sum(a=>(long)a.InitialMB)*Units.MiB;
            r.PredictedLimit=e.Physical+r.CapacityMB*Units.MiB;
            r.Relocation=r.Target.Any(t=>!s.Active.Any(a=>string.Equals(a.Path,t.Path,StringComparison.OrdinalIgnoreCase)));
            r.Ready=r.Reasons.Count==0;
            r.Id=Json.Hash(new { Fingerprint=s.Fingerprint(),r.Target,r.Peak,r.Headroom,r.Ready });
            return r;
        }
        static PageEntry Entry(string drive,long mb)
        { if(mb>uint.MaxValue) throw new InvalidOperationException("Allocation exceeds supported range"); return new PageEntry {Path=drive+@"\pagefile.sys",InitialMB=(uint)mb,MaximumMB=(uint)mb}; }
        public static bool HasSpace(Snapshot s,List<PageEntry> target,out string reason,bool restoration=false,bool keepUnchanged=false)
        {
            reason=null;
            if(target==null || target.Count==0) {reason="No page files selected";return false;}
            if(target.Any(x=>x==null || string.IsNullOrWhiteSpace(x.Path))) {reason="Invalid page-file path";return false;}
            if(target.Select(x=>x.Path.ToUpperInvariant()).Distinct().Count()!=target.Count) {reason="Duplicate page-file paths";return false;}
            foreach(var entry in target)
            {
                if(entry.Path==null || !System.Text.RegularExpressions.Regex.IsMatch(entry.Path,@"^[A-Za-z]:\\pagefile\.sys$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {reason="Unsupported page-file path";return false;}
                var v=s.Volumes.FirstOrDefault(x=>string.Equals(x.Drive,Drive(entry.Path),StringComparison.OrdinalIgnoreCase));
                if(v==null || (!restoration && !v.Eligible)) {reason="Drive eligibility unavailable";return false;}
                if(restoration && entry.InitialMB==0 && entry.MaximumMB==0)
                {if(v.Free<Units.GiB){reason="Insufficient space for restored automatic entry";return false;}continue;}
                if(entry.InitialMB>entry.MaximumMB || entry.MaximumMB==0) {reason="Fixed allocation required for capacity validation";return false;}
                long old=s.Active.Where(a=>string.Equals(a.Path,entry.Path,StringComparison.OrdinalIgnoreCase)).Sum(a=>(long)a.AllocatedMB)*Units.MiB;
                long projected=v.Free+old-entry.InitialMB*Units.MiB;
                // Only a temporary relocation stage may retain an unchanged low-space allocation.
                bool unchanged=keepUnchanged && old==entry.InitialMB*Units.MiB;
                if(projected<(restoration?Units.GiB:Reserve(v)) && !unchanged) {reason="Insufficient free-space reserve on "+v.Drive;return false;}
            }
            return true;
        }
        public static List<PageEntry> StageOne(Snapshot current,List<PageEntry> final)
        {
            var result=Json.Copy(final);
            foreach(var old in current.Active)
            {
                var item=result.FirstOrDefault(x=>string.Equals(x.Path,old.Path,StringComparison.OrdinalIgnoreCase));
                if(item==null) result.Add(new PageEntry {Path=old.Path,InitialMB=old.AllocatedMB,MaximumMB=old.AllocatedMB});
                else if(item.InitialMB<old.AllocatedMB) item.InitialMB=item.MaximumMB=old.AllocatedMB;
            }
            return result;
        }
        public static bool CanAutoGrow(Policy p,Snapshot s,Recommendation r,ChangeJournal j,long now,out string reason)
        {
            reason="Auto-grow is disabled or not eligible";
            if(!p.AutoGrow || !p.FirstReviewed || !string.IsNullOrEmpty(p.PauseReason) || !r.Ready || s.Automatic || (j!=null && j.Pending) || now-p.LastChange<86400) return false;
            if(p.ApprovedFingerprint!=s.Fingerprint()) {reason="Outside configuration change";return false;}
            if(r.Target.Count!=s.Entries.Count || r.Target.Sum(x=>(long)x.InitialMB)>p.CeilingMB) return false;
            long growth=0;
            foreach(var t in r.Target)
            {
                var old=s.Entries.FirstOrDefault(x=>string.Equals(x.Path,t.Path,StringComparison.OrdinalIgnoreCase));
                if(old==null || old.InitialMB!=old.MaximumMB || t.InitialMB<old.InitialMB || !p.ApprovedPaths.Contains(t.Path,StringComparer.OrdinalIgnoreCase)) return false;
                growth+=t.InitialMB-old.InitialMB;
            }
            if(growth<2048) return false;
            reason=null; return true;
        }
    }
}
