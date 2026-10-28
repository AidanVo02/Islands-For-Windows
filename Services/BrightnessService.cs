using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DynamicIsland.Services
{
    public class BrightnessChangedEventArgs : EventArgs
    {
        public int Brightness { get; set; }
    }

    public class BrightnessService
    {
        private static readonly Lazy<BrightnessService> _lazy = new(() => new BrightnessService());
        public static BrightnessService Instance => _lazy.Value;

        public event EventHandler<BrightnessChangedEventArgs>? BrightnessChanged;

        private int _cachedBrightness = 70;
        private ManagementEventWatcher? _brightnessWatcher;

        private BrightnessService()
        {
            _cachedBrightness = GetBrightness();
            StartBrightnessWatcher();
        }

        private void StartBrightnessWatcher()
        {
            try
            {
                var scope = new ManagementScope(@"root\wmi");
                var query = new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
                _brightnessWatcher = new ManagementEventWatcher(scope, query);
                _brightnessWatcher.EventArrived += (s, e) =>
                {
                    try
                    {
                        if (e.NewEvent["Brightness"] != null)
                        {
                            int b = Convert.ToInt32(e.NewEvent["Brightness"]);
                            _cachedBrightness = b;
                            BrightnessChanged?.Invoke(this, new BrightnessChangedEventArgs { Brightness = b });
                        }
                    }
                    catch {}
                };
                _brightnessWatcher.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrightnessService] Watcher start error: {ex.Message}");
            }
        }

        public int GetBrightness()
        {
            try
            {
                // Thử cách 1: WMI WmiMonitorBrightness (cho laptop)
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["CurrentBrightness"] != null)
                    {
                        _cachedBrightness = Convert.ToInt32(obj["CurrentBrightness"]);
                        return _cachedBrightness;
                    }
                }
            }
            catch {}

            // Thử cách 2: Desktop monitor qua dxva2.dll
            int desktopBri = GetDesktopMonitorBrightness();
            if (desktopBri >= 0)
            {
                _cachedBrightness = desktopBri;
                return _cachedBrightness;
            }

            return _cachedBrightness;
        }

        private System.Threading.CancellationTokenSource? _brightnessCts;
        private readonly object _brightnessLock = new();

        public void SetBrightness(int brightness)
        {
            brightness = Math.Clamp(brightness, 0, 100);
            _cachedBrightness = brightness;

            // Phát event ngay lập tức để HUD và UI phản hồi 0ms
            BrightnessChanged?.Invoke(this, new BrightnessChangedEventArgs { Brightness = brightness });

            lock (_brightnessLock)
            {
                _brightnessCts?.Cancel();
                _brightnessCts = new System.Threading.CancellationTokenSource();
                var token = _brightnessCts.Token;

                Task.Run(async () =>
                {
                    try
                    {
                        // Debounce 40ms để tránh nghẽn WMI/DXVA2 khi kéo slider liên tục
                        await Task.Delay(40, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (token.IsCancellationRequested) return;

                    bool success = false;
                    try
                    {
                        // Cách 1: WMI (Laptop)
                        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            if (token.IsCancellationRequested) return;
                            obj.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)brightness });
                            success = true;
                            break;
                        }
                    }
                    catch {}

                    if (!success && !token.IsCancellationRequested)
                    {
                        // Cách 2: dxva2.dll (Desktop monitor)
                        SetDesktopMonitorBrightness(brightness);
                    }
                }, token);
            }
        }

        public void AdjustBrightness(int delta)
        {
            int current = _cachedBrightness;
            SetBrightness(current + delta);
        }

        #region Desktop Monitor DXVA2 Interop
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        private int GetDesktopMonitorBrightness()
        {
            try
            {
                IntPtr hMonitor = MonitorFromWindow(IntPtr.Zero, 1); // MONITOR_DEFAULTTOPRIMARY
                if (hMonitor == IntPtr.Zero) return -1;

                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0) return -1;

                var monitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return -1;

                try
                {
                    if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out uint cur, out uint max))
                    {
                        if (max > min)
                        {
                            return (int)((cur - min) * 100 / (max - min));
                        }
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, monitors);
                }
            }
            catch {}
            return -1;
        }

        private void SetDesktopMonitorBrightness(int percent)
        {
            try
            {
                IntPtr hMonitor = MonitorFromWindow(IntPtr.Zero, 1);
                if (hMonitor == IntPtr.Zero) return;

                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0) return;

                var monitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return;

                try
                {
                    if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out uint cur, out uint max))
                    {
                        uint target = min + (uint)((max - min) * (percent / 100.0));
                        SetMonitorBrightness(monitors[0].hPhysicalMonitor, target);
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, monitors);
                }
            }
            catch {}
        }
        #endregion
    }
}
