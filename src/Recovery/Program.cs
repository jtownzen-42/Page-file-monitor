using PagefileMonitor;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows.Forms;

namespace PagefileMonitorRecovery
{
    static class Program
    {
        static readonly string InstallDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PagefileMonitor");
        static void Sc(string args)
        {
            using(var p=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe"),args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
            { string output=p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd();p.WaitForExit();if(p.ExitCode!=0)throw new InvalidOperationException(output); }
        }
        static void Protect(string dir,string owner,bool ownerReads)
        {
            if(Directory.Exists(dir))
            {
                var current=Directory.GetAccessControl(dir);
                var existingOwner=(SecurityIdentifier)current.GetOwner(typeof(SecurityIdentifier));
                if(existingOwner!=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null) && existingOwner!=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null))
                    throw new InvalidOperationException("Existing installation/data folder is not administrator-owned. Review and move it before installing: "+dir);
                foreach(FileSystemAccessRule rule in current.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                    if(rule.AccessControlType==AccessControlType.Allow && (rule.FileSystemRights&(FileSystemRights.WriteData|FileSystemRights.AppendData|FileSystemRights.WriteAttributes|FileSystemRights.WriteExtendedAttributes|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership))!=0 &&
                       !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)) && !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)))
                        throw new InvalidOperationException("Existing data folder permits non-admin writes. Review it before installation: "+dir);
            }
            Directory.CreateDirectory(dir);
            if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Refusing a redirected installation/data directory");
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
            foreach(var id in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(id,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(owner),ownerReads?FileSystemRights.ReadAndExecute:FileSystemRights.Read,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(dir,acl);
        }
        static void Install(string owner)
        {
            new SecurityIdentifier(owner);
            if(ServiceController.GetServices().Any(s=>s.ServiceName=="PagefileMonitor"))throw new InvalidOperationException("Service already installed. Stop/remove the service before reinstalling; backups are retained.");
            Protect(InstallDir,owner,true);Protect(Security.Root,owner,true);
            // Dedicated protected folders prevent a local non-admin from supplying recovery state.
            Protect(Path.Combine(Security.Root,"backups"),owner,true);
            string source=AppDomain.CurrentDomain.BaseDirectory;
            foreach(string name in new[]{"commerce.json","PagefileMonitor.exe","PagefileMonitor.Service.exe","PagefileMonitor.Recovery.exe","PagefileMonitor.Core.dll","PagefileMonitor.exe.config","PagefileMonitor.Service.exe.config","PagefileMonitor.Recovery.exe.config"})
            {
                string from=Path.Combine(source,name);if(!File.Exists(from))throw new FileNotFoundException("Release file missing",from);
                string to=Path.Combine(InstallDir,name);if(!string.Equals(from,to,StringComparison.OrdinalIgnoreCase))File.Copy(from,to,true);
            }
            File.WriteAllText(Path.Combine(Security.Root,"owner.sid"),owner);
            Sc("create PagefileMonitor binPath= \"\\\""+Path.Combine(InstallDir,"PagefileMonitor.Service.exe")+"\\\"\" start= auto obj= LocalSystem DisplayName= \"Pagefile Monitor\"");
            try
            {
                Sc("description PagefileMonitor \"Local memory and page-file measurements. Changes require reviewed policy.\"");
                Sc("failure PagefileMonitor reset= 86400 actions= restart/10000/restart/30000");
                using(var service=new ServiceController("PagefileMonitor")){service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(30));}
            }
            catch
            {
                try{StopAndRemove();}catch(Exception){}
                throw;
            }
        }
        static void StopAndRemove()
        {
            using(var service=new ServiceController("PagefileMonitor"))
            {if(service.Status!=ServiceControllerStatus.Stopped){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(45));}}
            Sc("delete PagefileMonitor");
        }
        static void Upgrade()
        {
            string owner=File.ReadAllText(Path.Combine(Security.Root,"owner.sid")).Trim();
            Protect(InstallDir,owner,true);Protect(Security.Root,owner,true);
            var journalPath=Path.Combine(Security.Root,"journal.json");
            if(File.Exists(journalPath) && Json.Read<ChangeJournal>(File.ReadAllText(journalPath)).Pending)
                throw new InvalidOperationException("Resolve the pending page-file transaction before updating.");
            foreach(var process in Process.GetProcessesByName("PagefileMonitor"))using(process)
            {
                if(string.Equals(process.MainModule.FileName,Path.Combine(InstallDir,"PagefileMonitor.exe"),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Close the installed dashboard, then retry the update from the new release folder.");
            }
            string backup=Path.Combine(InstallDir,"updates",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
            using(var service=new ServiceController("PagefileMonitor"))
            {
                bool wasRunning=service.Status!=ServiceControllerStatus.Stopped;
                if(wasRunning){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(45));}
                try
                {
                    ReleaseUpdate.Apply(AppDomain.CurrentDomain.BaseDirectory,InstallDir,backup,()=>
                    {
                        try
                        {
                            service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(45));
                            var reply=PipeProtocol.Send(new Request{Command="status"});
                            if(!reply.Ok || reply.Status?.ServiceVersion!=typeof(Program).Assembly.GetName().Version.ToString())throw new InvalidOperationException("Updated service version could not be verified.");
                        }
                        catch{service.Refresh();if(service.Status!=ServiceControllerStatus.Stopped){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(45));}throw;}
                    });
                }
                catch
                {
                    service.Refresh();if(wasRunning && service.Status==ServiceControllerStatus.Stopped){service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(45));}throw;
                }
            }
        }
        [STAThread] static int Main(string[] args)
        {
            bool dialog=args.Contains("--dialog");args=args.Where(x=>x!="--dialog").ToArray();
            try
            {
                if(args.Length==0 || args[0]=="--help")
                {Console.WriteLine("PagefileMonitor.Recovery.exe --install OWNER_SID | --uninstall | --apply ID | --finish-move | --restore-original | --restore-previous | --system-managed | --policy on|off CEILING_MB | --confirm-heavy | --review-exhaustion LATEST_EVENT_UTC_SECONDS | --acknowledge | --upgrade\nAdd --offline to a restore/system-managed command when the service is stopped. Add --dialog for a result window.");return 0;}
                if(!Security.IsAdmin())throw new UnauthorizedAccessException("Run this recovery tool as administrator.");
                string command=args[0].TrimStart('-');
                if(command=="install") {if(args.Length!=2)throw new ArgumentException("Owner SID required");Install(args[1]);}
                else if(command=="upgrade")Upgrade();
                else if(command=="license-activate")
                {
                    if(!Directory.Exists(Security.Root) || !File.Exists(Path.Combine(Security.Root,"owner.sid")))throw new InvalidOperationException("Install the monitoring service before activation.");
                    using(var form=new Form{Text="Activate Pagefile Monitor Pro",Width=480,Height=190,StartPosition=FormStartPosition.CenterScreen})
                    {
                        var label=new Label{Text="Enter the license key from your Gumroad receipt. It will be sent to Gumroad for validation.",Left=15,Top=15,Width=430,Height=40};
                        var input=new TextBox{Left=15,Top=60,Width=430,UseSystemPasswordChar=true,MaxLength=256};
                        var button=new Button{Text="Activate",Left=340,Top=100,DialogResult=DialogResult.OK};
                        form.Controls.AddRange(new Control[]{label,input,button});form.AcceptButton=button;
                        if(form.ShowDialog()!=DialogResult.OK)return 0;Licensing.Activate(input.Text);
                    }
                }
                else if(command=="license-deactivate")Licensing.Deactivate();
                else if(command=="uninstall")StopAndRemove();
                else if(args.Contains("--offline"))
                {
                    if(!new[]{"restore-original","restore-previous","system-managed"}.Contains(command))throw new ArgumentException("Offline mode is only for restoration");
                    var service=ServiceController.GetServices().FirstOrDefault(s=>s.ServiceName=="PagefileMonitor");
                    if(service!=null)using(service)if(service.Status!=ServiceControllerStatus.Stopped)throw new InvalidOperationException("Stop the PagefileMonitor service before offline recovery.");
                    var manager=new ChangeManager(Security.Root,new WindowsConfigurationBackend());
                    if(command=="system-managed")manager.SystemManaged();else manager.Restore(command=="restore-original");
                }
                else
                {
                    var request=new Request {Command=command,Value=args.Length>1?args[1]:null,Enabled=command=="confirm-heavy"};
                    if(command=="policy"){if(args.Length!=3)throw new ArgumentException("Use --policy on|off CEILING_MB");request.Enabled=args[1]=="on";request.CeilingMB=uint.Parse(args[2]);}
                    var reply=PipeProtocol.Send(request);if(!reply.Ok)throw new InvalidOperationException(reply.Message);
                }
                string message=command=="install"?"Installed. The service is collecting measurements; page-file settings were not changed.":command=="uninstall"?"Service removed. Recovery tools, data and backups remain; page-file settings were not changed.":new[]{"confirm-heavy","review-exhaustion","acknowledge","upgrade","license-activate","license-deactivate"}.Contains(command)?"Completed. Page-file settings were not changed.":"Completed. Any page-file change requires a restart before the active allocation can be verified.";
                Console.WriteLine(message);if(dialog)MessageBox.Show(message,"Pagefile Monitor");return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex.Message);if(dialog)MessageBox.Show(ex.Message,"Pagefile Monitor",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
        }
    }
}
