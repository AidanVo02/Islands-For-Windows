using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                File.WriteAllText(LogFile, $"[UnhandledException] {args.ExceptionObject}");
            }
            catch {}
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.WriteAllText(LogFile, $"[DispatcherUnhandledException] {args.Exception}");
            }
            catch {}
            args.Handled = true;
        };

        if (e.Args.Length > 0 && (e.Args[0] == "--settings" || e.Args[0] == "-s" || e.Args[0] == "/settings"))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SettingsWindow.ShowOrActivate();
            }), DispatcherPriority.Loaded);
        }
    }
}
