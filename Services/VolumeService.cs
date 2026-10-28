using System;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services
{
    public class VolumeChangedEventArgs : EventArgs
    {
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public int VolumePercent => (int)Math.Round(Volume * 100);
    }

    public class VolumeService : IDisposable
    {
        private static readonly Lazy<VolumeService> _lazy = new(() => new VolumeService());
        public static VolumeService Instance => _lazy.Value;

        private IAudioEndpointVolume? _audioEndpointVolume;
        private AudioEndpointVolumeCallback? _callback;
        private IAudioEndpointVolume? _micAudioEndpointVolume;
        private bool _isDisposed;

        public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;
        public event EventHandler<bool>? MicrophoneMuteChanged;

        private VolumeService()
        {
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                // eRender = 0, eMultimedia = 1
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
                if (hr == 0 && device != null)
                {
                    Guid iidIAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
                    // CLSCTX_ALL = 23
                    hr = device.Activate(ref iidIAudioEndpointVolume, 23, IntPtr.Zero, out object volumeObj);
                    if (hr == 0 && volumeObj is IAudioEndpointVolume audioEndpointVolume)
                    {
                        _audioEndpointVolume = audioEndpointVolume;
                        _callback = new AudioEndpointVolumeCallback(this);
                        _audioEndpointVolume.RegisterControlChangeNotify(_callback);
                    }
                }

                InitializeMic();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] Init error: {ex.Message}");
            }
        }

        public void InitializeMic()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                // 1 = eCapture (Microphone), 1 = eMultimedia
                int hr = enumerator.GetDefaultAudioEndpoint(1, 1, out var micDevice);
                if (hr == 0 && micDevice != null)
                {
                    Guid iidIAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
                    hr = micDevice.Activate(ref iidIAudioEndpointVolume, 23, IntPtr.Zero, out object micObj);
                    if (hr == 0 && micObj is IAudioEndpointVolume micVol)
                    {
                        _micAudioEndpointVolume = micVol;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] InitMic error: {ex.Message}");
            }
        }

        public bool IsMicrophoneMuted()
        {
            if (_micAudioEndpointVolume == null) InitializeMic();
            if (_micAudioEndpointVolume == null) return false;
            try
            {
                _micAudioEndpointVolume.GetMute(out bool isMuted);
                return isMuted;
            }
            catch
            {
                return false;
            }
        }

        public bool ToggleMicrophoneMute()
        {
            if (_micAudioEndpointVolume == null) InitializeMic();
            if (_micAudioEndpointVolume == null) return false;
            try
            {
                _micAudioEndpointVolume.GetMute(out bool isMuted);
                bool newMute = !isMuted;
                Guid empty = Guid.Empty;
                _micAudioEndpointVolume.SetMute(newMute, ref empty);
                MicrophoneMuteChanged?.Invoke(this, newMute);
                return newMute;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] ToggleMicrophoneMute error: {ex.Message}");
                return false;
            }
        }

        public void SetMicrophoneMute(bool isMuted)
        {
            if (_micAudioEndpointVolume == null) InitializeMic();
            if (_micAudioEndpointVolume == null) return;
            try
            {
                Guid empty = Guid.Empty;
                _micAudioEndpointVolume.SetMute(isMuted, ref empty);
                MicrophoneMuteChanged?.Invoke(this, isMuted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] SetMicrophoneMute error: {ex.Message}");
            }
        }

        public float GetVolume()
        {
            if (_audioEndpointVolume == null) return 0f;
            try
            {
                _audioEndpointVolume.GetMasterVolumeLevelScalar(out float level);
                return level;
            }
            catch
            {
                return 0f;
            }
        }

        public bool IsMuted()
        {
            if (_audioEndpointVolume == null) return false;
            try
            {
                _audioEndpointVolume.GetMute(out bool isMuted);
                return isMuted;
            }
            catch
            {
                return false;
            }
        }

        public void SetVolume(float level)
        {
            if (_audioEndpointVolume == null) return;
            try
            {
                level = Math.Clamp(level, 0f, 1f);
                Guid empty = Guid.Empty;
                _audioEndpointVolume.SetMasterVolumeLevelScalar(level, ref empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] SetVolume error: {ex.Message}");
            }
        }

        public void AdjustVolume(float delta)
        {
            float current = GetVolume();
            SetVolume(current + delta);
        }

        public void ToggleMute()
        {
            if (_audioEndpointVolume == null) return;
            try
            {
                bool current = IsMuted();
                Guid empty = Guid.Empty;
                _audioEndpointVolume.SetMute(!current, ref empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeService] ToggleMute error: {ex.Message}");
            }
        }

        internal void OnVolumeNotify(float volume, bool isMuted)
        {
            VolumeChanged?.Invoke(this, new VolumeChangedEventArgs
            {
                Volume = volume,
                IsMuted = isMuted
            });
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (_audioEndpointVolume != null && _callback != null)
                {
                    _audioEndpointVolume.UnregisterControlChangeNotify(_callback);
                }
            }
            catch {}
        }

        #region COM Interfaces & Classes
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig]
            int GetDevice(string pwstrId, out IMMDevice device);
            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            [PreserveSig]
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            [PreserveSig]
            int GetState(out int pdwState);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig]
            int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
            [PreserveSig]
            int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
            [PreserveSig]
            int GetChannelCount(out uint pnChannelCount);
            [PreserveSig]
            int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig]
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            [PreserveSig]
            int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig]
            int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig]
            int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig]
            int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            [PreserveSig]
            int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig]
            int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            [PreserveSig]
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
            [PreserveSig]
            int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            [PreserveSig]
            int VolumeStepUp(ref Guid pguidEventContext);
            [PreserveSig]
            int VolumeStepDown(ref Guid pguidEventContext);
            [PreserveSig]
            int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            [PreserveSig]
            int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
        }

        [Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolumeCallback
        {
            [PreserveSig]
            int OnNotify(IntPtr pNotifyData);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIO_VOLUME_NOTIFICATION_DATA
        {
            public Guid guidEventContext;
            public int bMuted;
            public float fMasterVolume;
            public uint nChannels;
        }

        [ComVisible(true)]
        private class AudioEndpointVolumeCallback : IAudioEndpointVolumeCallback
        {
            private readonly VolumeService _owner;

            public AudioEndpointVolumeCallback(VolumeService owner)
            {
                _owner = owner;
            }

            public int OnNotify(IntPtr pNotifyData)
            {
                if (pNotifyData != IntPtr.Zero)
                {
                    try
                    {
                        var data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotifyData);
                        _owner.OnVolumeNotify(data.fMasterVolume, data.bMuted != 0);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VolumeService] OnNotify error: {ex.Message}");
                    }
                }
                return 0; // S_OK
            }
        }
        #endregion
    }
}
