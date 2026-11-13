using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Services
{
    public class VolumeChangedEventArgs : EventArgs
    {
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public int VolumePercent => (int)Math.Round(Volume * 100);
    }

    public class AudioDeviceInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Dịch vụ quản lý âm lượng và Microphone hệ thống sử dụng NAudio CoreAudioApi.
    /// Tự động bắt sự kiện thay đổi thiết bị (cắm/rút tai nghe, đổi output) và tự phục hồi khi có lỗi COM.
    /// </summary>
    public class VolumeService : IDisposable, IMMNotificationClient
    {
        private static readonly Lazy<VolumeService> _lazy = new(() => new VolumeService());
        public static VolumeService Instance => _lazy.Value;

        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _playbackDevice;
        private MMDevice? _captureDevice;
        private readonly object _lock = new();
        private bool _isDisposed;

        // Throttling / Debouncing cho thanh trượt Volume để không làm đứng UI Thread
        private System.Threading.CancellationTokenSource? _volumeCts;
        private readonly object _volumeCtsLock = new();
        private volatile float _cachedVolume = -1f;

        public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;
        public event EventHandler<bool>? MicrophoneMuteChanged;

        private VolumeService()
        {
            Initialize();
        }

        private void Initialize()
        {
            lock (_lock)
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    _enumerator.RegisterEndpointNotificationCallback(this);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeService] Register notification error: {ex.Message}");
                }

                InitPlaybackDeviceInternal();
                InitCaptureDeviceInternal();
            }
        }

        private void InitPlaybackDeviceInternal()
        {
            try
            {
                if (_playbackDevice != null)
                {
                    try
                    {
                        _playbackDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                    }
                    catch { }
                    _playbackDevice.Dispose();
                    _playbackDevice = null;
                }

                _enumerator ??= new MMDeviceEnumerator();
                _playbackDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (_playbackDevice?.AudioEndpointVolume != null)
                {
                    _playbackDevice.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] InitPlaybackDevice error: {ex.Message}");
            }
        }

        private void InitCaptureDeviceInternal()
        {
            try
            {
                if (_captureDevice != null)
                {
                    _captureDevice.Dispose();
                    _captureDevice = null;
                }

                _enumerator ??= new MMDeviceEnumerator();
                _captureDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] InitCaptureDevice error: {ex.Message}");
            }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            VolumeChanged?.Invoke(this, new VolumeChangedEventArgs
            {
                Volume = data.MasterVolume,
                IsMuted = data.Muted
            });
        }

        public float GetVolume()
        {
            lock (_lock)
            {
                try
                {
                    if (_playbackDevice?.AudioEndpointVolume == null)
                    {
                        InitPlaybackDeviceInternal();
                    }
                    return _playbackDevice?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0f;
                }
                catch
                {
                    try
                    {
                        InitPlaybackDeviceInternal();
                        return _playbackDevice?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0f;
                    }
                    catch
                    {
                        return 0f;
                    }
                }
            }
        }

        public bool IsMuted()
        {
            lock (_lock)
            {
                try
                {
                    if (_playbackDevice?.AudioEndpointVolume == null)
                    {
                        InitPlaybackDeviceInternal();
                    }
                    return _playbackDevice?.AudioEndpointVolume?.Mute ?? false;
                }
                catch
                {
                    try
                    {
                        InitPlaybackDeviceInternal();
                        return _playbackDevice?.AudioEndpointVolume?.Mute ?? false;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Chỉnh âm lượng kèm Debounce (50ms) chạy trên Background Task để đảm bảo UI mượt 60 FPS khi kéo nhanh
        /// </summary>
        public void SetVolume(float level)
        {
            float target = Math.Clamp(level, 0f, 1f);
            _cachedVolume = target;
            var debounceCts = new System.Threading.CancellationTokenSource();

            lock (_volumeCtsLock)
            {
                _volumeCts?.Cancel();
                _volumeCts = debounceCts;
            }

            _ = SetVolumeAfterDebounceAsync(target, debounceCts);
        }

        private async Task SetVolumeAfterDebounceAsync(float target, System.Threading.CancellationTokenSource debounceCts)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(50, debounceCts.Token).ConfigureAwait(false);
                if (!debounceCts.IsCancellationRequested)
                {
                    SetVolumeHardware(target);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer slider value superseded this pending update.
            }
            finally
            {
                lock (_volumeCtsLock)
                {
                    if (ReferenceEquals(_volumeCts, debounceCts))
                    {
                        _volumeCts = null;
                    }
                }

                debounceCts.Dispose();
            }
        }

        /// <summary>
        /// Chỉnh âm lượng tức thì (dành cho con lăn chuột hoặc phím tắt) chạy trên Background Task
        /// </summary>
        public void SetVolumeImmediate(float level)
        {
            float target = Math.Clamp(level, 0f, 1f);
            _cachedVolume = target;

            lock (_volumeCtsLock)
            {
                _volumeCts?.Cancel();
                _volumeCts = null;
            }

            System.Threading.Tasks.Task.Run(() => SetVolumeHardware(target));
        }

        private void SetVolumeHardware(float target)
        {
            lock (_lock)
            {
                try
                {
                    if (_playbackDevice?.AudioEndpointVolume == null)
                    {
                        InitPlaybackDeviceInternal();
                    }
                    if (_playbackDevice?.AudioEndpointVolume != null)
                    {
                        _playbackDevice.AudioEndpointVolume.MasterVolumeLevelScalar = target;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeService] SetVolume retry: {ex.Message}");
                    try
                    {
                        InitPlaybackDeviceInternal();
                        if (_playbackDevice?.AudioEndpointVolume != null)
                        {
                            _playbackDevice.AudioEndpointVolume.MasterVolumeLevelScalar = target;
                        }
                    }
                    catch { }
                }
            }
        }

        public void AdjustVolume(float delta)
        {
            float current = GetVolume();
            SetVolumeImmediate(current + delta);
        }

        public void ToggleMute()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        if (_playbackDevice?.AudioEndpointVolume == null)
                        {
                            InitPlaybackDeviceInternal();
                        }
                        if (_playbackDevice?.AudioEndpointVolume != null)
                        {
                            _playbackDevice.AudioEndpointVolume.Mute = !_playbackDevice.AudioEndpointVolume.Mute;
                        }
                    }
                    catch
                    {
                        try
                        {
                            InitPlaybackDeviceInternal();
                            if (_playbackDevice?.AudioEndpointVolume != null)
                            {
                                _playbackDevice.AudioEndpointVolume.Mute = !_playbackDevice.AudioEndpointVolume.Mute;
                            }
                        }
                        catch { }
                    }
                }
            });
        }

        public bool IsMicrophoneMuted()
        {
            lock (_lock)
            {
                try
                {
                    if (_captureDevice?.AudioEndpointVolume == null)
                    {
                        InitCaptureDeviceInternal();
                    }
                    return _captureDevice?.AudioEndpointVolume?.Mute ?? false;
                }
                catch
                {
                    try
                    {
                        InitCaptureDeviceInternal();
                        return _captureDevice?.AudioEndpointVolume?.Mute ?? false;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        public bool ToggleMicrophoneMute()
        {
            bool newMute = false;
            bool success = false;
            lock (_lock)
            {
                try
                {
                    if (_captureDevice?.AudioEndpointVolume == null)
                    {
                        InitCaptureDeviceInternal();
                    }
                    if (_captureDevice?.AudioEndpointVolume != null)
                    {
                        newMute = !_captureDevice.AudioEndpointVolume.Mute;
                        _captureDevice.AudioEndpointVolume.Mute = newMute;
                        success = true;
                    }
                }
                catch
                {
                    try
                    {
                        InitCaptureDeviceInternal();
                        if (_captureDevice?.AudioEndpointVolume != null)
                        {
                            newMute = !_captureDevice.AudioEndpointVolume.Mute;
                            _captureDevice.AudioEndpointVolume.Mute = newMute;
                            success = true;
                        }
                    }
                    catch { }
                }
            }

            if (success)
            {
                MicrophoneMuteChanged?.Invoke(this, newMute);
                return newMute;
            }
            return false;
        }

        public void SetMicrophoneMute(bool isMuted)
        {
            bool success = false;
            lock (_lock)
            {
                try
                {
                    if (_captureDevice?.AudioEndpointVolume == null)
                    {
                        InitCaptureDeviceInternal();
                    }
                    if (_captureDevice?.AudioEndpointVolume != null)
                    {
                        _captureDevice.AudioEndpointVolume.Mute = isMuted;
                        success = true;
                    }
                }
                catch
                {
                    try
                    {
                        InitCaptureDeviceInternal();
                        if (_captureDevice?.AudioEndpointVolume != null)
                        {
                            _captureDevice.AudioEndpointVolume.Mute = isMuted;
                            success = true;
                        }
                    }
                    catch { }
                }
            }

            if (success)
            {
                MicrophoneMuteChanged?.Invoke(this, isMuted);
            }
        }

        public void InitializeMic()
        {
            lock (_lock)
            {
                InitCaptureDeviceInternal();
            }
        }

        #region IMMNotificationClient Callbacks
        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role deviceRole, string defaultDeviceId)
        {
            if (deviceRole == Role.Multimedia || deviceRole == Role.Console)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(250); // Chờ Windows chuyển đổi endpoint xong

                        float vol = 0f;
                        bool mute = false;
                        bool notifyVol = false;
                        bool micMute = false;
                        bool notifyMic = false;

                        lock (_lock)
                        {
                            if (dataFlow == DataFlow.Render)
                            {
                                InitPlaybackDeviceInternal();
                                vol = GetVolume();
                                mute = IsMuted();
                                notifyVol = true;
                            }
                            else if (dataFlow == DataFlow.Capture)
                            {
                                InitCaptureDeviceInternal();
                                micMute = IsMicrophoneMuted();
                                notifyMic = true;
                            }
                        }

                        if (notifyVol)
                        {
                            VolumeChanged?.Invoke(this, new VolumeChangedEventArgs { Volume = vol, IsMuted = mute });
                        }
                        if (notifyMic)
                        {
                            MicrophoneMuteChanged?.Invoke(this, micMute);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VolumeService] OnDefaultDeviceChanged error: {ex.Message}");
                    }
                });
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        #endregion

        #region Audio Output Device Management & Switching
        public List<AudioDeviceInfo> GetPlaybackDevices()
        {
            var list = new List<AudioDeviceInfo>();
            lock (_lock)
            {
                try
                {
                    _enumerator ??= new MMDeviceEnumerator();
                    string? currentId = _playbackDevice?.ID;
                    var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    foreach (var ep in endpoints)
                    {
                        list.Add(new AudioDeviceInfo
                        {
                            Id = ep.ID,
                            Name = ep.FriendlyName,
                            ShortName = SimplifyAudioDeviceName(ep.FriendlyName),
                            IsDefault = ep.ID.Equals(currentId, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VolumeService] GetPlaybackDevices error: {ex.Message}");
                }
            }
            return list;
        }

        public static string SimplifyAudioDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Loa";

            if (name.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Tai nghe", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Buds", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Headset", StringComparison.OrdinalIgnoreCase))
            {
                return "Tai nghe";
            }

            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Intel(R) Display", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Monitor", StringComparison.OrdinalIgnoreCase))
            {
                return "Màn hình";
            }

            if (name.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Loa", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
            {
                return "Loa";
            }

            int parenIndex = name.IndexOf('(');
            if (parenIndex > 0)
            {
                return name.Substring(0, parenIndex).Trim();
            }
            return name.Length > 10 ? name.Substring(0, 10).Trim() : name;
        }

        public bool SetDefaultPlaybackDevice(string deviceId)
        {
            try
            {
                float volume;
                bool muted;
                lock (_lock)
                {
                    var policyConfig = (IPolicyConfig)new PolicyConfigClient();
                    int hr1 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    int hr2 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    if (hr1 != 0 && hr2 != 0)
                    {
                        return false;
                    }

                    InitPlaybackDeviceInternal();
                    volume = _playbackDevice?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0f;
                    muted = _playbackDevice?.AudioEndpointVolume?.Mute ?? false;
                }

                VolumeChanged?.Invoke(this, new VolumeChangedEventArgs { Volume = volume, IsMuted = muted });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] SetDefaultPlaybackDevice error: {ex.Message}");
            }
            return false;
        }

        public string CyclePlaybackDevice()
        {
            var devices = GetPlaybackDevices();
            if (devices.Count <= 1) return devices.FirstOrDefault()?.ShortName ?? "Loa";

            int currentIndex = devices.FindIndex(d => d.IsDefault);
            int nextIndex = (currentIndex + 1) % devices.Count;
            var nextDevice = devices[nextIndex];
            SetDefaultPlaybackDevice(nextDevice.Id);
            return nextDevice.ShortName;
        }

        public string GetCurrentPlaybackDeviceShortName()
        {
            try
            {
                if (_playbackDevice != null)
                {
                    return SimplifyAudioDeviceName(_playbackDevice.FriendlyName);
                }
            }
            catch { }
            return "Loa";
        }

        public string GetCurrentPlaybackDeviceFullName()
        {
            lock (_lock)
            {
                try
                {
                    if (_playbackDevice == null)
                    {
                        InitPlaybackDeviceInternal();
                    }
                    return _playbackDevice?.FriendlyName ?? "Loa";
                }
                catch
                {
                    return "Loa";
                }
            }
        }

        public static (string ShortName, string IconGlyph) GetAudioDeviceInfo(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ("Loa", "\uE767");

            if (name.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Tai nghe", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Buds", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Headset", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Earphone", StringComparison.OrdinalIgnoreCase))
            {
                return ("Tai nghe", "\uE795");
            }

            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Intel(R) Display", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Monitor", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("LCD", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TV", StringComparison.OrdinalIgnoreCase))
            {
                return ("Màn hình", "\uE7F4");
            }

            return ("Loa", "\uE767");
        }
        #endregion

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            lock (_lock)
            {
                try
                {
                    if (_playbackDevice != null)
                    {
                        try
                        {
                            _playbackDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                        }
                        catch { }
                        _playbackDevice.Dispose();
                        _playbackDevice = null;
                    }

                    if (_captureDevice != null)
                    {
                        _captureDevice.Dispose();
                        _captureDevice = null;
                    }

                    if (_enumerator != null)
                    {
                        try
                        {
                            _enumerator.UnregisterEndpointNotificationCallback(this);
                        }
                        catch { }
                        _enumerator.Dispose();
                        _enumerator = null;
                    }
                }
                catch { }
            }
        }
    }

    #region PolicyConfig COM Interop for Audio Device Switching
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient { }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([In, MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, [In, MarshalAs(UnmanagedType.U4)] ERole eRole);
        [PreserveSig] int SetEndpointVisibility();
    }

    internal enum ERole : uint
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }
    #endregion
}
