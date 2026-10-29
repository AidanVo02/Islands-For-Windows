using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "DynamicIsland_SingleInstance_Mutex_App";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }
}
