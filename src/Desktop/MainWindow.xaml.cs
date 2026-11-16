using PagefileMonitor;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PagefileMonitorDesktop
{
 public partial class MainWindow : Window
 {
  readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
  readonly System.Windows.Forms.NotifyIcon tray=new System.Windows.Forms.NotifyIcon();
  Status status;bool refreshing;string history;
  public MainWindow()
  {
   InitializeComponent();tray.Icon=System.Drawing.SystemIcons.Information;tray.Text="Pagefile Monitor";
   tray.DoubleClick+=(s,e)=>{Show();WindowState=WindowState.Normal;Activate();tray.Visible=false;};
   var menu=new System.Windows.Forms.ContextMenuStrip();menu.Items.Add("Open",null,(s,e)=>{Show();WindowState=WindowState.Normal;Activate();});menu.Items.Add("Exit dashboard",null,(s,e)=>Close());tray.ContextMenuStrip=menu;
   Loaded+=async(s,e)=>await Refresh();timer.Tick+=async(s,e)=>await Refresh();timer.Start();Closed+=(s,e)=>{timer.Stop();tray.Dispose();};
  }
  async Task Refresh()
  {
   if(refreshing)return;refreshing=true;
   try
   {
    var reply=await Task.Run(()=>PipeProtocol.Send(new Request{Command="status"}));if(!reply.Ok)throw new InvalidOperationException(reply.Message);
    status=reply.Status;if(status==null)throw new InvalidOperationException("Service returned no status.");
    Connection.Text=(status.ReadOnly?"Temporary collector":"Service connected")+" • "+(status.ServiceVersion??"older build");Render();
    if(status.Sample==null || Units.Now-status.Sample.Time>10 || status.Sample.Time>Units.Now+5)
    {Connection.Text="Collector stalled";Health.Text="Samples are stale. Do not use these readings as current memory demand.";}
    else if(status.ServiceVersion!=typeof(MainWindow).Assembly.GetName().Version.ToString())Health.Text+=" • Service update available in Settings.";
   }
   catch(Exception ex)
   {
    Connection.Text="Service unavailable";Health.Text="Service unavailable. Displayed values may be stale; no continuous collection is confirmed. "+ex.Message;
    if(status==null)
    {
     try
     {
      var snapshot=await Task.Run(()=>WindowsMetrics.ReadSnapshot());var memory=WindowsMetrics.ReadMemory(snapshot.Boot);
      status=new Status{Snapshot=snapshot,Sample=memory,Policy=new Policy(),Health="Read-only preview; service not installed or unavailable."};Render();
     }catch(Exception preview){Health.Text="Service and Windows preview unavailable: "+preview.Message;}
    }
    tray.Text="Pagefile Monitor: service unavailable";
   }
   finally{refreshing=false;}
  }
  void Render()
  {
   LicenseStatus.Text=status.Pro?"Pro • verified online":"Free • monitoring and recovery always available";
   Health.Text=status.Health??"Collecting…";var s=status.Sample;
   if(s!=null){Commit.Text=Units.Size(s.Commit)+" / "+Units.Size(s.Limit);Peak.Text=Units.Size(s.BootPeak);Available.Text=Units.Size(s.Available);Pressure.Value=s.Limit==0?0:s.Commit*100.0/s.Limit;}
   var e=status.Evidence;
   Coverage.Text=e==null?"Live read-only preview. Install the service to retain history.":$"Valid monitoring: {e.ValidSeconds/3600.0:F1} hours  •  Light {e.Light/60} min  •  Normal {e.Normal/60} min  •  Heavy {e.Heavy/60} min\nCurrent marker: {s?.Mark}. "+(e.Gaps?"Collection gaps or pruned detail recorded.":"")+(e.ExhaustionCount>0?"\nResource-exhaustion events in the last 90 days: "+e.ExhaustionCount+(e.Exhaustion?" (not reviewed).":" (reviewed)."):"");
   var snap=status.Snapshot;
   OverviewDetails.Text=snap==null?"No snapshot":$"Automatic management: {snap.Automatic}  |  Dump setting: {Dump(snap.DumpType)}\n"+string.Join("\n",snap.Active.Select(x=>x.Path+"  allocated "+x.AllocatedMB+" MB; used "+x.UsedMB+" MB; peak "+x.PeakMB+" MB"))+"\n\n"+string.Join("\n",snap.Volumes.Select(v=>v.Drive+"  "+v.Model+"  free "+Units.Size(v.Free)+"  "+(v.Eligible?"internal SSD":"eligibility unconfirmed")))+"\n\nTop private-memory users: "+string.Join(", ",status.Processes.Take(5).Select(p=>p.Name+" "+Units.Size(p.PrivateBytes)));
   RecommendationText.Text=RecommendationDescription();var p=status.Policy??new Policy();PolicyText.Text="Auto-grow: "+(p.AutoGrow?"enabled":"disabled")+"  |  Ceiling: "+p.CeilingMB/1024+" GiB\n"+(p.PauseReason??"No automation pause recorded.");
   if(status.Journal!=null && string.IsNullOrEmpty(ChangeText.Text))ChangeText.Text=Json.Write(status.Journal);
   tray.Text="Pagefile Monitor: "+(status.Journal?.Phase=="pending-restart"?"restart pending":"monitoring");
  }
  static string Dump(int type)=>type==3?"Small memory dump":type==1?"Complete memory dump":type==0?"None":"Kernel / automatic / active (requires review)";
  string RecommendationDescription()
  {
   var r=status?.Recommendation;if(r==null)return "Learning has not yet produced an estimate.";
   var sb=new StringBuilder(r.Ready?"READY FOR REVIEW\n\n":"PROVISIONAL / BLOCKED\n\n");
   sb.AppendLine("Peak demand: "+Units.Size(r.Peak));sb.AppendLine("Added headroom: "+Units.Size(r.Headroom));sb.AppendLine("Proposed total: "+r.CapacityMB+" MB");sb.AppendLine("Estimated commit limit: "+Units.Size(r.PredictedLimit));
   if(status.Evidence!=null && status.Evidence.BootPeak>status.Evidence.Peak)sb.AppendLine("Boot peak exceeds directly observed samples; it may predate monitoring.");
   sb.AppendLine("\nProposed fixed initial / maximum allocations:");foreach(var t in r.Target)sb.AppendLine(t.ToString());
   if(r.Relocation)sb.AppendLine("Relocation requires two reviewed stages and restart verification before the original is reduced.");
   sb.AppendLine("\nProjected final free space:");foreach(var v in r.ProjectedFree)sb.AppendLine(v.Key+" "+Units.Size(v.Value));
   foreach(string reason in r.Reasons)sb.AppendLine("• "+reason);
   sb.AppendLine("\nEngineering estimate: peak + max(4 GiB, 20% of peak) − usable RAM, plus dump requirements. Future workloads can exceed recorded demand.");return sb.ToString();
  }
  async Task Elevated(string args,string explanation)
  {
   if(MessageBox.Show(explanation+"\n\nContinue?","Review action",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
   string tool=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PagefileMonitor.Recovery.exe");
   try
   {
    using(var p=Process.Start(new ProcessStartInfo(tool,args+" --dialog"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden}))await Task.Run(()=>p.WaitForExit());
    await Refresh();
   }catch(Exception ex){MessageBox.Show(ex.Message,"Action not completed");}
  }
  async void Install(object sender,RoutedEventArgs e)=>await Elevated("--install "+WindowsIdentity.GetCurrent().User.Value,"Install the monitor service to collect locally at Windows startup. This does not change page-file settings.");
  async void Uninstall(object sender,RoutedEventArgs e)=>await Elevated("--uninstall","Stop and remove the service. Keep page-file settings, backups and recovery tools.");
  async void Upgrade(object sender,RoutedEventArgs e)=>await Elevated("--upgrade","Update the installed service from this release. Collection pauses briefly. Existing binaries are backed up and restored if verification fails. Measurements, policy, backups and page-file settings are retained. Close any dashboard opened from Program Files first.");
  async void Apply(object sender,RoutedEventArgs e)
  {
   if(status?.Recommendation?.Ready!=true){MessageBox.Show("The recommendation is not ready. Review the listed reasons.");return;}
   await Elevated("--apply "+status.Recommendation.Id,RecommendationDescription()+"\nA backup will be saved before configuration changes. Restart manually when ready.");
  }
  async void Finish(object sender,RoutedEventArgs e)=>await Elevated("--finish-move","Finish a destination-verified relocation by reducing the original allocation. This requires another restart.");
  async void Restore(object sender,RoutedEventArgs e)=>await Elevated("--"+(string)((Button)sender).Tag,"Restore the selected page-file configuration after checking available space. Automatic growth will be disabled. A restart may be required.");
  async void ConfirmHeavy(object sender,RoutedEventArgs e)
  {
   if(status?.Evidence==null || status.Evidence.Heavy<1800){MessageBox.Show("Mark at least 30 minutes of heavy workload first. A confirmation without recorded heavy sessions is not accepted.");return;}
   await ConfirmHeavyElevated();
  }
  Task ConfirmHeavyElevated()=>Elevated("--confirm-heavy","Confirm that your marked heavy sessions include the demanding workloads you expect to run.");
  async void ReviewExhaustion(object sender,RoutedEventArgs e)
  {
   var ev=status?.Evidence;
   if(ev==null || ev.LastExhaustion==0){MessageBox.Show("No resource-exhaustion events are recorded in the last 90 days, or the installed service predates this feature.");return;}
   if(!ev.Exhaustion){MessageBox.Show("The recorded resource-exhaustion events have already been reviewed.");return;}
   await Elevated("--review-exhaustion "+ev.LastExhaustion,"Windows reported "+ev.ExhaustionCount+" resource-exhaustion event(s) in the last 90 days. The latest was "+DateTimeOffset.FromUnixTimeSeconds(ev.LastExhaustion).ToLocalTime().ToString("g")+".\n\nUse Changes & Recovery → Load change log to see each time. Continue only if you have identified what caused them. This records your review; it does not change page-file settings. Any later event blocks recommendations again.");
  }
  async void Acknowledge(object sender,RoutedEventArgs e)=>await Elevated("--acknowledge","Accept the current configuration as the reviewed baseline for further recommendations. Auto-grow remains disabled.");
  async void AutoGrow(object sender,RoutedEventArgs e)
  {
   uint gib;if(!uint.TryParse(Ceiling.Text,out gib)||gib<2||gib>1024){MessageBox.Show("Enter a ceiling from 2 to 1024 GiB.");return;}
   await Elevated("--policy "+(string)((Button)sender).Tag+" "+gib*1024,"Update auto-grow policy with a total ceiling of "+gib+" GiB. Only verified, approved fixed locations can grow. Moves and reductions still require review.");
  }
  async void Mark(object sender,RoutedEventArgs e)
  {try{string marker=(string)((Button)sender).Tag;var r=await Task.Run(()=>PipeProtocol.Send(new Request{Command="mark",Value=marker}));if(!r.Ok)MessageBox.Show(r.Message);await Refresh();}catch(Exception ex){MessageBox.Show(ex.Message);}}
  async void RefreshClick(object sender,RoutedEventArgs e)=>await Refresh();
  async void History(object sender,RoutedEventArgs e)
  {
   try{var r=await Task.Run(()=>PipeProtocol.Send(new Request{Command="history"}));if(!r.Ok)throw new Exception(r.Message);history=r.Data;HistoryText.Text=history;Draw();}catch(Exception ex){MessageBox.Show(ex.Message);}
  }
  void Draw()
  {
   Chart.Children.Clear();var values=history.Split('\n').Skip(1).Where(l=>!string.IsNullOrWhiteSpace(l)).Reverse().Select(l=>double.Parse(l.Split(',')[1],System.Globalization.CultureInfo.InvariantCulture)/Units.GiB).ToList();if(values.Count==0)return;
   double maximum=Math.Max(1,values.Max()*1.1),width=Math.Max(200,Chart.ActualWidth),height=Math.Max(100,Chart.ActualHeight);
   var line=new Polyline{Stroke=new SolidColorBrush(Color.FromRgb(0,160,255)),StrokeThickness=2};for(int i=0;i<values.Count;i++)line.Points.Add(new Point(i*width/Math.Max(1,values.Count-1),height-values[i]/maximum*height));Chart.Children.Add(line);ChartLabel.Text="Peak shown: "+values.Max().ToString("F2")+" GiB";
  }
  void Export(object sender,RoutedEventArgs e)
  {if(history==null){MessageBox.Show("Load history first.");return;}Save("pagefile-history.csv","CSV|*.csv",history);}
  void Report(object sender,RoutedEventArgs e)=>Save("pagefile-report.txt","Text|*.txt","Pagefile Monitor report\r\n"+DateTimeOffset.Now+"\r\n\r\n"+OverviewDetails.Text+"\r\n\r\n"+Coverage.Text+"\r\n\r\n"+RecommendationDescription());
  void Save(string name,string filter,string content){var d=new Microsoft.Win32.SaveFileDialog{FileName=name,Filter=filter};if(d.ShowDialog()==true)File.WriteAllText(d.FileName,content);}
  async void Events(object sender,RoutedEventArgs e){try{var r=await Task.Run(()=>PipeProtocol.Send(new Request{Command="events"}));ChangeText.Text=(status?.Journal==null?"No configuration changes recorded.":Json.Write(status.Journal))+"\n\n"+r.Data;}catch(Exception ex){MessageBox.Show(ex.Message);}}
  void Backups(object sender,RoutedEventArgs e){string folder=System.IO.Path.Combine(Security.Root,"backups");if(Directory.Exists(folder))Process.Start(new ProcessStartInfo("explorer.exe","\""+folder+"\""){UseShellExecute=true});else MessageBox.Show("Backups are created when the service starts.");}
  void Tmog(object sender,RoutedEventArgs e){const string file=@"C:\Program Files\Task Manager TMOG\Task Manager.exe";if(File.Exists(file))Process.Start(new ProcessStartInfo(file){UseShellExecute=true});else MessageBox.Show("TMOG is not installed at the configured location.");}
  void BuyPro(object sender,RoutedEventArgs e)
  {
   var c=CommerceConfig.Load();Uri uri;
   if(!c.Ready || !Uri.TryCreate(c.CheckoutUrl,UriKind.Absolute,out uri) || uri.Scheme!="https" || !uri.Host.EndsWith(".lemonsqueezy.com",StringComparison.OrdinalIgnoreCase)){MessageBox.Show("Pro sales are not configured in this build yet. Free features are available now.");return;}
   Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
  }
  async void ActivateLicense(object sender,RoutedEventArgs e)=>await Elevated("--license-activate","Activate Pro for this PC. Enter the key in the administrator dialog; validation sends it to Lemon Squeezy. No memory telemetry is sent. Service status refreshes within one minute.");
  async void DeactivateLicense(object sender,RoutedEventArgs e)=>await Elevated("--license-deactivate","Release this PC's license activation through Lemon Squeezy. Automatic growth pauses after the next check; Free features and recovery remain available.");
  void TmogSite(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo("https://tmog.org/"){UseShellExecute=true});
  void Tray(object sender,RoutedEventArgs e){tray.Visible=true;Hide();}
 }
}
