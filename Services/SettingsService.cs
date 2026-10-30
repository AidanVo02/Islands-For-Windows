using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland.Services
{
    public class IslandSettings
    {
        // Vị trí & Màn hình
        public int MonitorIndex { get; set; } = 0;
        public double TopOffset { get; set; } = 3;
        public double HorizontalOffset { get; set; } = 0;
        public bool AlwaysOnTop { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool AutoHide { get; set; } = false;
        public double AutoHideDelay { get; set; } = 1.5;
        public bool LockPosition { get; set; } = false;

        // Kích thước & Hình dáng
        public double IdleWidth { get; set; } = 190;
        public double IdleHeight { get; set; } = 36;
        public double ExpandedWidth { get; set; } = 400;
        public double ExpandedHeight { get; set; } = 185;
        public double CornerRadius { get; set; } = 18;

        // Giao diện & Màu sắc
        public string ThemeBackground { get; set; } = "#080809";
        public string BorderColor { get; set; } = "#2A2A30";
        public bool EnableShadow { get; set; } = true;
        public double ShadowOpacity { get; set; } = 0.6;
        public double ShadowBlur { get; set; } = 20;

        // Tính năng & Widget
        public bool ShowClock { get; set; } = true;
        public bool Use24HourClock { get; set; } = true;
        public bool ShowBattery { get; set; } = true;
        public bool HoverAnimation { get; set; } = true;
        public bool EqualizerEnabled { get; set; } = true;
        public bool AdaptiveColorGlowEnabled { get; set; } = true;
        public bool AutoSwitchMedia { get; set; } = true;
        public bool VolumeBrightnessHudEnabled { get; set; } = true;
        public bool MouseWheelVolumeEnabled { get; set; } = true;
        public bool EnablePrivacyIndicators { get; set; } = true;
        public bool EnableLockKeyNotifications { get; set; } = true;

        // Tìm kiếm nhanh & Khởi chạy ứng dụng (Quick Search & App Launcher)
        public bool EnableSearchHotkey { get; set; } = true;

        // Truyền file sang điện thoại
        // "WindowsShare": Phương án 1 (Phone Link, Nearby Share - Hoạt động mọi nơi, kể cả quán cafe có AP Isolation)
        // "LocalSend": Phương án 2 (Mạng nội bộ gia đình)
        public string FileTransferMethod { get; set; } = "WindowsShare";
        public string LocalSendTargetIp { get; set; } = "";
        public int LocalSendPort { get; set; } = 53317;
        public string LocalSendTargetName { get; set; } = "";
        public bool LocalSendAutoDiscovery { get; set; } = true;

        public IslandSettings Clone()
        {
            return (IslandSettings)this.MemberwiseClone();
        }
    }

    public class SettingsService
    {
        private static readonly Lazy<SettingsService> _lazy = new(() => new SettingsService());
        public static SettingsService Instance => _lazy.Value;

        private readonly string _settingsFilePath;
        private IslandSettings _current = new();

        public event Action<IslandSettings>? SettingsChanged;

        public IslandSettings Current => _current;

        private SettingsService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "DynamicIsland");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _settingsFilePath = Path.Combine(folder, "settings.json");

            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<IslandSettings>(json);
                    if (loaded != null)
                    {
                        _current = loaded;
                    }
                }
                else
                {
                    Save();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
                _current = new IslandSettings();
            }

            // Đồng bộ trạng thái khởi động cùng Windows
            _current.StartWithWindows = IsStartupEnabled();
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_current, options);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public void Update(Action<IslandSettings> updateAction)
        {
            updateAction(_current);
            Save();
            SettingsChanged?.Invoke(_current);
        }

        public void ResetToDefaults()
        {
            _current = new IslandSettings();
            SetStartup(false);
            Save();
            SettingsChanged?.Invoke(_current);
        }

        public void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    string? appPath = Environment.ProcessPath;
                    if (enable && !string.IsNullOrEmpty(appPath))
                    {
                        key.SetValue("DynamicIsland", $"\"{appPath}\"");
                        _current.StartWithWindows = true;
                    }
                    else
                    {
                        key.DeleteValue("DynamicIsland", false);
                        _current.StartWithWindows = false;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update startup registry: {ex.Message}");
            }
        }

        public bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("DynamicIsland") != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
