using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
namespace PagefileMonitorDesktop
{
 public partial class App : Application
 {
  protected override void OnStartup(StartupEventArgs e)
  {
   base.OnStartup(e);
   DispatcherUnhandledException+=(s,a)=>{MessageBox.Show(a.Exception.Message,"Pagefile Monitor");a.Handled=true;};
   if(e.Args.Contains("--smoke")){var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(8)};timer.Tick+=(s,a)=>Shutdown();timer.Start();}
  }
 }
}
