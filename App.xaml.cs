using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Interaction logic for App.xaml
/// Bổ sung Global Crash Guard, khôi phục UI an toàn khi xảy ra ngoại lệ, và kích hoạt Hardware Acceleration.
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
    private static readonly string LogFile = Path.Combine(LogDir, "crash.log");
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// Ghi log lỗi chi tiết vào file %APPDATA%\DynamicIsland\crash.log
    /// </summary>
    public static void LogCrash(string source, Exception? ex, string? extra = null)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string extraInfo = string.IsNullOrWhiteSpace(extra) ? string.Empty : $" [{extra}]";
            string logText = $"[{timestamp}] [{source}]{extraInfo}\n{ex?.ToString() ?? "Unknown Exception"}\n" +
                             new string('-', 50) + "\n";
            File.AppendAllText(LogFile, logText);
        }
        catch
        {
            // Tránh phát sinh lỗi khi đang ghi log crash
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1. Global Crash Guard: Đăng ký Exception Handlers ĐẦU TIÊN trước mọi logic khởi tạo
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            LogCrash("AppDomain.UnhandledException", ex, $"IsTerminating={args.IsTerminating}");
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved(); // Đánh dấu đã xử lý để không làm sập Runtime
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // Ngăn văng ứng dụng khi có lỗi UI không nghiêm trọng

            // Tự động khôi phục viên thuốc về trạng thái Idle an toàn
            try
            {
                if (MainWindow is MainWindow mw)
                {
                    mw.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            mw.TransitionToState(IslandState.Idle);
                        }
                        catch { }
                    }), DispatcherPriority.Normal);
                }
            }
            catch { }
        };

        // 2. Ép kích hoạt Hardware Acceleration trên toàn bộ Process
        try
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        }
        catch (Exception ex)
        {
            LogCrash("RenderOptions.ProcessRenderMode", ex);
        }

        // 3. Kiểm tra Single Instance
        const string mutexName = "Local\\DynamicIsland_SingleInstance_Mutex_App";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            var running = System.Diagnostics.Process.GetProcessesByName(current.ProcessName);
            if (running.Length > 1)
            {
                Shutdown();
                return;
            }
        }

        try
        {
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            LogCrash("base.OnStartup", ex);
            throw;
        }

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
