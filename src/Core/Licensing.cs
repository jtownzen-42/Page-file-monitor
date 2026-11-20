using System;
using System.IO;
using System.Net;
using System.Text;
using System.Security.Cryptography;

namespace PagefileMonitor
{
 public sealed class CommerceConfig
 {
  // Gumroad product_id (product page, Content, License key). A public identifier, not a secret.
  public string GumroadProductId {get;set;}
  public string CheckoutUrl {get;set;}
  public bool Ready => !string.IsNullOrWhiteSpace(GumroadProductId);
  public bool TryCheckout(out Uri uri)
  {
   uri=null;
   if(!Ready || !Uri.TryCreate(CheckoutUrl,UriKind.Absolute,out uri) || uri.Scheme!="https")return false;
   return uri.Host.Equals("gumroad.com",StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".gumroad.com",StringComparison.OrdinalIgnoreCase);
  }
  public static CommerceConfig Load() { var p=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"commerce.json");return File.Exists(p)?(Json.Read<CommerceConfig>(File.ReadAllText(p))??new CommerceConfig()):new CommerceConfig(); }
 }
 public sealed class GumroadPurchase
 {
  public string product_id {get;set;}
  public bool? refunded {get;set;} public bool? disputed {get;set;} public bool? dispute_won {get;set;} public bool? chargebacked {get;set;}
  public string subscription_ended_at {get;set;} public string subscription_cancelled_at {get;set;} public string subscription_failed_at {get;set;}
 }
 public sealed class GumroadReceipt {public bool? success {get;set;} public int? uses {get;set;} public string message {get;set;} public GumroadPurchase purchase {get;set;}}
 public sealed class SavedLicense {public string Key {get;set;} public long Activated {get;set;}}
 // Unreachable means Gumroad gave no answer. It never revokes a license: offline workstations keep Pro.
 public enum LicenseCheck {Valid,Rejected,Unreachable}
 public static class Licensing
 {
  public const string VerifyUrl="https://api.gumroad.com/v2/licenses/verify";
  // One purchase covers one person on up to this many activations.
  public const int MaxActivations=3;
  // A stored license is re-checked this often; after no answer or a rejection it is retried sooner.
  public const int RecheckSeconds=86400,RetrySeconds=3600;
  static string FilePath=>Path.Combine(Security.Root,"license.bin");
  static readonly object sync=new object();static long nextCheck;static bool lastPro;

  public static bool Accept(CommerceConfig c,GumroadReceipt r,bool activating,out string reason)
  {
   reason="The license was not accepted.";
   if(c==null || !c.Ready){reason="The publisher has not configured Gumroad for this build.";return false;}
   if(r==null || r.success!=true || r.purchase==null){reason="That license key was not found for this product.";return false;}
   var p=r.purchase;
   if(p.product_id!=c.GumroadProductId){reason="That key belongs to a different product.";return false;}
   if(p.refunded==true){reason="This purchase was refunded.";return false;}
   if(p.chargebacked==true){reason="This purchase was charged back.";return false;}
   if(p.disputed==true && p.dispute_won!=true){reason="This purchase is under dispute.";return false;}
   if(!string.IsNullOrEmpty(p.subscription_ended_at) || !string.IsNullOrEmpty(p.subscription_cancelled_at) || !string.IsNullOrEmpty(p.subscription_failed_at)){reason="This subscription is no longer active.";return false;}
   if(activating && (r.uses??0)>MaxActivations){reason="This key has already been activated "+MaxActivations+" times. Contact support to reset it.";return false;}
   reason=null;return true;
  }
  // Only a JSON reply that carries Gumroad's own success field counts as an answer.
  // Timeouts, server errors, proxies and captive portals are Unreachable, not Rejected.
  public static LicenseCheck Check(CommerceConfig c,int httpStatus,string body,bool activating,out GumroadReceipt receipt,out string reason)
  {
   receipt=null;reason="Could not reach Gumroad to check the license. Connect to the internet and try again.";
   if(httpStatus<=0 || httpStatus>=500 || httpStatus==408 || httpStatus==429 || string.IsNullOrWhiteSpace(body))return LicenseCheck.Unreachable;
   try{receipt=Json.Read<GumroadReceipt>(body);}catch{receipt=null;}
   if(receipt==null || receipt.success==null){receipt=null;return LicenseCheck.Unreachable;}
   return Accept(c,receipt,activating,out reason)?LicenseCheck.Valid:LicenseCheck.Rejected;
  }
  // Pro stays on unless Gumroad explicitly rejects the key.
  public static bool Entitled(LicenseCheck check)=>check!=LicenseCheck.Rejected;

  static int Post(string product,string key,bool increment,out string body)
  {
   body=null;
   try
   {
    var req=(HttpWebRequest)WebRequest.Create(VerifyUrl);
    req.Method="POST";req.Accept="application/json";req.ContentType="application/x-www-form-urlencoded";req.Timeout=10000;req.ReadWriteTimeout=10000;req.AllowAutoRedirect=false;
    var bytes=Encoding.UTF8.GetBytes("product_id="+Uri.EscapeDataString(product)+"&license_key="+Uri.EscapeDataString(key)+"&increment_uses_count="+(increment?"true":"false"));
    req.ContentLength=bytes.Length;
    using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
    using(var response=(HttpWebResponse)req.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){body=reader.ReadToEnd();return (int)response.StatusCode;}
   }
   catch(WebException ex)
   {
    // Gumroad answers an unknown key with 404 and a JSON body; that is an answer, not an outage.
    var response=ex.Response as HttpWebResponse;if(response==null)return 0;
    try{using(response)using(var reader=new StreamReader(response.GetResponseStream())){body=reader.ReadToEnd();return (int)response.StatusCode;}}catch{body=null;return 0;}
   }
   catch(IOException){body=null;return 0;}
  }
  static SavedLicense Read()=>Json.Read<SavedLicense>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath),null,DataProtectionScope.LocalMachine)));
  public static bool Validate()
  {
   try
   {
    var c=CommerceConfig.Load();
    if(!c.Ready || !File.Exists(FilePath)){lock(sync){nextCheck=0;lastPro=false;}return false;}
    lock(sync){if(nextCheck!=0 && Units.Now<nextCheck)return lastPro;}
    var saved=Read();string body,reason;GumroadReceipt receipt;
    var check=Check(c,Post(c.GumroadProductId,saved.Key,false,out body),body,false,out receipt,out reason);
    // A rejected key is kept on disk and retried, so a publisher-side mistake cannot erase customers' licenses.
    lock(sync){lastPro=Entitled(check);nextCheck=Units.Now+(check==LicenseCheck.Valid?RecheckSeconds:RetrySeconds);return lastPro;}
   }
   catch{return false;}
  }
  public static void Activate(string key)
  {
   if(!Security.IsAdmin())throw new UnauthorizedAccessException("Administrator required.");
   var c=CommerceConfig.Load();if(!c.Ready)throw new InvalidOperationException("The publisher has not configured Gumroad for this build.");
   if(File.Exists(FilePath))throw new InvalidOperationException("Remove the current license before activating another.");
   key=(key??"").Trim();if(key.Length==0 || key.Length>256)throw new ArgumentException("Enter a license key.");
   string body,reason;GumroadReceipt receipt;
   // Check the key first so a full or wrong key does not consume an activation.
   var check=Check(c,Post(c.GumroadProductId,key,false,out body),body,false,out receipt,out reason);
   if(check!=LicenseCheck.Valid)throw new InvalidOperationException(reason);
   if((receipt.uses??0)>=MaxActivations)throw new InvalidOperationException("This key has already been activated "+MaxActivations+" times. Contact support to reset it.");
   // Save before counting. Gumroad cannot give an activation back, so a failed write must happen before one is spent,
   // and a lost reply to the counting call must leave the customer licensed.
   var data=ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Write(new SavedLicense{Key=key,Activated=Units.Now})),null,DataProtectionScope.LocalMachine);
   using(var f=new FileStream(FilePath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(data,0,data.Length);f.Flush(true);}
   check=Check(c,Post(c.GumroadProductId,key,true,out body),body,true,out receipt,out reason);
   if(check==LicenseCheck.Rejected){try{File.Delete(FilePath);}catch{}throw new InvalidOperationException(reason);}
  }
  // Gumroad's public License API cannot give an activation back, so this only removes the key from this PC.
  public static void Deactivate()
  {
   if(!Security.IsAdmin())throw new UnauthorizedAccessException("Administrator required.");
   if(File.Exists(FilePath))File.Delete(FilePath);
  }
 }
}
