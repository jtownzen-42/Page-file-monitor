using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace PagefileMonitor
{
    public static class WindowsMetrics
    {
        [StructLayout(LayoutKind.Sequential)] struct PerfInfo
        {
            public uint Size;
            public UIntPtr Commit, Limit, Peak, Physical, Available, Cache, Kernel, Paged, Nonpaged, PageSize;
            public uint Handles, Processes, Threads;
        }
        [DllImport("psapi.dll", SetLastError=true)] static extern bool GetPerformanceInfo(out PerfInfo value, uint size);
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        public static Sample ReadMemory(long boot)
        {
            PerfInfo p;
            if (!GetPerformanceInfo(out p, (uint)Marshal.SizeOf(typeof(PerfInfo)))) throw new System.ComponentModel.Win32Exception();
            long page = (long)p.PageSize.ToUInt64();
            return new Sample { Time=Units.Now, Boot=boot, Commit=(long)p.Commit.ToUInt64()*page, Limit=(long)p.Limit.ToUInt64()*page,
                BootPeak=(long)p.Peak.ToUInt64()*page, Physical=(long)p.Physical.ToUInt64()*page, Available=(long)p.Available.ToUInt64()*page,
                Cache=(long)p.Cache.ToUInt64()*page, KernelPaged=(long)p.Paged.ToUInt64()*page, KernelNonpaged=(long)p.Nonpaged.ToUInt64()*page };
        }
        public static List<ProcessSample> ReadProcesses()
        {
            var result = new List<ProcessSample>();
            foreach (var p in Process.GetProcesses()) using (p)
            {
                try
                {
                    var row = new ProcessSample { Name=p.ProcessName, Pid=p.Id, PrivateBytes=p.PrivateMemorySize64, WorkingSet=p.WorkingSet64 };
                    result.Add(row);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            result=result.OrderByDescending(p=>p.PrivateBytes).Take(20).ToList();
            foreach(var row in result)
                try { using(var process=Process.GetProcessById(row.Pid)) if(process.ProcessName==row.Name) row.Start=new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds(); } catch(Exception) { }
            return result;
        }
        public static void Query(string query, Action<ManagementObject> action, string scope="root\\cimv2")
        {
            using (var searcher = new ManagementObjectSearcher(scope, query))
            { searcher.Options.Timeout=TimeSpan.FromSeconds(10); using (var rows=searcher.Get()) foreach(ManagementObject row in rows) using(row) action(row); }
        }
        static Dictionary<string,object> ReadRegistry(string path, params string[] names)
        {
            using(var key=Registry.LocalMachine.OpenSubKey(path))
            {
                if(key==null) throw new InvalidOperationException("Required registry key unavailable");
                var result=new Dictionary<string,object>();
                foreach(string name in names) result[name]=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                return result;
            }
        }
        public const string MemoryKey=@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        public const string CrashKey=@"SYSTEM\CurrentControlSet\Control\CrashControl";
        public static Snapshot ReadSnapshot()
        {
            var s=new Snapshot { Time=Units.Now, UptimeSeconds=(long)(GetTickCount64()/1000) };
            try
            {
                Query("SELECT SystemDrive,LastBootUpTime FROM Win32_OperatingSystem", r=> {
                    s.WindowsDrive=(string)r["SystemDrive"];
                    s.Boot=new DateTimeOffset(ManagementDateTimeConverter.ToDateTime((string)r["LastBootUpTime"]).ToUniversalTime()).ToUnixTimeSeconds(); });
                Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem",r=>s.Automatic=(bool)r["AutomaticManagedPagefile"]);
                Query("SELECT Name,InitialSize,MaximumSize FROM Win32_PageFileSetting",r=>s.Entries.Add(new PageEntry {Path=(string)r["Name"],InitialMB=(uint)r["InitialSize"],MaximumMB=(uint)r["MaximumSize"]}));
                Query("SELECT Name,AllocatedBaseSize,CurrentUsage,PeakUsage,TempPageFile FROM Win32_PageFileUsage",r=>s.Active.Add(new ActivePage {Path=(string)r["Name"],AllocatedMB=(uint)r["AllocatedBaseSize"],UsedMB=(uint)r["CurrentUsage"],PeakMB=(uint)r["PeakUsage"],Temporary=(bool)r["TempPageFile"]}));
                s.MemoryRegistry=ReadRegistry(MemoryKey,"PagingFiles");
                s.CrashRegistry=ReadRegistry(CrashKey,"CrashDumpEnabled","DumpFile","MinidumpDir","DedicatedDumpFile","DumpFileSize","AlwaysKeepMemoryDump","FilterPages");
                if(s.CrashRegistry["CrashDumpEnabled"]==null)throw new InvalidOperationException("Crash-dump setting is missing");
                s.DumpType=Convert.ToInt32(s.CrashRegistry["CrashDumpEnabled"]);
                var storage=new List<Tuple<string,string,int,int>>();
                try { Query("SELECT FriendlyName,SerialNumber,MediaType,BusType FROM MSFT_PhysicalDisk",r=>storage.Add(Tuple.Create(Convert.ToString(r["FriendlyName"]).Trim(),Convert.ToString(r["SerialNumber"]).Trim(),Convert.ToInt32(r["MediaType"]),Convert.ToInt32(r["BusType"]))),@"root\Microsoft\Windows\Storage"); } catch (ManagementException) { }
                Query("SELECT * FROM Win32_DiskDrive",disk=> {
                    string model=Convert.ToString(disk["Model"]).Trim(), serial=Convert.ToString(disk["SerialNumber"]).Trim();
                    var matches=storage.Where(x=>(serial.Length>0 && x.Item2==serial)||x.Item1==model).ToList();
                    bool eligible=matches.Count==1 && matches[0].Item3==4 && new[]{3,8,10,11,17}.Contains(matches[0].Item4);
                    using(var partitions=disk.GetRelated("Win32_DiskPartition")) foreach(ManagementObject part in partitions) using(part)
                    using(var drives=part.GetRelated("Win32_LogicalDisk")) foreach(ManagementObject drive in drives) using(drive)
                    {
                        string letter=Convert.ToString(drive["DeviceID"]);
                        if(s.Volumes.Any(v=>v.Drive==letter)) continue;
                        s.Volumes.Add(new Volume { Drive=letter,Identity=serial+"|"+Convert.ToString(drive["VolumeSerialNumber"]),Model=model,
                            Eligible=eligible && Convert.ToString(drive["FileSystem"])=="NTFS" && Convert.ToInt32(drive["DriveType"])==3,
                            Reason=eligible?"Internal SSD; verify available at boot":"Internal SSD identity not established", Size=Convert.ToInt64(drive["Size"]),Free=Convert.ToInt64(drive["FreeSpace"]) });
                    }
                });
                if(s.Boot==0 || s.WindowsDrive==null) throw new InvalidOperationException("OS snapshot unavailable");
            }
            catch(Exception ex) { s.Error=ex.GetType().Name+": "+ex.Message; }
            return s;
        }
        public static List<long> ExhaustionEvents(long since)
        {
            string stamp=DateTimeOffset.FromUnixTimeSeconds(since).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var result=new List<long>();
            var query=new EventLogQuery("System",PathType.LogName,"*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004 and TimeCreated[@SystemTime >= '"+stamp+"']]]");
            using(var reader=new EventLogReader(query))
            { EventRecord record; while((record=reader.ReadEvent())!=null) using(record) if(record.TimeCreated.HasValue) result.Add(new DateTimeOffset(record.TimeCreated.Value.ToUniversalTime()).ToUnixTimeSeconds()); }
            return result;
        }
    }
    public sealed class Counters : IDisposable
    {
        [StructLayout(LayoutKind.Explicit)] struct Value { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
        [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhOpenQuery(string source,IntPtr data,out IntPtr query);
        [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query,string path,IntPtr data,out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter,uint format,out uint type,out Value value);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
        IntPtr query;
        readonly Dictionary<string,IntPtr> counters=new Dictionary<string,IntPtr>();
        public Counters(IEnumerable<Volume> volumes)
        {
            if(PdhOpenQuery(null,IntPtr.Zero,out query)!=0) throw new InvalidOperationException("PDH unavailable");
            Add("CPU %",@"\Processor(_Total)\% Processor Time");
            Add("Modified bytes",@"\Memory\Modified Page List Bytes");
            Add("Hard-fault pages in/sec",@"\Memory\Pages Input/sec");
            Add("Pages out/sec",@"\Memory\Pages Output/sec");
            Add("Pagefile usage %",@"\Paging File(_Total)\% Usage");
            foreach(var v in volumes)
            {
                string prefix=@"\LogicalDisk("+v.Drive+@")\";
                Add(v.Drive+" latency seconds",prefix+"Avg. Disk sec/Transfer");
                Add(v.Drive+" bytes/sec",prefix+"Disk Bytes/sec");
                Add(v.Drive+" queue",prefix+"Current Disk Queue Length");
                Add(v.Drive+" transfers/sec",prefix+"Disk Transfers/sec");
            }
            PdhCollectQueryData(query);
        }
        void Add(string name,string path) { IntPtr counter; counters[name]=PdhAddEnglishCounter(query,path,IntPtr.Zero,out counter)==0?counter:IntPtr.Zero; }
        public Dictionary<string,double?> Read()
        {
            var result=new Dictionary<string,double?>(); bool collected=PdhCollectQueryData(query)==0;
            foreach(var c in counters)
            {
                uint type; Value value;
                result[c.Key]=collected && c.Value!=IntPtr.Zero && PdhGetFormattedCounterValue(c.Value,0x200,out type,out value)==0 && value.Status<=1 && !double.IsNaN(value.Number) && !double.IsInfinity(value.Number)?(double?)value.Number:null;
            }
            return result;
        }
        public void Dispose() { if(query!=IntPtr.Zero) { PdhCloseQuery(query); query=IntPtr.Zero; } }
    }
}
