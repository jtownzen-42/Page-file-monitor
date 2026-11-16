using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PagefileMonitor
{
 public static class ReleaseUpdate
 {
  public static readonly string[] Files={"PagefileMonitor.exe","PagefileMonitor.Service.exe","PagefileMonitor.Recovery.exe","PagefileMonitor.Core.dll","PagefileMonitor.exe.config","PagefileMonitor.Service.exe.config","PagefileMonitor.Recovery.exe.config","commerce.json"};
  static string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream));}
  // Caller owns elevation, service stop/start and protected-directory validation.
  public static void Apply(string source,string destination,string backup,Action startAndVerify)
  {
   source=Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);destination=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
   if(string.Equals(source,destination,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Run the updater from the new release folder, not the installed folder.");
   foreach(var name in Files)if(!File.Exists(Path.Combine(source,name)))throw new FileNotFoundException("Incomplete release",name);
   Directory.CreateDirectory(backup);
   var existed=new HashSet<string>();
   foreach(var name in Files){var old=Path.Combine(destination,name);if(File.Exists(old)){File.Copy(old,Path.Combine(backup,name),false);existed.Add(name);}}
   Json.SaveText(Path.Combine(backup,"update-status.txt"),"Backup complete; replacement starting.");
   try
   {
    foreach(var name in Files)
    {
     // A placeholder release must not erase the publisher's existing store configuration.
     if(name=="commerce.json" && existed.Contains(name) && !Json.Read<CommerceConfig>(File.ReadAllText(Path.Combine(source,name))).Ready)continue;
     var from=Path.Combine(source,name);var to=Path.Combine(destination,name);File.Copy(from,to,true);
     if(Hash(from)!=Hash(to))throw new IOException("Update hash verification failed: "+name);
    }
    startAndVerify();Json.SaveText(Path.Combine(backup,"update-status.txt"),"Updated and service verified.");
   }
   catch
   {
    foreach(var name in Files){var to=Path.Combine(destination,name);if(existed.Contains(name))File.Copy(Path.Combine(backup,name),to,true);else if(File.Exists(to))File.Delete(to);}
    Json.SaveText(Path.Combine(backup,"update-status.txt"),"Update failed; previous binaries restored. Restart the service.");throw;
   }
  }
 }
}
