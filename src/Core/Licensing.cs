using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Security.Cryptography;

namespace PagefileMonitor
{
 public sealed class CommerceConfig
 {
  public int StoreId {get;set;}
  public int ProductId {get;set;}
  public int VariantId {get;set;}
  public string CheckoutUrl {get;set;}
  public bool Ready => StoreId>0 && ProductId>0 && VariantId>0;
  public static CommerceConfig Load() { var p=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"commerce.json");return File.Exists(p)?Json.Read<CommerceConfig>(File.ReadAllText(p)):new CommerceConfig(); }
 }
 public sealed class LicenseReceipt
 {
  public bool activated {get;set;} public bool valid {get;set;} public bool deactivated {get;set;}
  public LicenseDetails license_key {get;set;} public LicenseInstance instance {get;set;} public LicenseMeta meta {get;set;}
 }
 public sealed class LicenseDetails {public string status {get;set;} public string expires_at {get;set;}}
 public sealed class LicenseInstance {public string id {get;set;}}
 public sealed class LicenseMeta {public int store_id {get;set;} public int product_id {get;set;} public int variant_id {get;set;}}
 public sealed class SavedLicense {public string Key {get;set;} public string Instance {get;set;}}
 public static class Licensing
 {
  static string FilePath=>Path.Combine(Security.Root,"license.bin");
  public static bool Accept(CommerceConfig c,LicenseReceipt r,string instance,DateTimeOffset now)
  {
   if(!c.Ready || r==null || r.meta==null || r.license_key==null || r.instance==null || string.IsNullOrEmpty(r.instance.id))return false;
   if(r.meta.store_id!=c.StoreId || r.meta.product_id!=c.ProductId || r.meta.variant_id!=c.VariantId || r.license_key.status!="active")return false;
   if(instance!=null && r.instance.id!=instance)return false;
   if(!string.IsNullOrEmpty(r.license_key.expires_at)){DateTimeOffset expiry;if(!DateTimeOffset.TryParse(r.license_key.expires_at,out expiry)||expiry<=now)return false;}
   return instance==null?r.activated:r.valid;
  }
  static LicenseReceipt Call(string action,string key,string instance)
  {
   var req=(HttpWebRequest)WebRequest.Create("https://api.lemonsqueezy.com/v1/licenses/"+action);
   req.Method="POST";req.Accept="application/json";req.ContentType="application/x-www-form-urlencoded";req.Timeout=10000;req.ReadWriteTimeout=10000;req.AllowAutoRedirect=false;
   var body="license_key="+Uri.EscapeDataString(key)+"&"+(action=="activate"?"instance_name=":"instance_id=")+Uri.EscapeDataString(instance);
   var bytes=Encoding.UTF8.GetBytes(body);req.ContentLength=bytes.Length;
   using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
   using(var response=req.GetResponse())using(var reader=new StreamReader(response.GetResponseStream()))return Json.Read<LicenseReceipt>(reader.ReadToEnd());
  }
  static SavedLicense Read()=>Json.Read<SavedLicense>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath),null,DataProtectionScope.LocalMachine)));
  public static bool Validate()
  {
   try{var c=CommerceConfig.Load();if(!c.Ready||!File.Exists(FilePath))return false;var saved=Read();return Accept(c,Call("validate",saved.Key,saved.Instance),saved.Instance,DateTimeOffset.UtcNow);}catch{return false;}
  }
  public static void Activate(string key)
  {
   if(!Security.IsAdmin())throw new UnauthorizedAccessException("Administrator required.");
   var c=CommerceConfig.Load();if(!c.Ready)throw new InvalidOperationException("The publisher has not configured Lemon Squeezy for this build.");
   if(File.Exists(FilePath))throw new InvalidOperationException("Deactivate the current license before activating another.");
   if(string.IsNullOrWhiteSpace(key)||key.Length>256)throw new ArgumentException("Enter a license key.");
   // Validate product identity before consuming an activation slot.
   var check=Call("validate",key,"");
   if(check.meta==null||check.meta.store_id!=c.StoreId||check.meta.product_id!=c.ProductId||check.meta.variant_id!=c.VariantId||!check.valid)throw new InvalidOperationException("License does not belong to this Pro product or is invalid.");
   var r=Call("activate",key,"Pagefile Monitor "+Guid.NewGuid().ToString("N"));
   if(!Accept(c,r,null,DateTimeOffset.UtcNow))throw new InvalidOperationException("Pro activation was not accepted.");
   try {var data=ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Write(new SavedLicense{Key=key,Instance=r.instance.id})),null,DataProtectionScope.LocalMachine);using(var f=new FileStream(FilePath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(data,0,data.Length);f.Flush(true);}}
   catch {try{Call("deactivate",key,r.instance.id);}catch{}throw;}
  }
  public static void Deactivate()
  {
   if(!Security.IsAdmin())throw new UnauthorizedAccessException("Administrator required.");
   if(!File.Exists(FilePath))return;var s=Read();if(!Call("deactivate",s.Key,s.Instance).deactivated)throw new InvalidOperationException("Deactivation failed; license retained for retry.");File.Delete(FilePath);
  }
 }
}
