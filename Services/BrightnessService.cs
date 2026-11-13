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

            var debounceCts = new System.Threading.CancellationTokenSource();
            lock (_brightnessLock)
            {
                _brightnessCts?.Cancel();
                _brightnessCts = debounceCts;
            }

            _ = SetBrightnessAfterDebounceAsync(brightness, debounceCts);
        }

        private async Task SetBrightnessAfterDebounceAsync(int brightness, System.Threading.CancellationTokenSource debounceCts)
        {
            try
            {
                // Debounce 50ms để tránh nghẽn WMI/DXVA2 khi kéo slider liên tục.
                await Task.Delay(50, debounceCts.Token).ConfigureAwait(false);
                if (debounceCts.IsCancellationRequested) return;

                bool success = false;
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (debounceCts.IsCancellationRequested) return;
                        obj.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)brightness });
                        success = true;
                        break;
                    }
                }
                catch { }

                if (!success && !debounceCts.IsCancellationRequested)
                {
                    success = SetDesktopMonitorBrightness(brightness);
                    if (!success)
                    {
                        SetSoftwareGammaBrightness(brightness);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A newer slider value superseded this pending update.
            }
            finally
            {
                lock (_brightnessLock)
                {
                    if (ReferenceEquals(_brightnessCts, debounceCts))
                    {
                        _brightnessCts = null;
                    }
                }

                debounceCts.Dispose();
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

        private bool SetDesktopMonitorBrightness(int percent)
        {
            try
            {
                IntPtr hMonitor = MonitorFromWindow(IntPtr.Zero, 1);
                if (hMonitor == IntPtr.Zero) return false;

                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0) return false;

                var monitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return false;

                try
                {
                    if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out uint cur, out uint max))
                    {
                        uint target = min + (uint)((max - min) * (percent / 100.0));
                        return SetMonitorBrightness(monitors[0].hPhysicalMonitor, target);
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, monitors);
                }
            }
            catch {}
            return false;
        }

        #region Software Gamma Ramp Fallback (For monitors without DDC/CI)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;
        }

        [DllImport("gdi32.dll")]
        private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private void SetSoftwareGammaBrightness(int percent)
        {
            try
            {
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return;
                try
                {
                    double factor = Math.Clamp(percent / 100.0, 0.15, 1.0);
                    var ramp = new RAMP
                    {
                        Red = new ushort[256],
                        Green = new ushort[256],
                        Blue = new ushort[256]
                    };
                    for (int i = 0; i < 256; i++)
                    {
                        ushort val = (ushort)Math.Clamp((int)(i * factor * 256.0), 0, 65535);
                        ramp.Red[i] = val;
                        ramp.Green[i] = val;
                        ramp.Blue[i] = val;
                    }
                    SetDeviceGammaRamp(hdc, ref ramp);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
            catch { }
        }
        #endregion
        #endregion
    }
}
