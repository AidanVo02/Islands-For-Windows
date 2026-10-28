using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;
using Point = System.Windows.Point;
using DynamicIsland.Services;

namespace DynamicIsland
{
    public enum IslandMode
    {
        Idle,
        MediaCompact,
        Expanded,
        ExpandedMedia,
        FileDropHover,
        FileTransferring,
        FileResult,
        SliderHud,
        LockKeyHud
    }

    public partial class MainWindow : Window
    {
        private readonly MediaService _mediaService = new();
        private readonly BatteryService _batteryService = new();
        private readonly DispatcherTimer _clockTimer = new();
        private readonly DispatcherTimer _equalizerTimer = new();
        private readonly Random _random = new();

        private IslandMode _currentMode = IslandMode.Idle;
        private bool _isMediaActive = false;

        // Quản lý HUD & Control Center Âm lượng / Độ sáng
        private readonly DispatcherTimer _hudAutoDismissTimer = new();
        private readonly DispatcherTimer _lockKeyDismissTimer = new();
        private bool _isAdjustingBrightness = false;
        private bool _isUpdatingSliders = false;

        // Quản lý truyền file LocalSend
        private bool _isFileTransferInProgress = false;
        private DispatcherTimer? _fileResultTimer;

        // Quản lý đa màn hình
        private Screen[] _screens = Array.Empty<Screen>();
        private int _currentScreenIndex = 0;

        // Quản lý kéo thả / click
        private Point _startPoint;
        private bool _isDragging = false;

        // Quản lý Tự động ẩn (Auto-Hide)
        private bool _isIslandHidden = false;
        private readonly DispatcherTimer _autoHideTimer = new();
        private readonly DispatcherTimer _cursorCheckTimer = new();

        public MainWindow()
        {
            _isUpdatingSliders = true;
            InitializeComponent();
            _isUpdatingSliders = false;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Ẩn khỏi Alt+Tab
                Win32Helper.SetToolWindow(this);

                // Khởi tạo đa màn hình
                _screens = Screen.AllScreens;
                var settings = SettingsService.Instance.Current;

                if (settings.MonitorIndex >= 0 && settings.MonitorIndex < _screens.Length)
                {
                    _currentScreenIndex = settings.MonitorIndex;
                }
                else
                {
                    int initialIndex = 0;
                    long maxArea = 0;
                    for (int i = 0; i < _screens.Length; i++)
                    {
                        long area = (long)_screens[i].Bounds.Width * _screens[i].Bounds.Height;
                        if (area > maxArea)
                        {
                            maxArea = area;
                            initialIndex = i;
                        }
                    }
                    _currentScreenIndex = initialIndex;
                    settings.MonitorIndex = initialIndex;
                    SettingsService.Instance.Save();
                }

                // Đăng ký nhận sự kiện thay đổi Settings tức thì
                SettingsService.Instance.SettingsChanged += SettingsService_SettingsChanged;
                ApplySettings(settings);

                // Khởi động đồng hồ
                _clockTimer.Interval = TimeSpan.FromSeconds(1);
                _clockTimer.Tick += (s, args) => UpdateClock();
                _clockTimer.Start();
                UpdateClock();

                // Khởi động hoạt ảnh sóng nhạc (Equalizer)
                _equalizerTimer.Interval = TimeSpan.FromMilliseconds(100);
                _equalizerTimer.Tick += EqualizerTimer_Tick;

                // Cấu hình timer Tự động ẩn (Auto-Hide)
                _autoHideTimer.Tick += AutoHideTimer_Tick;
                _cursorCheckTimer.Interval = TimeSpan.FromMilliseconds(50);
                _cursorCheckTimer.Tick += CursorCheckTimer_Tick;

                // Khởi động dịch vụ Pin
                _batteryService.BatteryChanged += BatteryService_BatteryChanged;
                _batteryService.Initialize();

                // Khởi động dịch vụ Bắt nhạc Windows
                _mediaService.MediaTrackChanged += MediaService_MediaTrackChanged;
                _mediaService.PlaybackStateChanged += MediaService_PlaybackStateChanged;
                _mediaService.TimelineChanged += MediaService_TimelineChanged;
                await _mediaService.InitializeAsync();

                // Khởi động dịch vụ HUD Âm lượng & Độ sáng
                _hudAutoDismissTimer.Tick += HudAutoDismissTimer_Tick;
                VolumeService.Instance.VolumeChanged += VolumeService_VolumeChanged;
                BrightnessService.Instance.BrightnessChanged += BrightnessService_BrightnessChanged;

                // Khởi động dịch vụ Quyền riêng tư (Privacy Indicators)
                PrivacyService.Instance.PrivacyStatusChanged += PrivacyService_PrivacyStatusChanged;
                PrivacyService.Instance.Start();

                // Khởi động dịch vụ Phím khóa & Bộ gõ (Lock Key & Input Method)
                _lockKeyDismissTimer.Tick += LockKeyDismissTimer_Tick;
                LockKeyService.Instance.LockKeyChanged += LockKeyService_LockKeyChanged;
                LockKeyService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Window_Loaded error: {ex}");
            }
        }

        private void SettingsService_SettingsChanged(IslandSettings settings)
        {
            Dispatcher.Invoke(() =>
            {
                ApplySettings(settings);
            });
        }

        private void ApplySettings(IslandSettings s)
        {
            _screens = Screen.AllScreens;
            if (s.MonitorIndex >= 0 && s.MonitorIndex < _screens.Length)
            {
                _currentScreenIndex = s.MonitorIndex;
            }

            PositionOnCurrentScreen();

            // Áp dụng màu sắc & bóng đổ
            try
            {
                var bg = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s.ThemeBackground);
                var border = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s.BorderColor);
                var bgBrush = new SolidColorBrush(bg);
                var borderBrush = new SolidColorBrush(border);

                IslandContainer.Background = bgBrush;
                IslandContainer.BorderBrush = borderBrush;

