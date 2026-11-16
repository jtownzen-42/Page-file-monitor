using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PagefileMonitor
{
    public static class Json
    {
        public static string Write(object value) => new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(value);
        public static T Read<T>(string text) => new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<T>(text);
        public static T Copy<T>(T value) => Read<T>(Write(value));
        public static void Save(string path, object value)
        { SaveText(path,Write(value)); }
        public static void SaveText(string path,string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(value); writer.Flush(); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static string Hash(object value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Write(value)))).Replace("-", "");
        }
    }
    public static class Units
    {
        public const long MiB = 1048576, GiB = 1073741824;
        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public static string Size(long bytes) => (bytes / (double)GiB).ToString("F2") + " GiB";
    }
    public sealed class PageEntry
    {
        public string Path { get; set; }
        public uint InitialMB { get; set; }
        public uint MaximumMB { get; set; }
        public override string ToString() => Path + " : " + InitialMB + " / " + MaximumMB + " MB";
    }
    public sealed class ActivePage
    {
        public string Path { get; set; }
        public uint AllocatedMB { get; set; }
        public uint UsedMB { get; set; }
        public uint PeakMB { get; set; }
        public bool Temporary { get; set; }
    }
    public sealed class Volume
    {
        public string Drive { get; set; }
        public string Identity { get; set; }
        public string Model { get; set; }
        public bool Eligible { get; set; }
        public string Reason { get; set; }
        public long Size { get; set; }
        public long Free { get; set; }
        public double? LatencyMs { get; set; }
    }
    public sealed class Snapshot
    {
        public long Time { get; set; }
        public long Boot { get; set; }
        public string WindowsDrive { get; set; }
        public bool Automatic { get; set; }
        public List<PageEntry> Entries { get; set; } = new List<PageEntry>();
        public List<ActivePage> Active { get; set; } = new List<ActivePage>();
        public List<Volume> Volumes { get; set; } = new List<Volume>();
        public Dictionary<string, object> MemoryRegistry { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> CrashRegistry { get; set; } = new Dictionary<string, object>();
        public int DumpType { get; set; }
        public long UptimeSeconds { get; set; }
        public string Error { get; set; }
        public string Fingerprint() => Json.Hash(new { Automatic, Entries = Entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(), MemoryRegistry, CrashRegistry });
    }
    public sealed class Sample
    {
        public long Time { get; set; }
        public long Boot { get; set; }
        public long Commit { get; set; }
        public long Limit { get; set; }
        public long BootPeak { get; set; }
        public long Physical { get; set; }
        public long Available { get; set; }
        public long Cache { get; set; }
        public long KernelPaged { get; set; }
        public long KernelNonpaged { get; set; }
        public string Mark { get; set; } = "unmarked";
        public string Utilization => Limit > 0 && (Commit > Limit * .85 || Available < Physical * .1) ? "memory pressure" : "within capacity";
    }
    // Credits workload coverage from a monotonic clock so timer drift is neither lost nor invented.
    public sealed class CoverageClock
    {
        long previousMs=-1,previousUtc,carryMs;
        public long LastJumpSeconds { get; private set; }
        public int Credit(long monotonicMs,long utcSeconds,out bool gap)
        {
            gap=false;int valid=0;LastJumpSeconds=0;
            if(previousMs>=0)
            {
                long elapsed=monotonicMs-previousMs,wall=utcSeconds-previousUtc;
                if(elapsed>=500 && elapsed<=2500 && wall>=0 && wall<=3)
                {
                    carryMs+=elapsed;
                    // A sample sharing the previous second is not stored again; its time is credited to the next one.
                    if(wall>0){valid=(int)(carryMs/1000);carryMs%=1000;}
                }
                else {gap=true;carryMs=0;LastJumpSeconds=Math.Abs(wall);}
            }
            previousMs=monotonicMs;previousUtc=utcSeconds;return valid;
        }
    }
    public sealed class ProcessSample
    {
        public string Name { get; set; }
        public int Pid { get; set; }
        public long? Start { get; set; }
        public long PrivateBytes { get; set; }
        public long WorkingSet { get; set; }
    }
    public sealed class Evidence
    {
        public long First { get; set; }
        public long Last { get; set; }
        public long ValidSeconds { get; set; }
        public long Peak { get; set; }
        public long BootPeak { get; set; }
        public long Physical { get; set; }
        public long Light { get; set; }
        public long Normal { get; set; }
        public long Heavy { get; set; }
        public bool Exhaustion { get; set; }
        public int ExhaustionCount { get; set; }
        public long LastExhaustion { get; set; }
        public bool Gaps { get; set; }
    }
    public sealed class Policy
    {
        public bool HeavyConfirmed { get; set; }
        // Heavy seconds recorded when the confirmation was given; a confirmation without sessions is not evidence.
        public long HeavyConfirmedSeconds { get; set; }
        // Exhaustion events at or before this UTC second were reviewed by an administrator.
        public long ExhaustionReviewedThrough { get; set; }
        public bool AutoGrow { get; set; }
        public uint CeilingMB { get; set; } = 65536;
        public List<string> ApprovedPaths { get; set; } = new List<string>();
        public string ApprovedFingerprint { get; set; }
        public long LastChange { get; set; }
        public bool FirstReviewed { get; set; }
        public string PauseReason { get; set; }
    }
    public sealed class Recommendation
    {
        public bool Ready { get; set; }
        public long Peak { get; set; }
        public long Headroom { get; set; }
        public long CapacityMB { get; set; }
        public long PredictedLimit { get; set; }
        public List<PageEntry> Target { get; set; } = new List<PageEntry>();
        public List<string> Reasons { get; set; } = new List<string>();
        public Dictionary<string, long> ProjectedFree { get; set; } = new Dictionary<string, long>();
        public bool Relocation { get; set; }
        public string Id { get; set; }
        public string SourceFingerprint { get; set; }
    }
    public sealed class ChangeJournal
    {
        public string Id { get; set; }
        public string Phase { get; set; }
        public string Backup { get; set; }
        public long Time { get; set; }
        public long Boot { get; set; }
        public string ExpectedFingerprint { get; set; }
        public Snapshot Desired { get; set; }
        public List<PageEntry> FinalTarget { get; set; }
        public string Error { get; set; }
        public bool Pending => Phase == "writing" || Phase == "pending-restart" || Phase == "stage-one-verified";
    }
    public sealed class Status
    {
        public string ServiceVersion {get;set;}
        public bool ReadOnly {get;set;}
        public bool Pro {get;set;}
        public Sample Sample { get; set; }
        public Snapshot Snapshot { get; set; }
        public Evidence Evidence { get; set; }
        public Recommendation Recommendation { get; set; }
        public Policy Policy { get; set; }
        public ChangeJournal Journal { get; set; }
        public string Health { get; set; }
        public Dictionary<string,double?> Counters { get; set; } = new Dictionary<string,double?>();
        public List<ProcessSample> Processes { get; set; } = new List<ProcessSample>();
    }
    public sealed class Request
    {
        public string Command { get; set; }
        public string Value { get; set; }
        public uint CeilingMB { get; set; }
        public bool Enabled { get; set; }
    }
    public sealed class Reply
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public Status Status { get; set; }
        public string Data { get; set; }
    }
}
