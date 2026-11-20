using PagefileMonitor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PagefileMonitorDesktop
{
 public partial class MainWindow : Window
 {
  readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
  readonly System.Windows.Forms.NotifyIcon tray=new System.Windows.Forms.NotifyIcon();
  Status status;bool refreshing;string history;
  // Workload colours are used everywhere a marker appears: badge, buttons, live graph and history shading.
  static readonly Dictionary<string,Color> MarkColors=new Dictionary<string,Color>{{"light",Color.FromRgb(0x3D,0xDC,0x97)},{"normal",Color.FromRgb(0x00,0xBD,0xFF)},{"heavy",Color.FromRgb(0xFF,0x9F,0x1C)},{"unmarked",Color.FromRgb(0x5F,0x7A,0x96)}};
  static readonly Color Good=Color.FromRgb(0x3D,0xDC,0x97),Warn=Color.FromRgb(0xFF,0x9F,0x1C),Bad=Color.FromRgb(0xFF,0x4D,0x6D),Cyan=Color.FromRgb(0x00,0xBD,0xFF),Muted=Color.FromRgb(0x82,0xB7,0xD8);
  static SolidColorBrush B(Color c,byte alpha=255)=>new SolidColorBrush(Color.FromArgb(alpha,c.R,c.G,c.B));
  sealed class LivePoint{public double Commit,Limit,Physical;public string Mark;}
  readonly List<LivePoint> live=new List<LivePoint>();long lastLiveTime;string shownMark="?";DateTime? markSince;
  public MainWindow()
  {
   InitializeComponent();tray.Icon=System.Drawing.SystemIcons.Information;tray.Text="Pagefile Monitor";
   tray.DoubleClick+=(s,e)=>{Show();WindowState=WindowState.Normal;Activate();tray.Visible=false;};
   var menu=new System.Windows.Forms.ContextMenuStrip();menu.Items.Add("Open",null,(s,e)=>{Show();WindowState=WindowState.Normal;Activate();});menu.Items.Add("Exit dashboard",null,(s,e)=>Close());tray.ContextMenuStrip=menu;
   Loaded+=async(s,e)=>await Refresh();LiveChart.SizeChanged+=(s,e)=>DrawLive();Chart.SizeChanged+=(s,e)=>{if(history!=null)Draw();};timer.Tick+=async(s,e)=>await Refresh();timer.Start();Closed+=(s,e)=>{timer.Stop();tray.Dispose();};
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
    tray.Text="Pagefile Monitor: service unavailable";RenderMarker(null);
   }
   finally{refreshing=false;}
  }
  void Render()
  {
   LicenseStatus.Text=status.Pro?"Pro • license active":"Free • monitoring and recovery always available";
   Health.Text=status.Health??"Collecting…";var s=status.Sample;
   if(s!=null)
   {
    Commit.Text=Units.Size(s.Commit)+" / "+Units.Size(s.Limit);Peak.Text=Units.Size(s.BootPeak);Available.Text=Units.Size(s.Available);
    double used=s.Limit==0?0:s.Commit*100.0/s.Limit;Pressure.Value=used;Pressure.Foreground=B(used>=85?Bad:used>=70?Warn:Cyan);
    PressureLabel.Text="Committed memory is "+used.ToString("F0")+"% of the commit limit"+(used>=85?" — memory pressure":used>=70?" — elevated":"")+".";
    if(s.Time!=lastLiveTime){lastLiveTime=s.Time;live.Add(new LivePoint{Commit=s.Commit,Limit=s.Limit,Physical=s.Physical,Mark=s.Mark??"unmarked"});if(live.Count>200)live.RemoveAt(0);}
   }
   RenderMarker(s?.Mark);RenderReadiness();RenderBars();DrawLive();
   var e=status.Evidence;
   Coverage.Text=e==null?"Live read-only preview. Install the service to retain history.":$"Valid monitoring: {e.ValidSeconds/3600.0:F1} hours  •  Light {e.Light/60} min  •  Normal {e.Normal/60} min  •  Heavy {e.Heavy/60} min\nCurrent marker: {s?.Mark}. "+(e.Gaps?"Collection gaps or pruned detail recorded.":"")+(e.ExhaustionCount>0?"\nResource-exhaustion events in the last 90 days: "+e.ExhaustionCount+(e.Exhaustion?" (not reviewed).":" (reviewed)."):"");
   var snap=status.Snapshot;
   OverviewDetails.Text=snap==null?"No snapshot":$"Automatic management: {snap.Automatic}  |  Dump setting: {Dump(snap.DumpType)}\n"+string.Join("\n",snap.Active.Select(x=>x.Path+"  allocated "+x.AllocatedMB+" MB; used "+x.UsedMB+" MB; peak "+x.PeakMB+" MB"))+"\n\n"+string.Join("\n",snap.Volumes.Select(v=>v.Drive+"  "+v.Model+"  free "+Units.Size(v.Free)+"  "+(v.Eligible?"internal SSD":"eligibility unconfirmed")))+"\n\nTop private-memory users: "+string.Join(", ",status.Processes.Take(5).Select(p=>p.Name+" "+Units.Size(p.PrivateBytes)));
   RecommendationText.Text=RecommendationDescription();var p=status.Policy??new Policy();PolicyText.Text="Auto-grow: "+(p.AutoGrow?"enabled":"disabled")+"  |  Ceiling: "+p.CeilingMB/1024+" GiB\n"+(p.PauseReason??"No automation pause recorded.");
   if(status.Journal!=null && string.IsNullOrEmpty(ChangeText.Text))ChangeText.Text=Json.Write(status.Journal);
   tray.Text="Pagefile Monitor: "+(status.Journal?.Phase=="pending-restart"?"restart pending":shownMark!=null&&shownMark!="unmarked"&&shownMark!="?"?shownMark+" workload marked":"monitoring");
  }
  void RenderMarker(string mark)
  {
   bool known=mark!=null;string key=known&&MarkColors.ContainsKey(mark)?mark:"unmarked";bool active=known&&key!="unmarked";var color=MarkColors[key];
   if(mark!=shownMark)
   {
    // The dashboard only knows when it saw the marker change, not when an earlier session began.
    markSince=shownMark=="?"||!active?(DateTime?)null:DateTime.Now;shownMark=mark;
    MarkerDot.BeginAnimation(OpacityProperty,active?new DoubleAnimation(1,.2,TimeSpan.FromSeconds(.8)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever}:null);
    MarkerBadge.BorderBrush=B(color);MarkerBadge.Background=B(color,(byte)(active?0x38:0x14));MarkerDot.Fill=B(color);MarkerStrip.Background=B(color,(byte)(active?0xFF:0x50));
    MarkerBadgeText.Foreground=active?B(color):B(Muted);MarkerBadgeText.Text=!known?"SERVICE UNAVAILABLE":active?key.ToUpperInvariant()+" WORKLOAD":"NO WORKLOAD MARKED";
    Title=active?"Pagefile Monitor — "+key+" workload":"Pagefile Monitor";
    foreach(var pair in new Dictionary<string,Button>{{"light",MarkLight},{"normal",MarkNormal},{"heavy",MarkHeavy},{"unmarked",MarkNone}})
    {
     var c=MarkColors[pair.Key];bool on=known&&pair.Key==key&&active;
     pair.Value.BorderBrush=B(c);pair.Value.BorderThickness=new Thickness(on?2:1);pair.Value.Background=on?B(c):B(c,0x18);pair.Value.Foreground=on?B(Color.FromRgb(2,13,35)):B(pair.Key=="unmarked"?Muted:c);pair.Value.FontWeight=on?FontWeights.Bold:FontWeights.Normal;
    }
   }
   var e=status?.Evidence;
   if(!known)MarkerBadgeSub.Text="Marker state is unknown until the service answers";
   else if(!active)MarkerBadgeSub.Text="Press Light, Normal or Heavy while you work";
   else
   {
    long have=e==null?0:key=="light"?e.Light:key=="normal"?e.Normal:e.Heavy,need=key=="normal"?7200:1800;
    MarkerBadgeSub.Text=(markSince.HasValue?"Recording for "+Span(DateTime.Now-markSince.Value):"Recording")+"  •  "+have/60+" of "+need/60+" min collected";
   }
  }
  static string Span(TimeSpan t)=>t.TotalHours>=1?(int)t.TotalHours+" h "+t.Minutes+" min":t.TotalMinutes>=1?(int)t.TotalMinutes+" min":"under a minute";
  static FrameworkElement Bar(string label,double fraction,string value,Color color,double labelWidth=170,double valueWidth=170)
  {
   fraction=double.IsNaN(fraction)?0:Math.Max(0,Math.Min(1,fraction));
   var row=new Grid{Margin=new Thickness(0,4,0,4)};
   row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(labelWidth)});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(valueWidth)});
   row.Children.Add(new TextBlock{Text=label,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)});
   var track=new Grid{Height=12,VerticalAlignment=VerticalAlignment.Center};
   track.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(0x12,0x27,0x45)),CornerRadius=new CornerRadius(6)});
   var fill=new Grid();fill.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(Math.Max(fraction,.0001),GridUnitType.Star)});fill.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(Math.Max(1-fraction,.0001),GridUnitType.Star)});
   fill.Children.Add(new Border{Background=B(color),CornerRadius=new CornerRadius(6)});track.Children.Add(fill);
   Grid.SetColumn(track,1);row.Children.Add(track);
   var text=new TextBlock{Text=value,TextWrapping=TextWrapping.NoWrap,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,Foreground=B(Muted),FontSize=12};
   Grid.SetColumn(text,2);row.Children.Add(text);return row;
  }
  void RenderReadiness()
  {
   ReadinessPanel.Children.Clear();var e=status.Evidence;var p=status.Policy??new Policy();
   if(e==null){ReadinessPanel.Children.Add(new TextBlock{Text="Progress appears once the installed service is connected.",Foreground=B(Muted)});ReadinessTitle.Text="PROGRESS TOWARD A RECOMMENDATION";return;}
   double days=e.First==0?0:(Units.Now-e.First)/86400.0;int done=0,total=5;
   Action<string,double,double,string,Color> add=(label,have,need,unit,color)=>
   {bool ok=have>=need;if(ok)done++;ReadinessPanel.Children.Add(Bar((ok?"✓ ":"○ ")+label,have/need,(have>=need*10?have.ToString("F0"):have.ToString(unit=="days"||unit=="h"?"F1":"F0"))+" of "+need.ToString("F0")+" "+unit,ok?Good:color));};
   add("Days of learning",days,7,"days",Cyan);add("Valid samples",e.ValidSeconds/3600.0,24,"h",Cyan);
   add("Light workload",e.Light/60,30,"min",MarkColors["light"]);add("Normal workload",e.Normal/60,120,"min",MarkColors["normal"]);add("Heavy workload",e.Heavy/60,30,"min",MarkColors["heavy"]);
   bool confirmed=p.HeavyConfirmed&&p.HeavyConfirmedSeconds>=1800;total++;if(confirmed)done++;
   ReadinessPanel.Children.Add(Bar((confirmed?"✓ ":"○ ")+"Heavy sessions confirmed",confirmed?1:0,confirmed?"confirmed":e.Heavy<1800?"needs heavy sessions first":"waiting for you",confirmed?Good:Warn));
   if(e.ExhaustionCount>0){total++;if(!e.Exhaustion)done++;ReadinessPanel.Children.Add(Bar((e.Exhaustion?"○ ":"✓ ")+"Exhaustion events reviewed",e.Exhaustion?0:1,e.ExhaustionCount+" event(s), "+(e.Exhaustion?"not reviewed":"reviewed"),e.Exhaustion?Bad:Good));}
   ReadinessTitle.Text="PROGRESS TOWARD A RECOMMENDATION  •  "+done+" OF "+total+" COMPLETE";
  }
  void RenderBars()
  {
   ProcessPanel.Children.Clear();var top=status.Processes.Take(8).ToList();
   if(top.Count==0)ProcessPanel.Children.Add(new TextBlock{Text="Process detail arrives with the next service sample.",Foreground=B(Muted)});
   long largest=top.Count==0?1:Math.Max(1,top.Max(x=>x.PrivateBytes));
   foreach(var x in top)ProcessPanel.Children.Add(Bar(x.Name,x.PrivateBytes/(double)largest,Units.Size(x.PrivateBytes),Cyan,150,80));
   DrivePanel.Children.Clear();var snap=status.Snapshot;if(snap==null)return;
   foreach(var v in snap.Volumes)
   {
    long page=snap.Active.Where(a=>a.Path.StartsWith(v.Drive,StringComparison.OrdinalIgnoreCase)).Sum(a=>(long)a.AllocatedMB)*Units.MiB;bool low=v.Free<RecommendationEngine.Reserve(v);
    DrivePanel.Children.Add(Bar(v.Drive+"  "+v.Model,v.Size<=0?0:1-v.Free/(double)v.Size,Units.Size(v.Free)+" free"+(page>0?"  •  page file "+Units.Size(page):""),low?Warn:Cyan,150,230));
   }
  }
  void DrawLive()
  {
   LiveChart.Children.Clear();if(live.Count<2){LiveLabel.Text="Live committed memory — collecting…";return;}
   double w=Math.Max(200,LiveChart.ActualWidth),h=Math.Max(60,LiveChart.ActualHeight),top=Math.Max(1,live.Max(x=>Math.Max(x.Limit,x.Commit))*1.06),step=w/199.0,offset=w-(live.Count-1)*step;
   Func<int,double> X=i=>offset+i*step;Func<double,double> Y=v=>h-v/top*h;
   foreach(var guide in new[]{Tuple.Create(live[live.Count-1].Limit,Bad,"commit limit"),Tuple.Create(live[live.Count-1].Physical,Muted,"usable RAM")})
   {
    LiveChart.Children.Add(new Line{X1=0,X2=w,Y1=Y(guide.Item1),Y2=Y(guide.Item1),Stroke=B(guide.Item2,0xB0),StrokeThickness=1,StrokeDashArray=new DoubleCollection{4,4}});
    var tag=new TextBlock{Text=guide.Item3,FontSize=10,Foreground=B(guide.Item2)};Canvas.SetLeft(tag,4);Canvas.SetTop(tag,Y(guide.Item1)+1);LiveChart.Children.Add(tag);
   }
   // One filled run per marker so the graph shows which workload each stretch was recorded under.
   int start=0;
   for(int i=1;i<=live.Count;i++)
   {
    if(i<live.Count && live[i].Mark==live[start].Mark)continue;
    int end=Math.Min(i,live.Count-1);var color=MarkColors.ContainsKey(live[start].Mark)&&live[start].Mark!="unmarked"?MarkColors[live[start].Mark]:Cyan;
    var area=new Polygon{Fill=B(color,0x38)};var line=new Polyline{Stroke=B(color),StrokeThickness=2,StrokeLineJoin=PenLineJoin.Round};
    area.Points.Add(new Point(X(start),h));for(int k=start;k<=end;k++){var pt=new Point(X(k),Y(live[k].Commit));area.Points.Add(pt);line.Points.Add(pt);}area.Points.Add(new Point(X(end),h));
    LiveChart.Children.Add(area);LiveChart.Children.Add(line);start=i;
   }
   var last=live[live.Count-1];var dot=new Ellipse{Width=9,Height=9,Fill=B(Colors.White)};Canvas.SetLeft(dot,X(live.Count-1)-4.5);Canvas.SetTop(dot,Y(last.Commit)-4.5);LiveChart.Children.Add(dot);
   LiveLabel.Text="Live committed memory  •  last "+Span(TimeSpan.FromSeconds((live.Count-1)*3))+"  •  now "+Units.Size((long)last.Commit)+"  •  colour follows the workload marker";
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
   Chart.Children.Clear();var inv=System.Globalization.CultureInfo.InvariantCulture;
   var rows=history.Split('\n').Skip(1).Where(l=>!string.IsNullOrWhiteSpace(l)).Reverse().Select(l=>l.Trim().Split(',')).Where(c=>c.Length>=8).ToList();if(rows.Count==0)return;
   var values=rows.Select(c=>double.Parse(c[1],inv)/Units.GiB).ToList();double ram=rows.Max(c=>double.Parse(c[3],inv))/Units.GiB;
   var marks=rows.Select(c=>{long l=long.Parse(c[5],inv),n=long.Parse(c[6],inv),hv=long.Parse(c[7].Trim(),inv);return hv>0&&hv>=n&&hv>=l?"heavy":n>0&&n>=l?"normal":l>0?"light":"unmarked";}).ToList();
   double maximum=Math.Max(1,Math.Max(values.Max(),ram)*1.1),width=Math.Max(200,Chart.ActualWidth),height=Math.Max(100,Chart.ActualHeight),step=width/Math.Max(1,values.Count-1);
   for(int i=0,start=0;i<=marks.Count;i++)
   {
    if(i<marks.Count && marks[i]==marks[start])continue;
    if(marks[start]!="unmarked"){var band=new Rectangle{Width=Math.Max(2,(i-start)*step),Height=height,Fill=B(MarkColors[marks[start]],0x48)};Canvas.SetLeft(band,start*step);Chart.Children.Add(band);}
    start=i;
   }
   Chart.Children.Add(new Line{X1=0,X2=width,Y1=height-ram/maximum*height,Y2=height-ram/maximum*height,Stroke=B(Muted,0xB0),StrokeThickness=1,StrokeDashArray=new DoubleCollection{4,4}});
   var area=new Polygon{Fill=B(Cyan,0x30)};var line=new Polyline{Stroke=B(Cyan),StrokeThickness=2};area.Points.Add(new Point(0,height));
   for(int i=0;i<values.Count;i++){var pt=new Point(i*step,height-values[i]/maximum*height);line.Points.Add(pt);area.Points.Add(pt);}
   area.Points.Add(new Point((values.Count-1)*step,height));Chart.Children.Add(area);Chart.Children.Add(line);
   int marked=marks.Count(m=>m!="unmarked");
   ChartLabel.Text="Peak shown: "+values.Max().ToString("F2")+" GiB  •  dashed line: usable RAM "+ram.ToString("F1")+" GiB  •  shaded: marked workload ("+marked+" min, green light / blue normal / orange heavy)";
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
   Uri uri;
   if(!CommerceConfig.Load().TryCheckout(out uri)){MessageBox.Show("Pro sales are not configured in this build yet. Free features are available now.");return;}
   Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
  }
  async void ActivateLicense(object sender,RoutedEventArgs e)=>await Elevated("--license-activate","Activate Pro for this PC. Enter the key from your Gumroad receipt in the administrator dialog; validation sends it to Gumroad. No memory telemetry is sent. Service status refreshes within one minute.");
  async void DeactivateLicense(object sender,RoutedEventArgs e)=>await Elevated("--license-deactivate","Remove the license key from this PC. Automatic growth pauses after the next check; Free features and recovery remain available. This does not give back one of the key's three activations.");
  void TmogSite(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo("https://tmog.org/"){UseShellExecute=true});
  void Tray(object sender,RoutedEventArgs e){tray.Visible=true;Hide();}
 }
}
