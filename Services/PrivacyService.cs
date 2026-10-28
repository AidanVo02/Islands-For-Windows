using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Timer = System.Threading.Timer;
using Microsoft.Win32;

namespace DynamicIsland.Services
{
    public class PrivacyStatusChangedEventArgs : EventArgs
    {
        public bool IsMicrophoneInUse { get; set; }
        public List<string> MicrophoneApps { get; set; } = new();
        public bool IsCameraInUse { get; set; }
        public List<string> CameraApps { get; set; } = new();
        public bool IsMicrophoneMuted { get; set; }

        public string MicrophoneSummary => MicrophoneApps.Count > 0 
            ? string.Join(", ", MicrophoneApps) 
            : "Không có ứng dụng nào";

        public string CameraSummary => CameraApps.Count > 0 
            ? string.Join(", ", CameraApps) 
            : "Không có ứng dụng nào";
    }

    public class PrivacyService : IDisposable
    {
        private static readonly Lazy<PrivacyService> _lazy = new(() => new PrivacyService());
        public static PrivacyService Instance => _lazy.Value;

        private readonly Timer _pollTimer;
        private bool _isDisposed;

        private HashSet<string> _lastMicApps = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _lastCamApps = new(StringComparer.OrdinalIgnoreCase);

        public bool IsMicrophoneInUse { get; private set; }
        public List<string> MicrophoneApps { get; private set; } = new();
        public bool IsCameraInUse { get; private set; }
        public List<string> CameraApps { get; private set; } = new();
        public bool IsMicrophoneMuted => VolumeService.Instance.IsMicrophoneMuted();

        public event EventHandler<PrivacyStatusChangedEventArgs>? PrivacyStatusChanged;

        private PrivacyService()
        {
            _pollTimer = new Timer(PollTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            // Kiểm tra ngay lập tức và lặp lại mỗi 1 giây
            _pollTimer.Change(0, 1000);
            VolumeService.Instance.MicrophoneMuteChanged += (s, muted) =>
            {
                TriggerStatusChanged();
            };
        }

        public void Stop()
        {
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public bool ToggleMicrophoneMute()
        {
            bool newMute = VolumeService.Instance.ToggleMicrophoneMute();
            TriggerStatusChanged();
            return newMute;
        }

        private void PollTimerCallback(object? state)
        {
            try
            {
                CheckPrivacyStatus();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PrivacyService] Poll error: {ex.Message}");
            }
        }

        private void CheckPrivacyStatus()
        {
            var micApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var camApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CheckConsentStore(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", micApps);
            CheckConsentStore(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", camApps);

            bool micInUse = micApps.Count > 0;
            bool camInUse = camApps.Count > 0;
            bool micMuted = VolumeService.Instance.IsMicrophoneMuted();

            bool changed = micInUse != IsMicrophoneInUse ||
                           camInUse != IsCameraInUse ||
                           !micApps.SetEquals(_lastMicApps) ||
                           !camApps.SetEquals(_lastCamApps);

            if (changed)
            {
                IsMicrophoneInUse = micInUse;
                MicrophoneApps = micApps.ToList();
                IsCameraInUse = camInUse;
                CameraApps = camApps.ToList();
                _lastMicApps = micApps;
                _lastCamApps = camApps;

                TriggerStatusChanged();
            }
        }

        private void TriggerStatusChanged()
        {
            PrivacyStatusChanged?.Invoke(this, new PrivacyStatusChangedEventArgs
            {
                IsMicrophoneInUse = IsMicrophoneInUse,
                MicrophoneApps = MicrophoneApps,
                IsCameraInUse = IsCameraInUse,
                CameraApps = CameraApps,
                IsMicrophoneMuted = IsMicrophoneMuted
            });
        }

        private void CheckConsentStore(string rootPath, HashSet<string> activeApps)
        {
            try
            {
                using var baseKey = Registry.CurrentUser.OpenSubKey(rootPath);
                if (baseKey == null) return;

                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    if (subKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                    {
                        using var nonPackagedKey = baseKey.OpenSubKey(subKeyName);
                        if (nonPackagedKey != null)
                        {
                            foreach (var appKeyName in nonPackagedKey.GetSubKeyNames())
                            {
                                using var appKey = nonPackagedKey.OpenSubKey(appKeyName);
                                if (appKey != null && IsAppAccessingDevice(appKey))
                                {
                                    activeApps.Add(CleanNonPackagedAppName(appKeyName));
                                }
                            }
                        }
                    }
                    else
                    {
                        using var appKey = baseKey.OpenSubKey(subKeyName);
                        if (appKey != null && IsAppAccessingDevice(appKey))
                        {
                            activeApps.Add(CleanPackagedAppName(subKeyName));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PrivacyService] Registry check error: {ex.Message}");
            }
        }

        private static bool IsAppAccessingDevice(RegistryKey key)
        {
            object? startObj = key.GetValue("LastUsedTimeStart");
            object? stopObj = key.GetValue("LastUsedTimeStop");
            if (startObj is long start && start > 0)
            {
                long stop = stopObj is long s ? s : 0;
                return stop == 0 || start > stop;
            }
            return false;
        }

        private static string CleanNonPackagedAppName(string rawKey)
        {
            try
            {
                string path = rawKey.Replace('#', '\\');
                if (File.Exists(path))
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                        return vi.FileDescription.Trim();
                }

                string name = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(name, "msedge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
                if (string.Equals(name, "chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
                if (string.Equals(name, "discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
                if (string.Equals(name, "zoom", StringComparison.OrdinalIgnoreCase)) return "Zoom";
                if (string.Equals(name, "obs64", StringComparison.OrdinalIgnoreCase)) return "OBS Studio";
                if (string.Equals(name, "telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
                if (string.Equals(name, "zalo", StringComparison.OrdinalIgnoreCase)) return "Zalo";
                if (string.Equals(name, "spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";

                if (name.Length > 0)
                    return char.ToUpper(name[0]) + name.Substring(1);

                return path;
            }
            catch
            {
                return rawKey;
            }
        }

        private static string CleanPackagedAppName(string packageFamilyName)
        {
            try
            {
                int underscore = packageFamilyName.IndexOf('_');
                string baseName = underscore > 0 ? packageFamilyName.Substring(0, underscore) : packageFamilyName;
                if (baseName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName.Substring("Microsoft.".Length);
                }
                if (baseName.Equals("WindowsCamera", StringComparison.OrdinalIgnoreCase)) return "Camera";
                if (baseName.Equals("SkypeApp", StringComparison.OrdinalIgnoreCase)) return "Skype";
                if (baseName.Equals("Teams", StringComparison.OrdinalIgnoreCase)) return "Microsoft Teams";
                if (baseName.Equals("WindowsSoundRecorder", StringComparison.OrdinalIgnoreCase)) return "Ghi âm (Sound Recorder)";
                return baseName;
            }
            catch
            {
                return packageFamilyName;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _pollTimer.Dispose();
        }
    }
}