                if (IslandShadow != null)
                {
                    IslandShadow.Background = bgBrush;
                    IslandShadow.Visibility = s.EnableShadow ? Visibility.Visible : Visibility.Collapsed;
                    if (IslandDropShadow != null)
                    {
                        IslandDropShadow.Opacity = s.ShadowOpacity;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying color settings: {ex.Message}");
            }

            // Áp dụng hiển thị Widgets
            if (ClockPanel != null)
            {
                ClockPanel.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;
            }
            if (BatteryPanel != null)
            {
                BatteryPanel.Visibility = s.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateClock();

            Topmost = s.AlwaysOnTop;

            if (MenuAutoHide != null)
            {
                MenuAutoHide.IsChecked = s.AutoHide;
            }

            if (s.AutoHide)
            {
                _cursorCheckTimer.Start();
                if (!IslandContainer.IsMouseOver && _currentMode != IslandMode.Expanded && !IslandContextMenu.IsOpen)
                {
                    StartAutoHideTimer();
                }
            }
            else
            {
                _cursorCheckTimer.Stop();
                _autoHideTimer.Stop();
                ShowIsland(instant: true);
            }

            // Cập nhật lại kích thước hiện tại
            SwitchMode(_currentMode);
        }

        #region Multi-Monitor Management
        private void PositionOnCurrentScreen()
        {
            if (_screens.Length == 0) return;
            if (_currentScreenIndex < 0 || _currentScreenIndex >= _screens.Length)
            {
                _currentScreenIndex = 0;
            }

            var screen = _screens[_currentScreenIndex];
            var s = SettingsService.Instance.Current;
            Win32Helper.PositionOnScreen(this, screen, s.TopOffset, s.HorizontalOffset, s.AlwaysOnTop);
        }

        public void SwitchToScreen(int index)
        {
            _screens = Screen.AllScreens;
            if (_screens.Length == 0) return;

            _currentScreenIndex = (index + _screens.Length) % _screens.Length;
            SettingsService.Instance.Update(s => s.MonitorIndex = _currentScreenIndex);
            PositionOnCurrentScreen();
        }

        public void SwitchToNextScreen()
        {
            SwitchToScreen(_currentScreenIndex + 1);
        }

        private void IslandContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            _screens = Screen.AllScreens;
            MenuMonitors.Items.Clear();
            MenuAutoHide.IsChecked = SettingsService.Instance.Current.AutoHide;

            for (int i = 0; i < _screens.Length; i++)
            {
                var screen = _screens[i];
                int screenIdx = i;
                string label = $"Màn hình {i + 1} ({screen.Bounds.Width}x{screen.Bounds.Height})";
                if (screen.Primary) label += " [Chính]";

                var item = new System.Windows.Controls.MenuItem
                {
                    Header = label,
                    IsChecked = (i == _currentScreenIndex)
                };

                item.Click += (s, args) => SwitchToScreen(screenIdx);
                MenuMonitors.Items.Add(item);
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Phím tắt F9 để chuyển màn hình tức thì
            if (e.Key == Key.F9)
            {
                SwitchToNextScreen();
            }
            // Phím tắt Ctrl + , để mở cài đặt
            else if (e.Key == Key.OemComma && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SettingsWindow.ShowOrActivate();
            }
        }
        #endregion

        private void UpdateClock()
        {
            var s = SettingsService.Instance.Current;
            string format = s.Use24HourClock ? "HH:mm" : "hh:mm tt";
            TxtClock.Text = DateTime.Now.ToString(format);
            if (TxtExpClock != null)
            {
                TxtExpClock.Text = DateTime.Now.ToString(format);
            }
        }

        #region Battery Service Callback
        private void BatteryService_BatteryChanged(BatteryInfo info)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    SolidColorBrush statusBrush;
                    SolidColorBrush badgeBgBrush;
                    SolidColorBrush badgeBorderBrush;

                    if (!info.HasBattery)
                    {
                        TxtBatteryPercent.Text = "AC";
                        BatteryFill.Width = 14;
                        statusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59)); // Green #34C759
                        badgeBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x2A, 0x1C));
                        badgeBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x58, 0x32));

                        BatteryFill.Fill = statusBrush;
                        if (TxtExpBatteryText != null)
                        {
                            TxtExpBatteryText.Text = "AC Nguồn";
                            TxtExpBatteryText.Foreground = statusBrush;
                        }
                        if (TxtExpBatteryIcon != null)
                        {
                            TxtExpBatteryIcon.Text = "\uE83E"; // Plug
                            TxtExpBatteryIcon.Foreground = statusBrush;
                        }
                        if (ExpBatteryBadge != null)
                        {
                            ExpBatteryBadge.Background = badgeBgBrush;
                            ExpBatteryBadge.BorderBrush = badgeBorderBrush;
                        }
                        return;
                    }

                    int pct = Math.Clamp(info.Percentage, 0, 100);
                    TxtBatteryPercent.Text = $"{pct}%";
                    BatteryFill.Width = Math.Max(2, (pct / 100.0) * 14.0);

                    // Màu sắc đặc trưng: Đầy/bình thường xanh lá (#34C759), yếu vàng (#FFCC00), cạn đỏ (#FF3B30)
                    if (info.IsCharging)
                    {
                        statusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59)); // Green
                        badgeBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x2A, 0x1C));
                        badgeBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x58, 0x32));
                    }
                    else if (pct <= 10)
                    {
                        statusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30)); // Alert Red
                        badgeBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x15, 0x15));
                        badgeBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x63, 0x23, 0x23));
                    }
                    else if (pct <= 20)
                    {
                        statusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xCC, 0x00)); // Low Power Amber
                        badgeBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x24, 0x12));
                        badgeBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5C, 0x4D, 0x1E));
                    }
                    else
                    {
                        statusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59)); // Characteristic Green
                        badgeBgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x2A, 0x1C));
                        badgeBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x58, 0x32));
                    }

                    BatteryFill.Fill = statusBrush;
                    if (pct <= 20 && !info.IsCharging)
                    {
                        TxtBatteryPercent.Foreground = statusBrush;
                    }
                    else
                    {
                        TxtBatteryPercent.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
                    }

                    if (TxtExpBatteryText != null)
                    {
                        TxtExpBatteryText.Text = info.IsCharging ? $"Sạc {pct}%" : $"{pct}%";
                        TxtExpBatteryText.Foreground = statusBrush;
                    }

                    if (TxtExpBatteryIcon != null)
                    {
                        TxtExpBatteryIcon.Foreground = statusBrush;
                        if (info.IsCharging)
                        {
                            TxtExpBatteryIcon.Text = "\uE862"; // BatteryCharging
                        }
                        else if (pct >= 85)
                        {
                            TxtExpBatteryIcon.Text = "\uE860"; // Battery10
                        }
                        else if (pct >= 60)
                        {
                            TxtExpBatteryIcon.Text = "\uE857"; // Battery7
                        }
                        else if (pct >= 35)
                        {
                            TxtExpBatteryIcon.Text = "\uE854"; // Battery4
                        }
                        else if (pct >= 15)
                        {
                            TxtExpBatteryIcon.Text = "\uE852"; // Battery2
                        }
                        else
                        {
                            TxtExpBatteryIcon.Text = "\uE850"; // Battery0
                        }
                    }

                    if (ExpBatteryBadge != null)
                    {
                        ExpBatteryBadge.Background = badgeBgBrush;
                        ExpBatteryBadge.BorderBrush = badgeBorderBrush;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"BatteryService error: {ex.Message}");
                }
            });
        }
        #endregion

        #region Media Service Callbacks
        private void MediaService_MediaTrackChanged(MediaTrackInfo track)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var s = SettingsService.Instance.Current;
                    _isMediaActive = !string.IsNullOrWhiteSpace(track.Title) && track.Title != "No Media Playing";

                    TxtMiniTitle.Text = track.Title ?? "";
                    ImgMiniThumb.Source = track.Thumbnail;

                    TxtLargeTitle.Text = track.Title ?? "";
                    TxtLargeArtist.Text = track.Artist ?? "";
                    ImgLargeThumb.Source = track.Thumbnail;

                    string source = "Media";
                    string appId = (track.SourceAppId ?? string.Empty).ToLower();
                    if (appId.Contains("spotify")) source = "Spotify";
                    else if (appId.Contains("chrome")) source = "Chrome";
                    else if (appId.Contains("edge") || appId.Contains("msedge")) source = "Edge";
                    else if (appId.Contains("apple")) source = "Apple Music";
                    TxtAppSource.Text = source;

                    UpdatePlaybackIcon(track.IsPlaying);

                    if (_isMediaActive && track.IsPlaying)
                    {
                        if (s.EqualizerEnabled)
                        {
                            _equalizerTimer.Start();
                        }
                        if (_currentMode == IslandMode.Idle && s.AutoSwitchMedia)
                        {
                            SwitchMode(IslandMode.MediaCompact);
                        }
                    }
                    else if (!_isMediaActive)
                    {
                        _equalizerTimer.Stop();
                        if (_currentMode != IslandMode.Expanded)
                        {
                            SwitchMode(IslandMode.Idle);
                        }
                    }

                    if (BtnSwitchToMedia != null)
                    {
                        BtnSwitchToMedia.Visibility = _isMediaActive ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediaTrackChanged error: {ex.Message}");
                }
            });
        }

        private void MediaService_PlaybackStateChanged(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var s = SettingsService.Instance.Current;
                    UpdatePlaybackIcon(isPlaying);
                    if (isPlaying)
                    {
                        if (s.EqualizerEnabled)
                        {
                            _equalizerTimer.Start();
                        }
                        if (_currentMode == IslandMode.Idle && _isMediaActive && s.AutoSwitchMedia)
                        {
                            SwitchMode(IslandMode.MediaCompact);
                        }
                    }
                    else
                    {
                        _equalizerTimer.Stop();
                        ResetEqualizerBars();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"PlaybackStateChanged error: {ex.Message}");
                }
            });
        }

        private void MediaService_TimelineChanged(TimeSpan position, TimeSpan duration)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    TxtElapsed.Text = FormatTime(position);
                    TxtDuration.Text = FormatTime(duration);

                    if (duration.TotalSeconds > 0)
                    {
                        MediaProgressBar.Value = (position.TotalSeconds / duration.TotalSeconds) * 100.0;
                    }
                    else
                    {
                        MediaProgressBar.Value = 0;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TimelineChanged error: {ex.Message}");
                }
            });
        }

        private string FormatTime(TimeSpan time)
        {
            return time.Hours > 0 
                ? time.ToString(@"h\:mm\:ss") 
                : time.ToString(@"m\:ss");
        }

        private void UpdatePlaybackIcon(bool isPlaying)
        {
            TxtPlayPauseIcon.Text = isPlaying ? "\uE769" : "\uE768"; // Pause vs Play
        }

        private void EqualizerTimer_Tick(object? sender, EventArgs e)
        {
            if (!SettingsService.Instance.Current.EqualizerEnabled)
            {
                ResetEqualizerBars();
                return;
            }

            WaveBar1.Height = _random.Next(4, 15);
            WaveBar2.Height = _random.Next(6, 17);
            WaveBar3.Height = _random.Next(4, 13);
        }

        private void ResetEqualizerBars()
        {
            WaveBar1.Height = 4;
            WaveBar2.Height = 4;
            WaveBar3.Height = 4;
        }
        #endregion

        #region Morphing & State Transitions
        public void SwitchMode(IslandMode newMode)
        {
            _currentMode = newMode;
            var s = SettingsService.Instance.Current;

            double targetWidth;
            double targetHeight;
            double targetRadius;

            // Ẩn tất cả view trước khi hiển thị view của mode tương ứng
            CompactIdleView.Visibility = Visibility.Collapsed;
            CompactMediaView.Visibility = Visibility.Collapsed;
            ExpandedControlCenterView.Visibility = Visibility.Collapsed;
            ExpandedMediaView.Visibility = Visibility.Collapsed;
            FileDropView.Visibility = Visibility.Collapsed;
            FileTransferView.Visibility = Visibility.Collapsed;
            FileResultView.Visibility = Visibility.Collapsed;
            SliderHudView.Visibility = Visibility.Collapsed;
            if (LockKeyHudView != null) LockKeyHudView.Visibility = Visibility.Collapsed;

            switch (newMode)
            {
                case IslandMode.Idle:
                    targetWidth = s.IdleWidth;
                    targetHeight = s.IdleHeight;
                    targetRadius = s.CornerRadius;
                    CompactIdleView.Visibility = Visibility.Visible;
                    break;

                case IslandMode.MediaCompact:
                    targetWidth = Math.Max(s.IdleWidth + 80, 270);
                    targetHeight = s.IdleHeight;
                    targetRadius = s.CornerRadius;
                    CompactMediaView.Visibility = Visibility.Visible;
                    break;

                case IslandMode.Expanded:
                    targetWidth = Math.Max(s.ExpandedWidth, 380);
                    targetHeight = 175;
                    targetRadius = 26;
                    ExpandedControlCenterView.Visibility = Visibility.Visible;
                    UpdateControlCenterContent();
                    break;

                case IslandMode.ExpandedMedia:
                    targetWidth = s.ExpandedWidth;
                    targetHeight = s.ExpandedHeight;
                    targetRadius = s.CornerRadius + 10;
                    ExpandedMediaView.Visibility = Visibility.Visible;
                    break;

                case IslandMode.FileDropHover:
                    targetWidth = 320;
                    targetHeight = 52;
                    targetRadius = 24;
                    FileDropView.Visibility = Visibility.Visible;
                    break;

                case IslandMode.FileTransferring:
                    targetWidth = 330;
                    targetHeight = 54;
                    targetRadius = 24;
                    FileTransferView.Visibility = Visibility.Visible;
                    break;

                case IslandMode.FileResult:
                    targetWidth = 290;
                    targetHeight = 42;
                    targetRadius = 21;
                    FileResultView.Visibility = Visibility.Visible;
                    break;

                case IslandMode.SliderHud:
                    targetWidth = 270;
                    targetHeight = 38;
                    targetRadius = 19;
                    SliderHudView.Visibility = Visibility.Visible;
                    break;

                case IslandMode.LockKeyHud:
                    targetWidth = 235;
                    targetHeight = 38;
                    targetRadius = 19;
                    if (LockKeyHudView != null) LockKeyHudView.Visibility = Visibility.Visible;
                    break;

                default:
                    targetWidth = s.IdleWidth;
                    targetHeight = s.IdleHeight;
                    targetRadius = s.CornerRadius;
                    CompactIdleView.Visibility = Visibility.Visible;
                    break;
            }

            AnimateIsland(targetWidth, targetHeight, targetRadius);
        }

        private void AnimateIsland(double width, double height, double cornerRadius)
        {
            var duration = TimeSpan.FromMilliseconds(320);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var animWidth = new DoubleAnimation(IslandContainer.Width, width, duration) { EasingFunction = easing };
            var animHeight = new DoubleAnimation(IslandContainer.Height, height, duration) { EasingFunction = easing };

            IslandContainer.BeginAnimation(WidthProperty, animWidth);
            IslandContainer.BeginAnimation(HeightProperty, animHeight);
            IslandContainer.CornerRadius = new CornerRadius(cornerRadius);

            if (IslandShadow != null)
            {
                IslandShadow.BeginAnimation(WidthProperty, animWidth);
                IslandShadow.BeginAnimation(HeightProperty, animHeight);
                IslandShadow.CornerRadius = new CornerRadius(cornerRadius);
            }
        }
        #endregion

        #region Mouse Interactions (Natural Drag & Click)
        private void TopEdgeTrigger_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isIslandHidden)
            {
                ShowIsland();
            }
            _autoHideTimer.Stop();
        }

        private void IslandContainer_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _autoHideTimer.Stop();
            if (_isIslandHidden)
            {
                ShowIsland();
            }

            if (_isFileTransferInProgress || 
                _currentMode == IslandMode.FileDropHover || 
                _currentMode == IslandMode.FileTransferring || 
                _currentMode == IslandMode.FileResult ||
                _currentMode == IslandMode.SliderHud)
            {
                return;
            }

            var s = SettingsService.Instance.Current;
            if (!s.HoverAnimation) return;

            if (_currentMode == IslandMode.Idle)
            {
                AnimateIsland(s.IdleWidth + 20, s.IdleHeight, s.CornerRadius);
            }
            else if (_currentMode == IslandMode.MediaCompact)
            {
                AnimateIsland(Math.Max(s.IdleWidth + 80, 270) + 20, s.IdleHeight, s.CornerRadius);
            }
        }

        private void IslandContainer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isFileTransferInProgress || 
                _currentMode == IslandMode.FileDropHover || 
                _currentMode == IslandMode.FileTransferring || 
                _currentMode == IslandMode.FileResult ||
                _currentMode == IslandMode.SliderHud)
            {
                return;
            }

            var s = SettingsService.Instance.Current;
            if (s.HoverAnimation)
            {
                if (_currentMode == IslandMode.Idle)
                {
                    AnimateIsland(s.IdleWidth, s.IdleHeight, s.CornerRadius);
                }
                else if (_currentMode == IslandMode.MediaCompact)
                {
                    AnimateIsland(Math.Max(s.IdleWidth + 80, 270), s.IdleHeight, s.CornerRadius);
                }
            }

            StartAutoHideTimer();
        }

        private void IslandContainer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isFileTransferInProgress || _currentMode == IslandMode.FileTransferring || _currentMode == IslandMode.SliderHud) return;

            _startPoint = e.GetPosition(this);
            _isDragging = false;
        }

        private void IslandContainer_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                Point currentPoint = e.GetPosition(this);
                Vector diff = _startPoint - currentPoint;
                if (Math.Abs(diff.X) > 6 || Math.Abs(diff.Y) > 6)
                {
                    _isDragging = true;
                    try
                    {
                        DragMove();
                    }
                    catch
                    {
                        // Bỏ qua lỗi ngắt DragMove khi nhả chuột
                    }
                }
            }
        }

        private void IslandContainer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging)
            {
                if (_isFileTransferInProgress || 
                    _currentMode == IslandMode.FileTransferring || 
                    _currentMode == IslandMode.FileResult ||
                    _currentMode == IslandMode.SliderHud)
                {
                    _isDragging = false;
                    return;
                }

                // Click hợp lệ: Mở rộng Control Center
                if (_currentMode != IslandMode.Expanded && _currentMode != IslandMode.ExpandedMedia)
                {
                    SwitchMode(IslandMode.Expanded);
                }
            }
            _isDragging = false;
        }

        private void BtnCollapse_Click(object sender, RoutedEventArgs e)
        {
            SwitchMode(_isMediaActive ? IslandMode.MediaCompact : IslandMode.Idle);
            StartAutoHideTimer();
        }
        #endregion

        #region Media Control Buttons
        private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TogglePlayPauseAsync();
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipNextAsync();
        }

        private async void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipPreviousAsync();
        }
        #endregion

        #region Context Menu & Settings Actions
        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow.ShowOrActivate();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow.ShowOrActivate();
        }

        private void MenuResetPosition_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Update(s =>
            {
                s.TopOffset = 3;
                s.HorizontalOffset = 0;
            });
        }

        private void MenuSwitchNextMonitor_Click(object sender, RoutedEventArgs e)
        {
            SwitchToNextScreen();
        }

        private void MenuToggleExpand_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMode == IslandMode.Expanded || _currentMode == IslandMode.ExpandedMedia)
            {
                SwitchMode(_isMediaActive ? IslandMode.MediaCompact : IslandMode.Idle);
            }
            else
            {
                SwitchMode(IslandMode.Expanded);
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void MenuAutoHide_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Update(s => s.AutoHide = MenuAutoHide.IsChecked);
        }

        private void IslandContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            StartAutoHideTimer();
        }
        #endregion

        #region Auto-Hide Logic
        public void ShowIsland(bool instant = false)
        {
            if (!_isIslandHidden) return;
            _isIslandHidden = false;

            if (instant)
            {
                IslandTransform.BeginAnimation(TranslateTransform.YProperty, null);
                IslandTransform.Y = 0;
                if (IslandShadow != null && SettingsService.Instance.Current.EnableShadow)
                {
                    IslandShadow.BeginAnimation(UIElement.OpacityProperty, null);
                    IslandShadow.Opacity = 1;
                }
            }
            else
            {
                var anim = new DoubleAnimation(IslandTransform.Y, 0, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                IslandTransform.BeginAnimation(TranslateTransform.YProperty, anim);

                if (IslandShadow != null && SettingsService.Instance.Current.EnableShadow)
                {
                    var animShadow = new DoubleAnimation(IslandShadow.Opacity, 1.0, TimeSpan.FromMilliseconds(260));
                    IslandShadow.BeginAnimation(UIElement.OpacityProperty, animShadow);
                }
            }
        }

        public void HideIsland()
        {
            if (_isIslandHidden) return;
            if (_currentMode == IslandMode.Expanded) return;
            if (IslandContextMenu.IsOpen) return;
            if (IslandContainer.IsMouseOver) return;

            _isIslandHidden = true;
            double targetY = -Math.Max(52, IslandContainer.ActualHeight + 16);

            var anim = new DoubleAnimation(IslandTransform.Y, targetY, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            IslandTransform.BeginAnimation(TranslateTransform.YProperty, anim);

            if (IslandShadow != null)
            {
                var animShadow = new DoubleAnimation(IslandShadow.Opacity, 0.0, TimeSpan.FromMilliseconds(200));
                IslandShadow.BeginAnimation(UIElement.OpacityProperty, animShadow);
            }
        }

        private void StartAutoHideTimer()
        {
            if (!SettingsService.Instance.Current.AutoHide) return;
            if (_currentMode == IslandMode.Expanded) return;
            if (IslandContextMenu.IsOpen) return;

            _autoHideTimer.Stop();
            _autoHideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.5, SettingsService.Instance.Current.AutoHideDelay));
            _autoHideTimer.Start();
        }

        private void AutoHideTimer_Tick(object? sender, EventArgs e)
        {
            _autoHideTimer.Stop();
            if (!IslandContainer.IsMouseOver && !IslandContextMenu.IsOpen && _currentMode != IslandMode.Expanded)
            {
                HideIsland();
            }
        }

        private void CursorCheckTimer_Tick(object? sender, EventArgs e)
        {
            var s = SettingsService.Instance.Current;
            if (!s.AutoHide || _screens.Length == 0) return;

            if (Win32Helper.GetCursorPos(out var pt))
            {
                var screen = _screens[Math.Clamp(_currentScreenIndex, 0, _screens.Length - 1)];

                // Kiểm tra xem chuột có nằm trên màn hình hiển thị Island không
                bool onScreenX = pt.X >= screen.Bounds.Left && pt.X <= screen.Bounds.Right;
                // Kiểm tra chuột có chạm viền trên cùng không (Y <= Top + 4px)
                bool atTopEdge = pt.Y <= screen.Bounds.Top + 4;

                if (atTopEdge && onScreenX)
                {
                    int centerX = screen.Bounds.X + (screen.Bounds.Width / 2) + (int)s.HorizontalOffset;
                    double activeWidth = Math.Max(380, IslandContainer.ActualWidth + 120);
                    bool nearIslandX = Math.Abs(pt.X - centerX) <= (activeWidth / 2);

                    if (nearIslandX || atTopEdge)
                    {
                        if (_isIslandHidden)
                        {
                            ShowIsland();
                        }
                        _autoHideTimer.Stop();
                    }
                }
            }
        }
        #endregion

        #region LocalSend Drag & Drop File Transfer
        private void IslandContainer_PreviewDragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (_isFileTransferInProgress) return;

            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                e.Handled = true;

                var settings = SettingsService.Instance.Current;
                string methodHint = settings.FileTransferMethod == "WindowsShare" 
                    ? "Phone Link" 
                    : "LocalSend";

                if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (files.Length == 1)
                    {
                        var fi = new System.IO.FileInfo(files[0]);
                        string sizeStr = fi.Length > 1024 * 1024 
                            ? $"{fi.Length / (1024.0 * 1024.0):0.1} MB" 
                            : $"{Math.Max(1, fi.Length / 1024)} KB";
                        TxtFileDropSubtitle.Text = $"{fi.Name} ({sizeStr}) • {methodHint}";
                    }
                    else
                    {
                        long totalBytes = 0;
                        foreach (var f in files)
                        {
                            if (System.IO.File.Exists(f))
                            {
                                totalBytes += new System.IO.FileInfo(f).Length;
                            }
                        }
                        string sizeStr = totalBytes > 1024 * 1024 
                            ? $"{totalBytes / (1024.0 * 1024.0):0.1} MB" 
                            : $"{Math.Max(1, totalBytes / 1024)} KB";
                        TxtFileDropSubtitle.Text = $"{files.Length} tệp ({sizeStr}) • {methodHint}";
                    }
                }

                _autoHideTimer.Stop();
                if (_isIslandHidden) ShowIsland();
                SwitchMode(IslandMode.FileDropHover);
            }
        }

        private void IslandContainer_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void IslandContainer_PreviewDragLeave(object sender, System.Windows.DragEventArgs e)
        {
            if (_isFileTransferInProgress) return;

            if (_currentMode == IslandMode.FileDropHover)
            {
                RestoreDefaultIslandMode();
            }
        }

        private async void IslandContainer_PreviewDrop(object sender, System.Windows.DragEventArgs e)
        {
            if (_isFileTransferInProgress) return;

            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Handled = true;
                if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    await ProcessSendFilesAsync(files);
                }
                else
                {
                    RestoreDefaultIslandMode();
                }
            }
        }

        private async Task ProcessSendFilesAsync(string[] files)
        {
            _isFileTransferInProgress = true;
            _autoHideTimer.Stop();

            try
            {
                var settings = SettingsService.Instance.Current;

                // ================= PHƯƠNG ÁN 1: WINDOWS SHARE TARGET (Phone Link / Nearby Share) =================
                // Phù hợp hoàn hảo khi ở quán cà phê, trường học, Wi-Fi công cộng (nơi router bật AP Isolation)
                if (settings.FileTransferMethod == "WindowsShare")
                {
                    SwitchMode(IslandMode.FileTransferring);
                    TransferProgressBar.Value = 50;
                    TxtTransferPercent.Text = "⚡";
                    TxtTransferStatus.Text = "Đang mở Phone Link / Chia sẻ...";

                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    bool opened = await WindowsShareService.Instance.ShareFilesAsync(hwnd, files);

                    if (opened)
                    {
                        TransferProgressBar.Value = 100;
                        string countStr = files.Length == 1 ? "1 tệp" : $"{files.Length} tệp";
                        ShowFileResult(true, $"Đã mở chia sẻ ({countStr})!");
                    }
                    else
                    {
                        ShowFileResult(false, "Không thể mở bảng chia sẻ Windows");
                    }
                    return;
                }

                // ================= PHƯƠNG ÁN 2: LOCALSEND API (Mạng Wi-Fi nội bộ) =================
                SwitchMode(IslandMode.FileTransferring);
                TransferProgressBar.Value = 5;
                TxtTransferPercent.Text = "0%";
                TxtTransferStatus.Text = "Đang tìm điện thoại...";

                LocalSendDeviceInfo? targetDevice = null;

                // 1. Tìm kiếm thiết bị LocalSend
                if (settings.LocalSendAutoDiscovery || string.IsNullOrWhiteSpace(settings.LocalSendTargetIp))
                {
                    targetDevice = await LocalSendService.Instance.DiscoverDeviceAsync(
                        settings.LocalSendTargetIp, 
                        settings.LocalSendPort);
                }
                else
                {
                    targetDevice = await LocalSendService.Instance.TestDeviceAsync(
                        settings.LocalSendTargetIp, 
                        settings.LocalSendPort);
                }

                if (targetDevice == null)
                {
                    // Tự động Fallback sang Phương án 1 (Phone Link) nếu LocalSend bị chặn mạng (quán cafe, AP isolation)
                    TxtTransferStatus.Text = "Không tìm thấy LocalSend. Đang mở Phone Link...";
                    TransferProgressBar.Value = 50;
                    await Task.Delay(600);

                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    bool fallbackOpened = await WindowsShareService.Instance.ShareFilesAsync(hwnd, files);

                    if (fallbackOpened)
                    {
                        ShowFileResult(true, "Wi-Fi chặn P2P: Đã mở Phone Link thay thế!");
                    }
                    else
                    {
                        ShowFileResult(false, "Không tìm thấy điện thoại (Wi-Fi chặn kết nối)");
                    }
                    return;
                }

                // Cập nhật tên thiết bị và IP vào cài đặt nếu phát hiện được
                if (!string.IsNullOrEmpty(targetDevice.IpAddress) && targetDevice.IpAddress != settings.LocalSendTargetIp)
                {
                    SettingsService.Instance.Update(s =>
                    {
                        s.LocalSendTargetIp = targetDevice.IpAddress;
                        s.LocalSendTargetName = targetDevice.Alias;
                    });
                }

                // 2. Bắt đầu gửi file qua LocalSend API
                TxtTransferStatus.Text = $"Gửi tới {targetDevice.Alias}...";
                
                var sendResult = await LocalSendService.Instance.SendFilesAsync(
                    files,
                    targetDevice.IpAddress,
                    targetDevice.Port,
                    targetDevice.Protocol,
                    onProgress: (p) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TransferProgressBar.Value = p;
                            TxtTransferPercent.Text = $"{(int)p}%";
                        });
                    });

                if (sendResult.Success)
                {
                    string countStr = files.Length == 1 ? "1 tệp" : $"{files.Length} tệp";
                    ShowFileResult(true, $"Đã gửi {countStr} thành công!");
                }
                else
                {
                    ShowFileResult(false, sendResult.Message);
                }
            }
            catch (Exception ex)
            {
                ShowFileResult(false, $"Lỗi: {ex.Message}");
            }
            finally
            {
                _isFileTransferInProgress = false;
            }
        }

        private void ShowFileResult(bool success, string message)
        {
            TxtResultIcon.Text = success ? "\uE73E" : "\uE7BA"; // CheckMark vs Warning
            TxtResultIcon.Foreground = success 
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59)) // Green #34C759
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A)); // Red #FF453A
            TxtResultMessage.Text = message;
            TxtResultMessage.Foreground = success 
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x85, 0x7E));

            SwitchMode(IslandMode.FileResult);

            _fileResultTimer?.Stop();
            _fileResultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _fileResultTimer.Tick += (s, e) =>
            {
                _fileResultTimer.Stop();
                RestoreDefaultIslandMode();
            };
            _fileResultTimer.Start();
        }

        private void RestoreDefaultIslandMode()
        {
            if (_isMediaActive && SettingsService.Instance.Current.AutoSwitchMedia)
            {
                SwitchMode(IslandMode.MediaCompact);
            }
            else
            {
                SwitchMode(IslandMode.Idle);
            }
        }
        #endregion

        #region Volume & Brightness HUD
        private void IslandContainer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var s = SettingsService.Instance.Current;
            if (!s.MouseWheelVolumeEnabled) return;

            e.Handled = true;
            _autoHideTimer.Stop();
            if (_isIslandHidden) ShowIsland();

            bool isAlt = Win32Helper.IsKeyDown(0x12) || // VK_MENU (Alt key toàn hệ thống)
                         Keyboard.IsKeyDown(Key.LeftAlt) || 
                         Keyboard.IsKeyDown(Key.RightAlt) || 
                         (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            if (isAlt)
            {
                // Điều chỉnh độ sáng màn hình
                int delta = e.Delta > 0 ? 5 : -5;
                BrightnessService.Instance.AdjustBrightness(delta);
            }
            else
            {
                // Điều chỉnh âm lượng hệ thống
                float delta = e.Delta > 0 ? 0.02f : -0.02f;
                VolumeService.Instance.AdjustVolume(delta);
            }
        }

        private void VolumeService_VolumeChanged(object? sender, VolumeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateExpVolumeUI(e.VolumePercent, e.IsMuted);
                if (SettingsService.Instance.Current.VolumeBrightnessHudEnabled && _currentMode != IslandMode.Expanded)
                {
                    ShowVolumeHud(e.VolumePercent, e.IsMuted);
                }
            });
        }

        private void BrightnessService_BrightnessChanged(object? sender, BrightnessChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateExpBrightnessUI(e.Brightness);
                if (SettingsService.Instance.Current.VolumeBrightnessHudEnabled && _currentMode != IslandMode.Expanded)
                {
                    ShowBrightnessHud(e.Brightness);
                }
            });
        }

        private void ShowVolumeHud(int percent, bool isMuted)
        {
            if (_isFileTransferInProgress || _currentMode == IslandMode.FileTransferring) return;

            _autoHideTimer.Stop();
            if (_isIslandHidden) ShowIsland();

            _isAdjustingBrightness = false;
            BtnHudIcon.ToolTip = isMuted ? "Đã tắt tiếng (Click để bật)" : "Đang bật tiếng (Click để tắt)";
            
            if (isMuted || percent == 0)
            {
                var muteBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A)); // Red #FF453A
                TxtHudIcon.Text = "\uE74F"; // Mute
                TxtHudIcon.Foreground = muteBrush;
                TxtHudValue.Foreground = muteBrush;
                HudProgressBar.Foreground = muteBrush;
            }
            else
            {
                if (percent < 33)
                {
                    TxtHudIcon.Text = "\uE992"; // Volume 1
                }
                else if (percent < 66)
                {
                    TxtHudIcon.Text = "\uE993"; // Volume 2
                }
                else
                {
                    TxtHudIcon.Text = "\uE994"; // Volume 3
                }
                var volBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                TxtHudIcon.Foreground = volBrush;
                TxtHudValue.Foreground = volBrush;
                HudProgressBar.Foreground = volBrush;
            }

            HudProgressBar.Value = percent;
            TxtHudValue.Text = $"{percent}%";

            if (_currentMode != IslandMode.SliderHud)
            {
                SwitchMode(IslandMode.SliderHud);
            }

            ResetHudDismissTimer();
        }

        private void ShowBrightnessHud(int percent)
        {
            if (_isFileTransferInProgress || _currentMode == IslandMode.FileTransferring) return;

            _autoHideTimer.Stop();
            if (_isIslandHidden) ShowIsland();

            _isAdjustingBrightness = true;
            TxtHudIcon.Text = "\uE706"; // Brightness / Sun
            BtnHudIcon.ToolTip = "Độ sáng màn hình";

            var sunBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD5, 0x4F)); // Sun Gold #FFD54F
            TxtHudIcon.Foreground = sunBrush;
            TxtHudValue.Foreground = sunBrush;
            HudProgressBar.Foreground = sunBrush;

            HudProgressBar.Value = percent;
            TxtHudValue.Text = $"{percent}%";

            if (_currentMode != IslandMode.SliderHud)
            {
                SwitchMode(IslandMode.SliderHud);
            }

            ResetHudDismissTimer();
        }

        private void ResetHudDismissTimer()
        {
            _hudAutoDismissTimer.Stop();
            _hudAutoDismissTimer.Interval = TimeSpan.FromSeconds(1.8);
            _hudAutoDismissTimer.Start();
        }

        private void HudAutoDismissTimer_Tick(object? sender, EventArgs e)
        {
            _hudAutoDismissTimer.Stop();
            if (_currentMode == IslandMode.SliderHud)
            {
                RestoreDefaultIslandMode();
            }
        }

        private void BtnHudIcon_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAdjustingBrightness)
            {
                VolumeService.Instance.ToggleMute();
            }
        }
        #endregion

        #region Control Center Actions & Events
        private void UpdateControlCenterContent()
        {
            float vol = VolumeService.Instance.GetVolume();
            bool muted = VolumeService.Instance.IsMuted();
            UpdateExpVolumeUI((int)Math.Round(vol * 100), muted);

            int bri = BrightnessService.Instance.GetBrightness();
            UpdateExpBrightnessUI(bri);

            if (BtnSwitchToMedia != null)
            {
                BtnSwitchToMedia.Visibility = _isMediaActive ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TxtExpClock != null)
            {
                TxtExpClock.Text = DateTime.Now.ToString(SettingsService.Instance.Current.Use24HourClock ? "HH:mm" : "hh:mm tt");
            }
        }

        private void UpdateExpVolumeUI(int percent, bool isMuted)
        {
            if (SliderExpVolume == null) return;
            _isUpdatingSliders = true;
            try
            {
                SliderExpVolume.Value = percent;
                TxtExpVolumePercent.Text = $"{percent}%";
                if (isMuted || percent == 0)
                {
                    var muteBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A)); // Red #FF453A
                    TxtExpVolumeIcon.Text = "\uE74F"; // Mute
                    TxtExpVolumeIcon.Foreground = muteBrush;
                    SliderExpVolume.Foreground = muteBrush;
                    TxtExpVolumePercent.Foreground = muteBrush;
                }
                else
                {
                    if (percent < 33)
                    {
                        TxtExpVolumeIcon.Text = "\uE992"; // Volume 1
                    }
                    else if (percent < 66)
                    {
                        TxtExpVolumeIcon.Text = "\uE993"; // Volume 2
                    }
                    else
                    {
                        TxtExpVolumeIcon.Text = "\uE994"; // Volume 3
                    }
                    var volBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                    TxtExpVolumeIcon.Foreground = volBrush;
                    SliderExpVolume.Foreground = volBrush;
                    TxtExpVolumePercent.Foreground = volBrush;
                }
            }
            finally
            {
                _isUpdatingSliders = false;
            }
        }

        private void UpdateExpBrightnessUI(int percent)
        {
            if (SliderExpBrightness == null) return;
            _isUpdatingSliders = true;
            try
            {
                var sunBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD5, 0x4F)); // Sun Gold #FFD54F
                SliderExpBrightness.Value = percent;
                TxtExpBrightnessPercent.Text = $"{percent}%";
                SliderExpBrightness.Foreground = sunBrush;
                TxtExpBrightnessPercent.Foreground = sunBrush;
            }
            finally
            {
                _isUpdatingSliders = false;
            }
        }

        private void SliderExpVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingSliders || TxtExpVolumePercent == null) return;
            int percent = (int)Math.Round(e.NewValue);
            TxtExpVolumePercent.Text = $"{percent}%";
            VolumeService.Instance.SetVolume(percent / 100f);
        }

        private void SliderExpBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingSliders || TxtExpBrightnessPercent == null) return;
            int percent = (int)Math.Round(e.NewValue);
            TxtExpBrightnessPercent.Text = $"{percent}%";
            BrightnessService.Instance.SetBrightness(percent);
        }

        private void BtnExpVolumeIcon_Click(object sender, RoutedEventArgs e)
        {
            VolumeService.Instance.ToggleMute();
        }

        private void BtnSwitchToMedia_Click(object sender, RoutedEventArgs e)
        {
            SwitchMode(IslandMode.ExpandedMedia);
        }

        private void BtnSwitchToControlCenter_Click(object sender, RoutedEventArgs e)
        {
            SwitchMode(IslandMode.Expanded);
        }
        #endregion

        #region Privacy Indicators Callbacks & Actions
        private void PrivacyService_PrivacyStatusChanged(object? sender, PrivacyStatusChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    bool showPrivacy = SettingsService.Instance.Current.EnablePrivacyIndicators;

                    // 1. Chấm Micro & Camera ở Compact Idle View
                    if (PrivacyIndicatorsPanel != null)
                    {
                        bool anyInUse = (e.IsMicrophoneInUse || e.IsCameraInUse) && showPrivacy;
                        PrivacyIndicatorsPanel.Visibility = anyInUse ? Visibility.Visible : Visibility.Collapsed;

                        if (MicPrivacyDot != null)
                        {
                            MicPrivacyDot.Visibility = e.IsMicrophoneInUse && showPrivacy ? Visibility.Visible : Visibility.Collapsed;
                        }
                        if (TxtMicTooltipApps != null)
                        {
                            TxtMicTooltipApps.Text = e.MicrophoneSummary;
                        }

                        if (CameraPrivacyDot != null)
                        {
                            CameraPrivacyDot.Visibility = e.IsCameraInUse && showPrivacy ? Visibility.Visible : Visibility.Collapsed;
                        }
                        if (TxtCamTooltipApps != null)
                        {
                            TxtCamTooltipApps.Text = e.CameraSummary;
                        }
                    }

                    // 2. Chấm Micro & Camera ở Compact Media View
                    if (PrivacyIndicatorsPanelMedia != null)
                    {
                        bool anyInUse = (e.IsMicrophoneInUse || e.IsCameraInUse) && showPrivacy;
                        PrivacyIndicatorsPanelMedia.Visibility = anyInUse ? Visibility.Visible : Visibility.Collapsed;

                        if (MicPrivacyDotMedia != null)
                        {
                            MicPrivacyDotMedia.Visibility = e.IsMicrophoneInUse && showPrivacy ? Visibility.Visible : Visibility.Collapsed;
                        }
                        if (TxtMicTooltipAppsMedia != null)
                        {
                            TxtMicTooltipAppsMedia.Text = $"🎤 {e.MicrophoneSummary}";
                        }

                        if (CameraPrivacyDotMedia != null)
                        {
                            CameraPrivacyDotMedia.Visibility = e.IsCameraInUse && showPrivacy ? Visibility.Visible : Visibility.Collapsed;
                        }
                        if (TxtCamTooltipAppsMedia != null)
                        {
                            TxtCamTooltipAppsMedia.Text = $"📷 {e.CameraSummary}";
                        }
                    }

                    // 3. Nút Global Mic Mute & Huy hiệu Privacy trong Trung tâm điều khiển
                    UpdateControlCenterPrivacyUI(e);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"PrivacyStatusChanged error: {ex.Message}");
                }
            });
        }

        private void UpdateControlCenterPrivacyUI(PrivacyStatusChangedEventArgs e)
        {
            if (BtnGlobalMicMute != null)
            {
                if (e.IsMicrophoneMuted)
                {
                    TxtGlobalMicIcon.Text = "\uE740"; // Mic Off
                    TxtGlobalMicIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A));
                    TxtGlobalMicText.Text = "Mic MUTE";
                    TxtGlobalMicText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A));
                }
                else
                {
                    TxtGlobalMicIcon.Text = "\uE720"; // Mic On
                    TxtGlobalMicIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));
                    TxtGlobalMicText.Text = "Mic ON";
                    TxtGlobalMicText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0xE5, 0xEA));
                }
            }

            if (ExpPrivacyBadge != null)
            {
                if (e.IsMicrophoneInUse || e.IsCameraInUse)
                {
                    ExpPrivacyBadge.Visibility = Visibility.Visible;
                    if (e.IsMicrophoneInUse && e.IsCameraInUse)
                    {
                        ExpPrivacyDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));
                        TxtExpPrivacyText.Text = "Mic & Cam đang dùng";
                        TxtExpPrivacyText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));
                    }
                    else if (e.IsMicrophoneInUse)
                    {
                        ExpPrivacyDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));
                        string firstApp = e.MicrophoneApps.Count > 0 ? e.MicrophoneApps[0] : "App";
                        TxtExpPrivacyText.Text = $"{firstApp} dùng Mic";
                        TxtExpPrivacyText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));
                    }
                    else
                    {
                        ExpPrivacyDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));
                        string firstApp = e.CameraApps.Count > 0 ? e.CameraApps[0] : "App";
                        TxtExpPrivacyText.Text = $"{firstApp} dùng Cam";
                        TxtExpPrivacyText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));
                    }
                }
                else
                {
                    ExpPrivacyBadge.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void MicPrivacyDot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            bool newMuted = PrivacyService.Instance.ToggleMicrophoneMute();
            ShowMicToast(newMuted);
        }

        private void BtnGlobalMicMute_Click(object sender, RoutedEventArgs e)
        {
            bool newMuted = PrivacyService.Instance.ToggleMicrophoneMute();
            ShowMicToast(newMuted);
        }

        private void ShowMicToast(bool isMuted)
        {
            ShowLockKeyHud(new LockKeyChangedEventArgs
            {
                KeyType = LockKeyType.InputMethod,
                IsOn = !isMuted,
                Title = "Microphone",
                StatusText = isMuted ? "MUTE" : "ON",
                Subtitle = isMuted ? "Đã tắt Micro toàn hệ thống" : "Đã bật Micro toàn hệ thống",
                IconUnicode = isMuted ? "\uE740" : "\uE720"
            });
        }
        #endregion

        #region Lock Key & Input Method Notifications
        private void LockKeyService_LockKeyChanged(object? sender, LockKeyChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (!SettingsService.Instance.Current.EnableLockKeyNotifications) return;
                ShowLockKeyHud(e);
            });
        }

        private void ShowLockKeyHud(LockKeyChangedEventArgs e)
        {
            if (_isFileTransferInProgress || _currentMode == IslandMode.FileTransferring) return;

            _autoHideTimer.Stop();
            if (_isIslandHidden) ShowIsland();

            TxtLockKeyIcon.Text = e.IconUnicode;
            TxtLockKeyTitle.Text = e.Title;
            TxtLockKeySubtitle.Text = e.Subtitle;
            TxtLockKeyStatus.Text = e.StatusText;

            // Màu sắc trạng thái đặc trưng
            if (e.KeyType == LockKeyType.InputMethod)
            {
                if (e.StatusText == "VIE")
                {
                    TxtLockKeyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30)); // Red Vietnamese flag
                    TxtLockKeyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30));
                    LockKeyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x15, 0x15));
                    LockKeyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5C, 0x24, 0x24));
                }
                else if (e.StatusText == "ENG")
                {
                    TxtLockKeyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF)); // Sky blue
                    TxtLockKeyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF));
                    LockKeyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x24, 0x3A));
                    LockKeyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x48, 0x6E));
                }
                else if (e.StatusText == "MUTE")
                {
                    TxtLockKeyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A));
                    TxtLockKeyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A));
                    LockKeyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x15, 0x15));
                    LockKeyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5C, 0x24, 0x24));
                }
                else
                {
                    TxtLockKeyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));
                    TxtLockKeyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));
                    LockKeyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x2A, 0x1C));
                    LockKeyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x58, 0x32));
                }
            }
            else
            {
                if (e.IsOn)
                {
                    TxtLockKeyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59)); // Green BẬT
                    TxtLockKeyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));
                    LockKeyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x2A, 0x1C));
                    LockKeyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x58, 0x32));
                }
                else
                {
                    TxtLockKeyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
                    TxtLockKeyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93)); // Dimmed TẮT
                    LockKeyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1E));
                    LockKeyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2E));
                }
            }

            if (_currentMode != IslandMode.Expanded && _currentMode != IslandMode.ExpandedMedia)
            {
                SwitchMode(IslandMode.LockKeyHud);
            }

            _lockKeyDismissTimer.Stop();
            _lockKeyDismissTimer.Interval = TimeSpan.FromSeconds(1.6);
            _lockKeyDismissTimer.Start();
        }

        private void LockKeyDismissTimer_Tick(object? sender, EventArgs e)
        {
            _lockKeyDismissTimer.Stop();
            if (_currentMode == IslandMode.LockKeyHud)
            {
                RestoreDefaultIslandMode();
            }
        }
        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            PrivacyService.Instance.Stop();
            LockKeyService.Instance.Stop();
        }
    }
}