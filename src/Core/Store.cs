using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PagefileMonitor
{
    // Uses Windows' serviced SQLite library; the database is created only by this app.
    public sealed class Sqlite : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Callback(IntPtr state,int count,IntPtr values,IntPtr names);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern int sqlite3_open_v2(byte[] filename,out IntPtr db,int flags,IntPtr vfs);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern int sqlite3_exec(IntPtr db,byte[] sql,Callback callback,IntPtr arg,out IntPtr error);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern void sqlite3_free(IntPtr value);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern int sqlite3_close(IntPtr db);
        IntPtr db;
        public Sqlite(string path)
        {
            if(sqlite3_open_v2(Utf8(path),out db,2|4|0x10000,IntPtr.Zero)!=0) { Dispose(); throw new IOException("Cannot open telemetry database"); }
            Execute("PRAGMA busy_timeout=3000; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;");
        }
        static byte[] Utf8(string s)=>Encoding.UTF8.GetBytes(s+'\0');
        static string Text(IntPtr p)
        {
            if(p==IntPtr.Zero) return null; int n=0; while(Marshal.ReadByte(p,n)!=0) n++;
            var bytes=new byte[n]; Marshal.Copy(p,bytes,0,n);return Encoding.UTF8.GetString(bytes);
        }
        public List<Dictionary<string,string>> Query(string sql)
        {
            var rows=new List<Dictionary<string,string>>();
            Callback cb=(arg,count,values,names)=> {
                var row=new Dictionary<string,string>();
                for(int i=0;i<count;i++) row[Text(Marshal.ReadIntPtr(names,i*IntPtr.Size))]=Text(Marshal.ReadIntPtr(values,i*IntPtr.Size));
                rows.Add(row); return 0;
            };
            IntPtr error; int code=sqlite3_exec(db,Utf8(sql),cb,IntPtr.Zero,out error);
            string message=Text(error); if(error!=IntPtr.Zero) sqlite3_free(error);
            GC.KeepAlive(cb);
            if(code!=0) throw new IOException("SQLite error "+code+": "+message);
            return rows;
        }
        public void Execute(string sql)=>Query(sql);
        public void Dispose() { if(db!=IntPtr.Zero) { sqlite3_close(db); db=IntPtr.Zero; } }
        public static string Q(string text)=>text==null?"NULL":"'"+text.Replace("'","''")+"'";
    }
    public sealed class Store : IDisposable
    {
        readonly Sqlite db;
        readonly object sync=new object();
        public string Path {get;}
        public Store(string path)
        {
            Path=path; Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)); db=new Sqlite(path);
            db.Execute("PRAGMA page_size=4096; PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA max_page_count=110000;"+
                "CREATE TABLE IF NOT EXISTS samples(t INTEGER PRIMARY KEY,boot INTEGER,commitbytes INTEGER,limitbytes INTEGER,peak INTEGER,physical INTEGER,available INTEGER,cache INTEGER,paged INTEGER,nonpaged INTEGER,mark TEXT);"+
                "CREATE TABLE IF NOT EXISTS minutes(t INTEGER PRIMARY KEY,peak INTEGER,bootpeak INTEGER,physical INTEGER,valid INTEGER,light INTEGER,normal INTEGER,heavy INTEGER);"+
                "CREATE TABLE IF NOT EXISTS detail(id INTEGER PRIMARY KEY,t INTEGER,kind TEXT,json TEXT); CREATE INDEX IF NOT EXISTS detail_time ON detail(t);"+
                "CREATE TABLE IF NOT EXISTS events(t INTEGER,kind TEXT,message TEXT,UNIQUE(t,kind,message));"+
                "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT); INSERT OR IGNORE INTO meta VALUES('first','"+Units.Now+"');"+
                "INSERT OR IGNORE INTO meta SELECT 'last-recorded',COALESCE(MAX(t),-1) FROM samples;");
        }
        public void Record(List<Tuple<Sample,int>> rows)=>Record(rows,null,null);
        // One transaction per batch keeps synchronous disk flushes off the one-second sampling path.
        public void Record(List<Tuple<Sample,int>> rows,List<Tuple<long,string,string>> details,List<Tuple<long,string,string>> events)
        {
            if(rows.Count==0 && (details==null || details.Count==0) && (events==null || events.Count==0)) return;
            var sql=new StringBuilder("BEGIN IMMEDIATE;");
            foreach(var pair in rows)
            {
                var s=pair.Item1; int valid=pair.Item2;
                sql.Append("INSERT OR IGNORE INTO samples VALUES(").Append(s.Time).Append(',').Append(s.Boot).Append(',').Append(s.Commit).Append(',').Append(s.Limit).Append(',').Append(s.BootPeak).Append(',').Append(s.Physical).Append(',').Append(s.Available).Append(',').Append(s.Cache).Append(',').Append(s.KernelPaged).Append(',').Append(s.KernelNonpaged).Append(',').Append(Sqlite.Q(s.Mark)).Append(");");
                long minute=s.Time/60*60;
                sql.Append("INSERT OR IGNORE INTO minutes VALUES(").Append(minute).Append(",0,0,0,0,0,0,0);");
                sql.Append("UPDATE minutes SET peak=MAX(peak,").Append(s.Commit).Append("),bootpeak=MAX(bootpeak,").Append(s.BootPeak).Append("),physical=").Append(s.Physical)
                    .Append(",valid=MIN(60,valid+").Append(valid).Append("),light=MIN(60,light+").Append(s.Mark=="light"?valid:0)
                    .Append("),normal=MIN(60,normal+").Append(s.Mark=="normal"?valid:0).Append("),heavy=MIN(60,heavy+").Append(s.Mark=="heavy"?valid:0).Append(") WHERE t=").Append(minute).Append(" AND ").Append(s.Time).Append(">COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key='last-recorded'),-1);");
                sql.Append("INSERT OR REPLACE INTO meta(key,value) VALUES('last-recorded',MAX(").Append(s.Time).Append(",COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key='last-recorded'),-1)));");
            }
            if(details!=null) foreach(var d in details) sql.Append("INSERT INTO detail(t,kind,json) VALUES(").Append(d.Item1).Append(',').Append(Sqlite.Q(d.Item2)).Append(',').Append(Sqlite.Q(d.Item3)).Append(");");
            if(events!=null) foreach(var e in events) sql.Append("INSERT OR IGNORE INTO events VALUES(").Append(e.Item1).Append(',').Append(Sqlite.Q(e.Item2)).Append(',').Append(Sqlite.Q(e.Item3)).Append(");");
            sql.Append("COMMIT;");
            lock(sync) try{db.Execute(sql.ToString());}catch{try{db.Execute("ROLLBACK;");}catch(IOException){}throw;}
        }
        public void Detail(string kind,object value,long? time=null)
        { lock(sync) db.Execute("INSERT INTO detail(t,kind,json) VALUES("+(time??Units.Now)+","+Sqlite.Q(kind)+","+Sqlite.Q(Json.Write(value))+");"); }
        public void Event(string kind,string message,long? time=null)
        { lock(sync) db.Execute("INSERT OR IGNORE INTO events VALUES("+(time??Units.Now)+","+Sqlite.Q(kind)+","+Sqlite.Q(message)+");"); }
        public Evidence Evidence(long exhaustionReviewedThrough=0)
        {
            lock(sync)
            {
                var x=db.Query("SELECT COUNT(*) n,COALESCE(MAX(t),0) last FROM events WHERE kind='exhaustion' AND t>="+(Units.Now-90*86400)+";")[0];
                var r=db.Query("SELECT MIN(t) first,MAX(t) last,MAX(peak) peak,MAX(bootpeak) bootpeak,MAX(physical) physical,SUM(valid) valid,SUM(light) light,SUM(normal) normal,SUM(heavy) heavy FROM minutes;")[0];
                long first=long.Parse(db.Query("SELECT value FROM meta WHERE key='first';")[0]["value"],CultureInfo.InvariantCulture);
                return new Evidence {First=first,Last=N(r,"last"),Peak=N(r,"peak"),BootPeak=N(r,"bootpeak"),Physical=N(r,"physical"),ValidSeconds=N(r,"valid"),Light=N(r,"light"),Normal=N(r,"normal"),Heavy=N(r,"heavy"),
                    ExhaustionCount=(int)N(x,"n"),LastExhaustion=N(x,"last"),Exhaustion=N(x,"last")>exhaustionReviewedThrough,
                    Gaps=db.Query("SELECT t FROM events WHERE kind IN ('gap','pruned','collection-error') LIMIT 1;").Count>0};
            }
        }
        static long N(Dictionary<string,string> row,string key)=>string.IsNullOrEmpty(row[key])?0:long.Parse(row[key],CultureInfo.InvariantCulture);
        public string HistoryCsv()
        {
            lock(sync)
            {
                var rows=db.Query("SELECT * FROM minutes ORDER BY t DESC LIMIT 1440;");
                var sb=new StringBuilder("utc,peak_committed_bytes,boot_peak_bytes,physical_bytes,valid_seconds,light_seconds,normal_seconds,heavy_seconds\r\n");
                foreach(var r in rows) sb.Append(DateTimeOffset.FromUnixTimeSeconds(N(r,"t")).ToString("O")).Append(',').Append(string.Join(",",new[]{"peak","bootpeak","physical","valid","light","normal","heavy"}.Select(k=>r[k]))).Append("\r\n");
                return sb.ToString();
            }
        }
        public string EventsText()
        { lock(sync) return string.Join("\r\n",db.Query("SELECT * FROM events ORDER BY t DESC LIMIT 200;").Select(r=>DateTimeOffset.FromUnixTimeSeconds(N(r,"t")).ToLocalTime()+" | "+r["kind"]+" | "+r["message"])); }
        public Dictionary<string,double> DiskLatencies()
        {
            List<Dictionary<string,string>> rows;
            lock(sync) rows=db.Query("SELECT json FROM detail WHERE kind='counters' ORDER BY t DESC LIMIT 12000;");
            {
                var values=new Dictionary<string,List<double>>();
                foreach(var row in rows)
                {
                    var data=Json.Read<Dictionary<string,double?>>(row["json"]);
                    foreach(var x in data.Where(x=>x.Key.EndsWith(" latency seconds") && x.Value.HasValue))
                    {
                        string drive=x.Key.Substring(0,2); double? transfers;
                        if(data.TryGetValue(drive+" transfers/sec",out transfers) && transfers>=1)
                        { if(!values.ContainsKey(drive)) values[drive]=new List<double>(); values[drive].Add(x.Value.Value*1000); }
                    }
                }
                return values.Where(x=>x.Value.Count>=12).ToDictionary(x=>x.Key,x=>x.Value.Average());
            }
        }
        public void Prune(long now)
        {
            // Each batch takes the lock separately so a slow disk cannot hold up sample batches for the whole prune.
            DeleteBatches("samples","t<"+(now-7*86400)); DeleteBatches("detail","t<"+(now-7*86400));
            DeleteBatches("minutes","t<"+(now-90*86400)); DeleteBatches("events","t<"+(now-90*86400));
            lock(sync)
            {
                long count=N(db.Query("PRAGMA page_count;")[0],"page_count"), free=N(db.Query("PRAGMA freelist_count;")[0],"freelist_count");
                bool pruned=false;
                while((count-free)*4096>400L*Units.MiB)
                {
                    db.Execute("DELETE FROM detail WHERE id IN (SELECT id FROM detail ORDER BY t LIMIT 1000); DELETE FROM samples WHERE t IN (SELECT t FROM samples ORDER BY t LIMIT 10000);");
                    long next=N(db.Query("PRAGMA freelist_count;")[0],"freelist_count");
                    if(next==free) break; free=next;pruned=true;
                }
                if(pruned) Event("pruned","Storage budget removed oldest raw records. Minute maxima remain.");
                db.Execute("PRAGMA incremental_vacuum(1024);");
            }
        }
        void DeleteBatches(string table,string where)
        {
            while(true) lock(sync)
            {
                db.Execute("DELETE FROM "+table+" WHERE rowid IN (SELECT rowid FROM "+table+" WHERE "+where+" LIMIT 2000);");
                if(N(db.Query("SELECT changes() n;")[0],"n")==0) break;
            }
        }
        public void Dispose(){lock(sync)db.Dispose();}
    }
}
