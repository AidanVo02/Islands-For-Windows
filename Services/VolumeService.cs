using System;
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

        public void SetVolume(float level)
        {
            float target = Math.Clamp(level, 0f, 1f);
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
            SetVolume(current + delta);
        }

        public void ToggleMute()
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
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
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
}
