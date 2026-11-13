using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland.Services
{
    /// <summary>
    /// Dịch vụ phân tích phổ âm thanh thời gian thực (Real-time Audio Visualizer)
    /// Sử dụng NAudio WasapiLoopbackCapture để bắt âm thanh hệ thống và phân tích FFT cho 4 cột sóng.
    /// Thiết kế Zero-GC Allocation trong vòng lặp âm thanh và Attack/Gravity Release smoothing.
    /// </summary>
    public class AudioVisualizerService : IDisposable, IMMNotificationClient
    {
        private static readonly Lazy<AudioVisualizerService> _lazy = new(() => new AudioVisualizerService());
        public static AudioVisualizerService Instance => _lazy.Value;

        private const int FFT_SIZE = 1024;
        private const int FFT_M = 10; // 2^10 = 1024

        // Hệ số khuếch đại theo chuẩn phòng thu cho 4 dải tần
        private const float BOOST_BASS = 1.0f * 12.0f;
        private const float BOOST_LOW_MID = 1.4f * 14.0f;
        private const float BOOST_MID_VOCAL = 1.8f * 18.0f;
        private const float BOOST_TREBLE = 2.8f * 26.0f;

        // Bộ đệm cố định cấp phát 1 lần duy nhất (Zero-GC Allocation)
        private readonly float[] _sampleRingBuffer = new float[FFT_SIZE];
        private int _sampleRingIndex = 0;
        private readonly float[] _hannWindow = new float[FFT_SIZE];
        private readonly Complex[] _fftBuffer = new Complex[FFT_SIZE];

        // Dữ liệu mục tiêu dạng volatile (Lock-Free giữa Audio Thread và UI Thread)
        private volatile float _targetB1 = 0f;
        private volatile float _targetB2 = 0f;
        private volatile float _targetB3 = 0f;
        private volatile float _targetB4 = 0f;
        private readonly float[] _smoothedBars = new float[4];
        private readonly object _audioLock = new();

        private WasapiLoopbackCapture? _capture;
        private MMDeviceEnumerator? _deviceEnumerator;
        private readonly DispatcherTimer _uiTimer;

        private DateTime _lastAudioActivity = DateTime.UtcNow;
        private DateTime _lastBeatTime = DateTime.MinValue;
        private bool _isCapturing = false;
        private bool _isEnabled = true;
        private bool _isDisposed = false;

        /// <summary>
        /// Bắn ra giá trị của 4 cột sóng nhạc đã được làm mượt trong khoảng [0.0 -> 1.0]
        /// </summary>
        public event Action<float, float, float, float>? WaveformUpdated;

        /// <summary>
        /// Kích hoạt khi có cú đập trống Kick cực mạnh ở dải trầm (Sub-bass > 0.85)
        /// </summary>
        public event Action? BeatDetected;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    if (!_isEnabled)
                    {
                        ResetBars();
                    }
                }
            }
        }

        private AudioVisualizerService()
        {
            // 1. Khởi tạo sẵn bảng Hann Window để giảm rò rỉ phổ tần số (Spectral Leakage)
            for (int i = 0; i < FFT_SIZE; i++)
            {
                _hannWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FFT_SIZE - 1))));
            }

            // 2. Timer cập nhật UI ở tần số 40 FPS (chu kỳ 25ms)
            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            _uiTimer.Tick += UiTimer_Tick;

            // 3. Đăng ký theo dõi thiết bị âm thanh thay đổi (rút tai nghe, cắm loa, đổi output)
            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _deviceEnumerator.RegisterEndpointNotificationCallback(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to register MMDevice notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Bắt đầu lắng nghe âm thanh và chạy vòng lặp UI
        /// </summary>
        public void Start()
        {
            if (!_isEnabled || _isDisposed) return;

            if (!_uiTimer.IsEnabled)
            {
                _uiTimer.Start();
            }

            if (!_isCapturing)
            {
                InitCapture();
            }
        }

        /// <summary>
        /// Dừng bắt âm thanh khi người dùng tạm dừng nhạc hoặc chuyển chế độ
        /// </summary>
        public void Stop()
        {
            try
            {
                _uiTimer.Stop();
                StopCaptureInternal();
                ResetBars();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioVisualizer Stop Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Khởi tạo lại WasapiLoopbackCapture khi người dùng đổi thiết bị âm thanh hoặc cắm tai nghe
        /// </summary>
        public void RestartCapture()
        {
            lock (_audioLock)
            {
                try
                {
                    StopCaptureInternal();
                    InitCapture();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"RestartCapture Error: {ex.Message}");
                }
            }
        }

        private void InitCapture()
        {
            try
            {
                _capture = new WasapiLoopbackCapture();
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                _isCapturing = true;
                _lastAudioActivity = DateTime.UtcNow;
            }
            catch (COMException comEx)
            {
                System.Diagnostics.Debug.WriteLine($"Init WasapiLoopbackCapture COMException: {comEx.Message} (0x{comEx.ErrorCode:X8})");
                _isCapturing = false;
                try { _capture?.Dispose(); } catch { }
                _capture = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Init WasapiLoopbackCapture failed: {ex.Message}");
                _isCapturing = false;
                try { _capture?.Dispose(); } catch { }
                _capture = null;
            }
        }

        private void StopCaptureInternal()
        {
            if (_capture != null)
            {
                try
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.StopRecording();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"StopCapture error: {ex.Message}");
                }
                finally
                {
                    try { _capture.Dispose(); } catch { }
                    _capture = null;
                    _isCapturing = false;
                }
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _isCapturing = false;
            if (e.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"RecordingStopped with exception: {e.Exception.Message}");
                // Khi thiết bị âm thanh bị rút hoặc invalid (AUDCLNT_E_DEVICE_INVALIDATED),
                // tự động lên lịch phục hồi sau 1.5 giây mà không làm sập ứng dụng
                if (_isEnabled && !_isDisposed)
                {
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(1500);
                        if (_isEnabled && !_isDisposed && !_isCapturing)
                        {
                            RestartCapture();
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Xử lý buffer âm thanh thực tế từ hệ thống.
        /// Tối ưu 100% Zero-GC Allocation: Không dùng từ khóa 'new' trong phương thức này.
        /// Bọc try-catch an toàn để phòng tránh ngoại lệ bất ngờ từ thiết bị phần cứng.
        /// </summary>
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0 || !_isEnabled) return;

            try
            {
                var format = _capture?.WaveFormat;
                if (format == null) return;

                int channels = format.Channels;
                int sampleRate = format.SampleRate;
                if (channels <= 0 || sampleRate <= 0) return;

                // Xử lý chuẩn IEEE Float 32-bit (Phổ biến nhất trên Windows 10/11 WASAPI Loopback)
                if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                {
                    ReadOnlySpan<float> floatSpan = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                    int frameCount = floatSpan.Length / channels;

                    for (int i = 0; i < frameCount; i++)
                    {
                        float mono = 0f;
                        int baseIndex = i * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            mono += floatSpan[baseIndex + c];
                        }
                        mono /= channels;

                        _sampleRingBuffer[_sampleRingIndex++] = mono;
                        if (_sampleRingIndex >= FFT_SIZE)
                        {
                            _sampleRingIndex = 0;
                            ProcessFft(sampleRate);
                        }
                    }
                }
                // Dự phòng cho trường hợp thiết bị báo định dạng 16-bit PCM
                else if (format.BitsPerSample == 16)
                {
                    ReadOnlySpan<short> shortSpan = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                    int frameCount = shortSpan.Length / channels;

                    for (int i = 0; i < frameCount; i++)
                    {
                        float mono = 0f;
                        int baseIndex = i * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            mono += shortSpan[baseIndex + c] / 32768.0f;
                        }
                        mono /= channels;

                        _sampleRingBuffer[_sampleRingIndex++] = mono;
                        if (_sampleRingIndex >= FFT_SIZE)
                        {
                            _sampleRingIndex = 0;
                            ProcessFft(sampleRate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnDataAvailable error: {ex.Message}");
            }
        }

        /// <summary>
        /// Phân tích FFT và chia 4 dải tần số bất đối xứng (Asymmetric Frequency Mapping)
        /// Ghi vào các biến volatile (Lock-Free) giúp Audio Thread không bao giờ bị nghẽn
        /// </summary>
        private void ProcessFft(int sampleRate)
        {
            // 1. Nhân mẫu âm thanh với Hann Window và đưa vào buffer phức
            for (int i = 0; i < FFT_SIZE; i++)
            {
                _fftBuffer[i].X = _sampleRingBuffer[i] * _hannWindow[i];
                _fftBuffer[i].Y = 0f;
            }

            // 2. Chạy thuật toán Fast Fourier Transform tại chỗ (In-Place)
            FastFourierTransform.FFT(true, FFT_M, _fftBuffer);

            // 3. Tính toán dải tần số:
            // Độ phân giải mỗi bin: binWidth = sampleRate / 1024 (Ví dụ: 48000 / 1024 = 46.875 Hz)
            float binWidth = (float)sampleRate / FFT_SIZE;
            int maxValidBin = (FFT_SIZE / 2) - 1;

            int BinOf(float hz) => Math.Clamp((int)(hz / binWidth), 1, maxValidBin);

            // Cột 1: 20Hz – 120Hz (Sub-Bass & Kick Drum)
            float b1 = CalculateBandMagnitude(BinOf(20), BinOf(120), BOOST_BASS);

            // Cột 2: 120Hz – 450Hz (Low-Mid & Bassline)
            float b2 = CalculateBandMagnitude(BinOf(120), BinOf(450), BOOST_LOW_MID);

            // Cột 3: 450Hz – 2500Hz (Vocal & Lead Instrument)
            float b3 = CalculateBandMagnitude(BinOf(450), BinOf(2500), BOOST_MID_VOCAL);

            // Cột 4: 2500Hz – 10000Hz (Hi-Hat, Snare & Presence)
            float b4 = CalculateBandMagnitude(BinOf(2500), BinOf(10000), BOOST_TREBLE);

            _targetB1 = b1;
            _targetB2 = b2;
            _targetB3 = b3;
            _targetB4 = b4;

            // Nếu có tín hiệu âm thanh phát ra
            if (b1 > 0.01f || b2 > 0.01f || b3 > 0.01f || b4 > 0.01f)
            {
                _lastAudioActivity = DateTime.UtcNow;
            }
        }

        private float CalculateBandMagnitude(int startBin, int endBin, float boost)
        {
            if (endBin < startBin) endBin = startBin;
            int count = endBin - startBin + 1;
            if (count <= 0) return 0f;

            float sum = 0f;
            for (int k = startBin; k <= endBin; k++)
            {
                float mag = MathF.Sqrt((_fftBuffer[k].X * _fftBuffer[k].X) + (_fftBuffer[k].Y * _fftBuffer[k].Y));
                sum += mag;
            }

            float avg = sum / count;
            // Áp dụng đường cong phi tuyến (Power curve) để sóng nhấp nhô sống động
            float boosted = avg * boost;
            float normalized = MathF.Pow(Math.Clamp(boosted, 0f, 1.4f), 0.72f);
            return Math.Clamp(normalized, 0f, 1.0f);
        }

        /// <summary>
        /// Vòng lặp vật lý làm mượt Attack & Gravity Decay chạy trên UI Dispatcher
        /// Đọc các biến volatile không cần lock để tối đa hóa hiệu năng
        /// </summary>
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            float target1, target2, target3, target4;

            // Kiểm tra trạng thái im lặng (Silence / Idle Detection):
            // Nếu không có âm thanh sau 3 giây, kéo mục tiêu về 0
            if ((DateTime.UtcNow - _lastAudioActivity).TotalSeconds > 3.0)
            {
                target1 = target2 = target3 = target4 = 0f;
            }
            else
            {
                target1 = _targetB1;
                target2 = _targetB2;
                target3 = _targetB3;
                target4 = _targetB4;
            }

            // Vật lý chuyển động:
            // Fast Attack (0.65): Khi có tiếng trống đập, vọt lên tức thì
            // Slow Gravity Decay (0.18): Khi âm thanh dứt, rơi xuống êm ái
            SmoothValue(ref _smoothedBars[0], target1);
            SmoothValue(ref _smoothedBars[1], target2);
            SmoothValue(ref _smoothedBars[2], target3);
            SmoothValue(ref _smoothedBars[3], target4);

            // Hiệu ứng "Đập nhẹ" (Beat Pulse): Khi tiếng trống Kick (Cột 1) vượt ngưỡng 0.85
            if (target1 > 0.85f || _smoothedBars[0] > 0.82f)
            {
                if ((DateTime.UtcNow - _lastBeatTime).TotalMilliseconds > 180)
                {
                    _lastBeatTime = DateTime.UtcNow;
                    BeatDetected?.Invoke();
                }
            }

            WaveformUpdated?.Invoke(_smoothedBars[0], _smoothedBars[1], _smoothedBars[2], _smoothedBars[3]);
        }

        private static void SmoothValue(ref float current, float target)
        {
            if (target > current)
            {
                current += (target - current) * 0.65f;
            }
            else
            {
                current += (target - current) * 0.18f;
            }

            if (current < 0.001f) current = 0f;
        }

        private void ResetBars()
        {
            _targetB1 = 0f;
            _targetB2 = 0f;
            _targetB3 = 0f;
            _targetB4 = 0f;

            _smoothedBars[0] = 0f;
            _smoothedBars[1] = 0f;
            _smoothedBars[2] = 0f;
            _smoothedBars[3] = 0f;

            WaveformUpdated?.Invoke(0f, 0f, 0f, 0f);
        }

        #region IMMNotificationClient (Tự động phát hiện khi đổi tai nghe / loa ngoài)

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Khi thiết bị phát âm thanh mặc định thay đổi, khởi động lại capture trên Background Task
            if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
            {
                System.Diagnostics.Debug.WriteLine("Default audio render device changed. Restarting capture asynchronously...");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(350); // Chờ Windows định tuyến xong
                        if (_isEnabled && !_isDisposed)
                        {
                            RestartCapture();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"AudioVisualizer OnDefaultDeviceChanged error: {ex.Message}");
                    }
                });
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string pwstrDeviceId) { }
        public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        #endregion

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Stop();

            if (_deviceEnumerator != null)
            {
                try
                {
                    _deviceEnumerator.UnregisterEndpointNotificationCallback(this);
                    _deviceEnumerator.Dispose();
                }
                catch { }
                _deviceEnumerator = null;
            }
        }
    }
}

