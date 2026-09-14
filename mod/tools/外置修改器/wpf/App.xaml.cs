using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PvzheRemote;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 崩溃日志（诊断用）
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                string msg = "[" + DateTime.Now.ToString("HH:mm:ss") + "] Dispatcher\n" +
                    args.Exception + "\n--- INNER ---\n" +
                    (args.Exception.InnerException?.ToString() ?? "无") + "\n\n";
                File.AppendAllText("err.txt", msg);
            }
            catch { }
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try { File.AppendAllText("err.txt", "[Unhandled] " + args.ExceptionObject + "\n\n"); } catch { }
        };
        base.OnStartup(e);
    }
}

