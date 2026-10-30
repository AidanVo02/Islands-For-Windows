using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
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

        LockKeyHud,

        Search
    }

    public partial class MainWindow : Window

    {
        private readonly MediaService _mediaService = new();

        private readonly BatteryService _batteryService = new();

        private readonly DispatcherTimer _clockTimer = new();

        private ExtractedColorPalette? _currentPalette;

        private LinearGradientBrush? _waveGradientBrush;

        private GradientStop? _waveStop0;

        private GradientStop? _waveStop1;



        private IslandMode _currentMode = IslandMode.Idle;

        private bool _isMediaActive = false;

        private DateTime _lastViewSwitchTime = DateTime.MinValue;

        // Quản lý phát nhạc & Live Lyrics theo thời gian thực

        private readonly DispatcherTimer _playbackProgressTimer = new();

        private TimeSpan _currentPosition = TimeSpan.Zero;

        private TimeSpan _currentDuration = TimeSpan.Zero;

        private bool _isPlaying = false;

        private string _currentTrackTitle = "";

        private string _currentArtist = "";

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

        private bool _dragAttempted = false;

        private bool _isInteractingWithControl = false;

        // Quản lý Tự động ẩn (Auto-Hide)

        private bool _isIslandHidden = false;

        private readonly DispatcherTimer _autoHideTimer = new();

        private readonly DispatcherTimer _cursorCheckTimer = new();

        // Quản lý Quick Search & App Launcher

        private bool _isSearchActive = false;

        private IslandMode _modeBeforeSearch = IslandMode.Idle;

        private List<LauncherResultItem> _currentSearchResults = new();

        private const int HOTKEY_ID_SEARCH = 9001;

        private bool _isSearchHotkeyRegistered = false;

        // Quản lý Smooth Kinetic Live Lyrics

        private readonly List<TextBlock> _lyricTextBlocks = new();

        private int _currentActiveLyricIndex = -1;

        private string _lastMiniTitle = "";

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

                InitializeVisualizerAndGlow();



                // Cấu hình timer Tự động ẩn (Auto-Hide)

                _autoHideTimer.Tick += AutoHideTimer_Tick;

                _cursorCheckTimer.Interval = TimeSpan.FromMilliseconds(50);

                _cursorCheckTimer.Tick += CursorCheckTimer_Tick;

                // Khởi động dịch vụ Pin

                _batteryService.BatteryChanged += BatteryService_BatteryChanged;

                _batteryService.Initialize();

                // Khởi động bộ đếm vị trí phát nhạc (250ms) & Live Lyrics (Đăng ký trước khi init media để không bị mất event)

                _playbackProgressTimer.Interval = TimeSpan.FromMilliseconds(250);

                _playbackProgressTimer.Tick += PlaybackProgressTimer_Tick;

                LyricsService.Instance.ActiveLinesChanged += LyricsService_ActiveLinesChanged;

                LyricsService.Instance.LyricsLoaded += LyricsService_LyricsLoaded;

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

                // Khởi động dịch vụ Quick Search & App Launcher

                AppLauncherService.Instance.Initialize();

                if (settings.EnableSearchHotkey)

                {
                    RegisterSearchHotkey();
                }

                // Tự động thu nhỏ khi mất tiêu điểm cửa sổ (Deactivated)

                Deactivated += (s, args) =>

                {
                    if (_isSearchActive)

                    {
                        ExitSearchMode();

                        return;
                    }

                    if (_currentMode == IslandMode.Expanded || _currentMode == IslandMode.ExpandedMedia)

                    {
                        if (SettingsWindow.IsCursorOverSettings(new Win32Helper.POINT())) return;

                        CollapseToDefault();
                    }
                };
            }

            catch (Exception ex)

            {
                System.Diagnostics.Debug.WriteLine($"Window_Loaded error: {ex}");
            }
        }

        protected override void OnSourceInitialized(EventArgs e)

        {
            base.OnSourceInitialized(e);

            var source = PresentationSource.FromVisual(this) as HwndSource;

            source?.AddHook(WndProc);
        }

        private const int WM_MOUSEHWHEEL = 0x020E;

        private const int WM_HOTKEY = 0x0312;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)

        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_SEARCH)

            {
                ToggleSearchMode();

                handled = true;

                return IntPtr.Zero;
            }

            if (msg == WM_MOUSEHWHEEL)

            {
                if (_currentMode == IslandMode.Expanded || _currentMode == IslandMode.ExpandedMedia)

                {
                    short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

                    if (delta != 0)

                    {
                        if ((DateTime.Now - _lastViewSwitchTime).TotalMilliseconds > 300)

                        {
                            _lastViewSwitchTime = DateTime.Now;

                            SwitchExpandedView();
                        }

                        handled = true;

                        return IntPtr.Zero;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private void Window_Closed(object? sender, EventArgs e)

        {
            UnregisterSearchHotkey();

            StopOutsideClickHook();
        }

        private void SettingsService_SettingsChanged(IslandSettings settings)

        {
            Dispatcher.BeginInvoke(() =>

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
                    IslandShadow.Background = new SolidColorBrush(Colors.Black);

                    IslandShadow.Visibility = s.EnableShadow ? Visibility.Visible : Visibility.Collapsed;

                    if (IslandDropShadow != null)
                    {
                        IslandDropShadow.Opacity = s.ShadowOpacity;
                    }
                }

                AudioVisualizerService.Instance.IsEnabled = s.EqualizerEnabled;
                if (!s.EqualizerEnabled)
                {
                    ResetEqualizerBars();
                }
                else if (_isPlaying && _isMediaActive)
                {
                    AudioVisualizerService.Instance.Start();
                }

                if (!s.AdaptiveColorGlowEnabled)
                {
                    RestoreDefaultShadow(animate: false);
                }
                else if (_isMediaActive && _isPlaying && _currentPalette != null)
                {
                    ApplyAdaptiveColors(_currentPalette, animate: false);
                }

                var transparentBg = System.Windows.Media.Color.FromArgb(0, bg.R, bg.G, bg.B);

                if (LyricsTopFadeBorder != null)

                {
                    LyricsTopFadeBorder.Background = new LinearGradientBrush(bg, transparentBg, new Point(0, 0), new Point(0, 1));
                }

                if (LyricsBottomFadeBorder != null)

                {
                    LyricsBottomFadeBorder.Background = new LinearGradientBrush(transparentBg, bg, new Point(0, 0), new Point(0, 1));
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

            if (MenuLockPosition != null)

            {
                MenuLockPosition.IsChecked = s.LockPosition;
            }

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

            if (s.EnableSearchHotkey && !_isSearchHotkeyRegistered)

            {
                RegisterSearchHotkey();
            }

            else if (!s.EnableSearchHotkey && _isSearchHotkeyRegistered)

            {
                UnregisterSearchHotkey();
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

            MenuLockPosition.IsChecked = SettingsService.Instance.Current.LockPosition;

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
            Dispatcher.BeginInvoke(() =>

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
            Dispatcher.BeginInvoke(() =>

            {
                try

                {
                    var s = SettingsService.Instance.Current;

                    string newTitle = track.Title ?? "";

                    string newArtist = track.Artist ?? "";

                    bool isNewTrack = !string.Equals(_currentTrackTitle, newTitle, StringComparison.OrdinalIgnoreCase)
                                   || !string.Equals(_currentArtist, newArtist, StringComparison.OrdinalIgnoreCase);

                    _isMediaActive = !string.IsNullOrWhiteSpace(newTitle) && newTitle != "No Media Playing";

                    _isPlaying = track.IsPlaying;

                    _currentTrackTitle = newTitle;

                    _currentArtist = newArtist;

                    // Chỉ reset mốc thời gian nếu là bài hát mới, hoặc nếu Windows GSMTC cung cấp position hợp lệ (>0)
                    if (isNewTrack || track.Position > TimeSpan.Zero)
                    {
                        _currentPosition = track.Position;
                    }

                    if (track.Duration > TimeSpan.Zero)
                    {
                        _currentDuration = track.Duration;
                    }

                    TxtElapsed.Text = FormatTime(_currentPosition);

                    TxtDuration.Text = FormatTime(_currentDuration);

                    TxtMiniTitle.Text = newTitle;

                    ImgMiniThumb.Source = track.Thumbnail;

                    TxtLargeTitle.Text = newTitle;

                    TxtLargeArtist.Text = newArtist;

                    ImgLargeThumb.Source = track.Thumbnail;

                    string source = "Media";

                    string appId = (track.SourceAppId ?? string.Empty).ToLower();

                    if (appId.Contains("spotify")) source = "Spotify";

                    else if (appId.Contains("chrome")) source = "Chrome";

                    else if (appId.Contains("edge") || appId.Contains("msedge")) source = "Edge";

                    else if (appId.Contains("apple")) source = "Apple Music";

                    TxtAppSource.Text = source;

                    UpdatePlaybackIcon(track.IsPlaying);

                    if (_isMediaActive)

                    {
                        // Chỉ tải lại lời và reset UI khi THỰC SỰ là bài hát mới
                        if (isNewTrack)
                        {
                            ResetLyricsUI("Đang tìm lời bài hát...");

                            TxtLyricsStatus.Text = "Đang tìm...";

                            _ = LyricsService.Instance.LoadLyricsAsync(_currentTrackTitle, _currentArtist, _currentDuration.TotalSeconds);
                        }

                        if (s.AdaptiveColorGlowEnabled)
                        {
                            if (track.Thumbnail != null)
                            {
                                var palette = ColorExtractorService.Instance.ExtractColors(track.Thumbnail);
                                ApplyAdaptiveColors(palette, animate: true);
                            }
                            else
                            {
                                var fallbackPalette = ColorExtractorService.Instance.DefaultPalette;
                                ApplyAdaptiveColors(fallbackPalette, animate: true);
                            }
                        }

                        if (track.IsPlaying)

                        {
                            _playbackProgressTimer.Start();

                            if (s.EqualizerEnabled)

                            {
                                AudioVisualizerService.Instance.Start();
                                AudioVisualizerService.Instance.RestartCapture();
                            }

                            if (_currentMode == IslandMode.Idle && s.AutoSwitchMedia)

                            {
                                SwitchMode(IslandMode.MediaCompact);
                            }
                        }

                        else

                        {
                            _playbackProgressTimer.Stop();

                            AudioVisualizerService.Instance.Stop();
                            ResetEqualizerBars();
                        }
                    }

                    else

                    {
                        _playbackProgressTimer.Stop();

                        AudioVisualizerService.Instance.Stop();
                        ResetEqualizerBars();
                        _currentPalette = null;
                        RestoreDefaultShadow(animate: true);

                        ResetLyricsUI("Không có trình phát nào đang mở");

                        TxtLyricsStatus.Text = "";

                        if (_currentMode != IslandMode.Expanded)

                        {
                            SwitchMode(IslandMode.Idle);
                        }
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
            Dispatcher.BeginInvoke(() =>

            {
                try

                {
                    var s = SettingsService.Instance.Current;

                    _isPlaying = isPlaying;

                    UpdatePlaybackIcon(isPlaying);

                    if (isPlaying)

                    {
                        _playbackProgressTimer.Start();

                        if (s.EqualizerEnabled)

                        {
                            AudioVisualizerService.Instance.Start();
                            AudioVisualizerService.Instance.RestartCapture();
                        }

                        if (s.AdaptiveColorGlowEnabled)
                        {
                            var palette = _currentPalette ?? ColorExtractorService.Instance.DefaultPalette;
                            ApplyAdaptiveColors(palette, animate: true);
                        }

                        if (_currentMode == IslandMode.Idle && _isMediaActive && s.AutoSwitchMedia)

                        {
                            SwitchMode(IslandMode.MediaCompact);
                        }
                    }

                    else

                    {
                        _playbackProgressTimer.Stop();

                        AudioVisualizerService.Instance.Stop();
                        ResetEqualizerBars();
                        _currentPalette = null;
                        RestoreDefaultShadow(animate: true);


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
            Dispatcher.BeginInvoke(() =>

            {
                try

                {
                    // Tránh trường hợp trình duyệt (Chrome/Edge) gửi position = 0 giữa lúc bài hát đang phát
                    if (position == TimeSpan.Zero && _currentPosition > TimeSpan.FromSeconds(2) && _isPlaying)
                    {
                        return;
                    }

                    _currentPosition = position;

                    if (duration > TimeSpan.Zero)
                    {
                        _currentDuration = duration;
                    }

                    TxtElapsed.Text = FormatTime(_currentPosition);

                    TxtDuration.Text = FormatTime(_currentDuration);

                    if (_currentDuration.TotalSeconds > 0)

                    {
                        MediaProgressBar.Value = (_currentPosition.TotalSeconds / _currentDuration.TotalSeconds) * 100.0;
                    }

                    else

                    {
                        MediaProgressBar.Value = 0;
                    }

                    // Luôn đồng bộ vị trí cho LyricsService
                    LyricsService.Instance.UpdatePlaybackPosition(_currentPosition);

                    if (_currentMode == IslandMode.ExpandedMedia)

                    {
                        UpdateActiveLyricVisuals(_currentPosition, animate: true);
                    }
                }

                catch (Exception ex)

                {
                    System.Diagnostics.Debug.WriteLine($"TimelineChanged error: {ex.Message}");
                }
            });
        }

        private void PlaybackProgressTimer_Tick(object? sender, EventArgs e)

        {
            if (!_isPlaying || !_isMediaActive) return;

            _currentPosition += TimeSpan.FromMilliseconds(250);

            if (_currentDuration > TimeSpan.Zero && _currentPosition > _currentDuration)

            {
                _currentPosition = _currentDuration;
            }

            TxtElapsed.Text = FormatTime(_currentPosition);

            if (_currentDuration.TotalSeconds > 0)

            {
                MediaProgressBar.Value = (_currentPosition.TotalSeconds / _currentDuration.TotalSeconds) * 100.0;
            }

            // Luôn đồng bộ vị trí lời bài hát cho cả thanh Compact lẫn Expanded
            LyricsService.Instance.UpdatePlaybackPosition(_currentPosition);

            if (_currentMode == IslandMode.ExpandedMedia)

            {
                UpdateActiveLyricVisuals(_currentPosition, animate: true);
            }
        }

        #region Live Lyrics & Smooth Kinetic Scrolling

        private void ResetLyricsUI(string message)

        {
            _lyricTextBlocks.Clear();

            LyricsScrollPanel.Children.Clear();

            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            LyricsTranslate.Y = 0;

            _currentActiveLyricIndex = -1;

            TxtLyricsEmptyMessage.Text = message;

            PanelLyricsEmpty.Visibility = Visibility.Visible;

            LyricsScrollPanel.Visibility = Visibility.Collapsed;
        }

        private void LyricsService_LyricsLoaded(SongLyrics lyrics)

        {
            Dispatcher.BeginInvoke(() =>

            {
                RefreshLyricsUI();
            });
        }

        private void LyricsService_ActiveLinesChanged(LyricLine? prev, LyricLine? current, LyricLine? next)

        {
            Dispatcher.BeginInvoke(() =>

            {
                if (current != null && !string.IsNullOrWhiteSpace(current.Text))

                {
                    if (_isMediaActive && _isPlaying)

                    {
                        string lyricText = current.Text == "•••" ? _currentTrackTitle : $"♪ {current.Text}";

                        UpdateCompactLyricsWithFade(lyricText);
                    }
                }

                else

                {
                    if (_isMediaActive && !string.IsNullOrWhiteSpace(_currentTrackTitle))

                    {
                        UpdateCompactLyricsWithFade(_currentTrackTitle);
                    }
                }
            });
        }

        private void RefreshLyricsUI()

        {
            var lyrics = LyricsService.Instance.CurrentLyrics;

            if (lyrics != null && lyrics.HasSyncedLyrics)

            {
                PanelLyricsEmpty.Visibility = Visibility.Collapsed;

                LyricsScrollPanel.Visibility = Visibility.Visible;

                TxtLyricsStatus.Text = "LRCLIB • Đã khớp";

                TxtLyricsStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0xB9, 0x54));

                if (_lyricTextBlocks.Count != lyrics.Lines.Count)
                {
                    BuildLyricsVisuals(lyrics);
                }

                UpdateActiveLyricVisuals(_currentPosition, animate: false);
            }

            else if (lyrics != null && !string.IsNullOrWhiteSpace(lyrics.PlainLyrics))

            {
                PanelLyricsEmpty.Visibility = Visibility.Collapsed;

                LyricsScrollPanel.Visibility = Visibility.Visible;

                TxtLyricsStatus.Text = "LRCLIB • Lời thường";

                TxtLyricsStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));

                BuildPlainLyricsVisuals(lyrics.PlainLyrics);

                TxtMiniTitle.Text = _currentTrackTitle;
            }

            else

            {
                ResetLyricsUI("Không có lời bài hát đồng bộ");

                TxtLyricsStatus.Text = "";

                TxtMiniTitle.Text = _currentTrackTitle;
            }
        }

        private void BuildLyricsVisuals(SongLyrics lyrics)

        {
            _lyricTextBlocks.Clear();

            LyricsScrollPanel.Children.Clear();

            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            LyricsTranslate.Y = 0;

            _currentActiveLyricIndex = -1;

            if (lyrics == null || lyrics.Lines.Count == 0) return;

            if (LyricsContainer.ActualWidth > 0)
            {
                LyricsScrollPanel.Width = LyricsContainer.ActualWidth;
            }

            // Đệm đầu (top spacer: 44px) để câu đầu tiên hiển thị tự nhiên trong vùng đọc chính

            var topSpacer = new FrameworkElement { Height = 44 };

            LyricsScrollPanel.Children.Add(topSpacer);

            for (int i = 0; i < lyrics.Lines.Count; i++)

            {
                var line = lyrics.Lines[i];

                var tb = new TextBlock

                {
                    Text = line.Text,

                    FontSize = 13.5,

                    FontWeight = FontWeights.Normal,

                    Foreground = System.Windows.Media.Brushes.White,

                    Opacity = 0.38,

                    TextWrapping = TextWrapping.Wrap,

                    TextAlignment = TextAlignment.Left,

                    Margin = new Thickness(0, 5, 0, 5),

                    Cursor = System.Windows.Input.Cursors.Hand,

                    Tag = i
                };

                int lineIndex = i;

                tb.MouseLeftButtonUp += (s, e) => SeekToLyricLine(lineIndex);

                _lyricTextBlocks.Add(tb);

                LyricsScrollPanel.Children.Add(tb);
            }

            // Đệm đáy (bottom spacer: 120px) để các câu cuối cùng luôn có thể cuộn lên vùng đọc chính

            var bottomSpacer = new FrameworkElement { Height = 120 };

            LyricsScrollPanel.Children.Add(bottomSpacer);
        }

        private void BuildPlainLyricsVisuals(string plainLyrics)

        {
            _lyricTextBlocks.Clear();

            LyricsScrollPanel.Children.Clear();

            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            LyricsTranslate.Y = 0;

            _currentActiveLyricIndex = -1;

            var lines = plainLyrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)

            {
                var tb = new TextBlock

                {
                    Text = line,

                    FontSize = 13.0,

                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBB, 0xBB, 0xC4)),

                    Opacity = 0.8,

                    TextWrapping = TextWrapping.Wrap,

                    Margin = new Thickness(0, 4, 0, 4)
                };

                LyricsScrollPanel.Children.Add(tb);
            }
        }

        private void LyricsContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && LyricsScrollPanel != null)
            {
                LyricsScrollPanel.Width = e.NewSize.Width;
            }
        }

        private void LyricsContainer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (LyricsScrollPanel == null || LyricsScrollPanel.Children.Count == 0) return;

            e.Handled = true;
            double delta = e.Delta > 0 ? 38.0 : -38.0;
            double currentY = (double)LyricsTranslate.GetValue(TranslateTransform.YProperty);
            if (double.IsNaN(currentY)) currentY = 0;

            double targetY = currentY + delta;
            double minOffset = -Math.Max(0, LyricsScrollPanel.ActualHeight - 120);
            targetY = Math.Max(minOffset, Math.Min(40.0, targetY));

            var anim = new DoubleAnimation(currentY, targetY, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private double GetLyricLineCenterY(int index)

        {
            if (index < 0 || index >= _lyricTextBlocks.Count) return 40.0;

            // Tính toán vị trí tâm dòng chính xác tuyệt đối dựa theo layout StackPanel

            // Đệm đầu topSpacer: 44px

            double y = 44.0;

            for (int i = 0; i < index; i++)

            {
                var tb = _lyricTextBlocks[i];

                double h = tb.ActualHeight > 0 ? tb.ActualHeight : (tb.FontSize > 15 ? 22.0 : 18.0);

                y += h + tb.Margin.Top + tb.Margin.Bottom;
            }

            var targetTb = _lyricTextBlocks[index];

            double targetH = targetTb.ActualHeight > 0 ? targetTb.ActualHeight : (targetTb.FontSize > 15 ? 22.0 : 18.0);

            return y + targetTb.Margin.Top + (targetH / 2.0);
        }

        private void UpdateActiveLyricVisuals(TimeSpan position, bool animate = true)

        {
            var lyrics = LyricsService.Instance.CurrentLyrics;

            if (lyrics == null || !lyrics.HasSyncedLyrics || _lyricTextBlocks.Count == 0) return;

            var lines = lyrics.Lines;

            int newActiveIndex = -1;

            for (int i = 0; i < lines.Count; i++)

            {
                if (lines[i].Timestamp <= position)

                {
                    newActiveIndex = i;
                }

                else

                {
                    break;
                }
            }

            // Nếu đang trong khúc dạo đầu trước khi câu đầu tiên bắt đầu

            if (newActiveIndex == -1)

            {
                if (_currentActiveLyricIndex != -1)

                {
                    _currentActiveLyricIndex = -1;

                    ScrollToLyricIndex(0, animate);
                }

                else if (_lyricTextBlocks.Count > 0 && Math.Abs(LyricsTranslate.Y) < 0.001)

                {
                    ScrollToLyricIndex(0, false);
                }

                return;
            }

            if (newActiveIndex == _currentActiveLyricIndex) return;

            int oldIndex = _currentActiveLyricIndex;

            _currentActiveLyricIndex = newActiveIndex;

            // 1. Hoạt ảnh dòng cũ: thu nhỏ mượt mà về 13.5px, độ mờ 0.38

            if (oldIndex >= 0 && oldIndex < _lyricTextBlocks.Count)

            {
                var oldTb = _lyricTextBlocks[oldIndex];

                if (animate)

                {
                    var animSize = new DoubleAnimation(oldTb.FontSize, 13.5, TimeSpan.FromMilliseconds(350))

                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    var animOpacity = new DoubleAnimation(oldTb.Opacity, 0.38, TimeSpan.FromMilliseconds(350))

                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    oldTb.BeginAnimation(TextBlock.FontSizeProperty, animSize);

                    oldTb.BeginAnimation(UIElement.OpacityProperty, animOpacity);
                }

                else

                {
                    oldTb.BeginAnimation(TextBlock.FontSizeProperty, null);

                    oldTb.BeginAnimation(UIElement.OpacityProperty, null);

                    oldTb.FontSize = 13.5;

                    oldTb.Opacity = 0.38;
                }

                oldTb.FontWeight = FontWeights.Normal;

                oldTb.Effect = null;
            }

            // 2. Hoạt ảnh dòng mới: phóng to mượt mà lên 16.5px, trắng sáng 1.0, quầng sáng mềm

            if (newActiveIndex >= 0 && newActiveIndex < _lyricTextBlocks.Count)

            {
                var newTb = _lyricTextBlocks[newActiveIndex];

                var targetLine = lines[newActiveIndex];

                if (animate)

                {
                    var animSize = new DoubleAnimation(newTb.FontSize, 16.5, TimeSpan.FromMilliseconds(320))

                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    var animOpacity = new DoubleAnimation(newTb.Opacity, 1.0, TimeSpan.FromMilliseconds(350))

                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    newTb.BeginAnimation(TextBlock.FontSizeProperty, animSize);

                    newTb.BeginAnimation(UIElement.OpacityProperty, animOpacity);
                }

                else

                {
                    newTb.BeginAnimation(TextBlock.FontSizeProperty, null);

                    newTb.BeginAnimation(UIElement.OpacityProperty, null);

                    newTb.FontSize = 16.5;

                    newTb.Opacity = 1.0;
                }

                newTb.FontWeight = FontWeights.Bold;

                newTb.Effect = new DropShadowEffect

                {
                    Color = System.Windows.Media.Colors.White,

                    BlurRadius = 10,

                    ShadowDepth = 0,

                    Opacity = 0.35
                };

                // Cập nhật lời bài hát trên thanh Compact View

                if (_isMediaActive && _isPlaying)

                {
                    string lyricText = targetLine.Text == "•••" ? _currentTrackTitle : $"♪ {targetLine.Text}";

                    UpdateCompactLyricsWithFade(lyricText);
                }

                // Cuộn mượt mà đưa dòng này vào chính giữa LyricsContainer

                ScrollToLyricIndex(newActiveIndex, animate);
            }
        }

        private void ScrollToLyricIndex(int index, bool animate)

        {
            if (index < 0 || index >= _lyricTextBlocks.Count) return;

            double itemCenterY = GetLyricLineCenterY(index);

            double containerHeight = (LyricsContainer.ActualHeight > 0) ? LyricsContainer.ActualHeight : 160.0;

            // Vùng tiêu điểm chuẩn Apple Music / Spotify: đặt dòng đang phát ở khoảng 40% chiều cao khung nhìn

            // Giúp luôn thấy 1 dòng đã qua ở trên và 2-3 dòng sắp tới ở dưới, tạo trải nghiệm cuộn liền mạch

            double focalY = containerHeight * 0.40;

            double targetTranslateY = focalY - itemCenterY;

            if (animate)

            {
                double currentY = (double)LyricsTranslate.GetValue(TranslateTransform.YProperty);

                if (double.IsNaN(currentY)) currentY = 0;

                if (Math.Abs(currentY - targetTranslateY) > 0.5)

                {
                    var animScroll = new DoubleAnimation

                    {
                        From = currentY,

                        To = targetTranslateY,

                        Duration = TimeSpan.FromMilliseconds(380),

                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    animScroll.Completed += (s, e) =>

                    {
                        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

                        LyricsTranslate.Y = targetTranslateY;
                    };

                    LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, animScroll);
                }
            }

            else

            {
                LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

                LyricsTranslate.Y = targetTranslateY;
            }

            // Nếu phần tử chưa hoàn thành layout pass (chưa render lần đầu), sau khi Loaded hãy căn chỉnh lại

            if (_lyricTextBlocks[index].ActualHeight == 0)

            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>

                {
                    if (_currentActiveLyricIndex == index)

                    {
                        ScrollToLyricIndex(index, animate: false);
                    }
                }));
            }
        }

        private void UpdateCompactLyricsWithFade(string newText)

        {
            if (TxtMiniTitle == null || _lastMiniTitle == newText) return;

            _lastMiniTitle = newText;

            var fadeOut = new DoubleAnimation(TxtMiniTitle.Opacity, 0.2, TimeSpan.FromMilliseconds(100));

            fadeOut.Completed += (s, e) =>

            {
                TxtMiniTitle.Text = newText;

                var fadeIn = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(150));

                TxtMiniTitle.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };

            TxtMiniTitle.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private async void SeekToLyricLine(int index)

        {
            try

            {
                var lyrics = LyricsService.Instance.CurrentLyrics;

                if (lyrics != null && index >= 0 && index < lyrics.Lines.Count)

                {
                    var targetTime = lyrics.Lines[index].Timestamp;

                    _currentPosition = targetTime;

                    TxtElapsed.Text = FormatTime(targetTime);

                    if (_currentDuration.TotalSeconds > 0)

                    {
                        MediaProgressBar.Value = (_currentPosition.TotalSeconds / _currentDuration.TotalSeconds) * 100.0;
                    }

                    UpdateActiveLyricVisuals(targetTime, animate: true);

                    await _mediaService.ChangePlaybackPositionAsync(targetTime);
                }
            }

            catch (Exception ex)

            {
                System.Diagnostics.Debug.WriteLine($"SeekToLyricLine error: {ex.Message}");
            }
        }

        #endregion

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

        #endregion

        #region Audio Visualizer & Adaptive Chameleon Glow

        private void InitializeVisualizerAndGlow()
        {
            _waveStop0 = new GradientStop(Color.FromRgb(0x1D, 0xB9, 0x54), 0.0);
            _waveStop1 = new GradientStop(Color.FromRgb(0x60, 0xCD, 0xFF), 1.0);
            _waveGradientBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1),
                EndPoint = new Point(0, 0)
            };
            _waveGradientBrush.GradientStops.Add(_waveStop0);
            _waveGradientBrush.GradientStops.Add(_waveStop1);

            WaveBar1.Fill = _waveGradientBrush;
            WaveBar2.Fill = _waveGradientBrush;
            WaveBar3.Fill = _waveGradientBrush;
            WaveBar4.Fill = _waveGradientBrush;

            ResetEqualizerBars();

            AudioVisualizerService.Instance.WaveformUpdated += AudioVisualizer_WaveformUpdated;
            AudioVisualizerService.Instance.BeatDetected += AudioVisualizer_BeatDetected;
            AudioVisualizerService.Instance.IsEnabled = SettingsService.Instance.Current.EqualizerEnabled;
        }

        private void AudioVisualizer_WaveformUpdated(float b1, float b2, float b3, float b4)
        {
            var s = SettingsService.Instance.Current;
            if (!s.EqualizerEnabled || !_isMediaActive || !_isPlaying)
            {
                ResetEqualizerBars();
                return;
            }

            const double minH = 3.0;
            const double maxH = 14.0;

            WaveBar1.Height = minH + (b1 * (maxH - minH));
            WaveBar2.Height = minH + (b2 * (maxH - minH));
            WaveBar3.Height = minH + (b3 * (maxH - minH));
            WaveBar4.Height = minH + (b4 * (maxH - minH));
        }

        private void AudioVisualizer_BeatDetected()
        {
            if (!_isPlaying || _currentMode != IslandMode.MediaCompact) return;

            var pulseAnimation = new DoubleAnimation
            {
                From = 1.06,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            MiniThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnimation);
            MiniThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnimation);
        }

        private void ResetEqualizerBars()
        {
            WaveBar1.Height = 3.0;
            WaveBar2.Height = 3.0;
            WaveBar3.Height = 3.0;
            WaveBar4.Height = 3.0;
        }

        private void ApplyAdaptiveColors(ExtractedColorPalette palette, bool animate = true)
        {
            _currentPalette = palette;
            var s = SettingsService.Instance.Current;
            if (!s.AdaptiveColorGlowEnabled)
            {
                RestoreDefaultShadow(animate);
                return;
            }

            TimeSpan duration = animate ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

            if (IslandShadow != null)
            {
                IslandShadow.Background = new SolidColorBrush(Colors.Black);
                IslandShadow.Visibility = s.EnableShadow ? Visibility.Visible : Visibility.Collapsed;
            }

            if (IslandDropShadow != null)
            {
                IslandDropShadow.BlurRadius = 24;
                IslandDropShadow.ShadowDepth = 0;
                IslandDropShadow.Direction = 270;
                IslandDropShadow.Opacity = 0.55;

                IslandDropShadow.BeginAnimation(DropShadowEffect.ColorProperty, null);
                if (animate)
                {
                    var shadowAnim = new ColorAnimation
                    {
                        To = palette.DominantColor,
                        Duration = duration,
                        EasingFunction = ease
                    };
                    IslandDropShadow.BeginAnimation(DropShadowEffect.ColorProperty, shadowAnim);
                }
                else
                {
                    IslandDropShadow.Color = palette.DominantColor;
                }
            }

            if (IslandContainer != null)
            {
                var neonBorder = Color.FromArgb(75, palette.DominantColor.R, palette.DominantColor.G, palette.DominantColor.B);
                if (IslandContainer.BorderBrush is SolidColorBrush solidBorder && !solidBorder.IsFrozen)
                {
                    if (animate)
                    {
                        var borderAnim = new ColorAnimation
                        {
                            To = neonBorder,
                            Duration = duration,
                            EasingFunction = ease
                        };
                        solidBorder.BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);
                    }
                    else
                    {
                        solidBorder.BeginAnimation(SolidColorBrush.ColorProperty, null);
                        solidBorder.Color = neonBorder;
                    }
                }
                else
                {
                    IslandContainer.BorderBrush = new SolidColorBrush(neonBorder);
                }
            }

            if (_waveStop0 != null && _waveStop1 != null)
            {
                if (animate)
                {
                    var stop0Anim = new ColorAnimation
                    {
                        To = palette.DominantColor,
                        Duration = duration,
                        EasingFunction = ease
                    };
                    var stop1Anim = new ColorAnimation
                    {
                        To = palette.SecondaryColor,
                        Duration = duration,
                        EasingFunction = ease
                    };
                    _waveStop0.BeginAnimation(GradientStop.ColorProperty, stop0Anim);
                    _waveStop1.BeginAnimation(GradientStop.ColorProperty, stop1Anim);
                }
                else
                {
                    _waveStop0.BeginAnimation(GradientStop.ColorProperty, null);
                    _waveStop1.BeginAnimation(GradientStop.ColorProperty, null);
                    _waveStop0.Color = palette.DominantColor;
                    _waveStop1.Color = palette.SecondaryColor;
                }
            }

            if (MediaProgressBar != null)
            {
                if (MediaProgressBar.Foreground is SolidColorBrush solidBrush && !solidBrush.IsFrozen)
                {
                    if (animate)
                    {
                        var progressAnim = new ColorAnimation
                        {
                            To = palette.DominantColor,
                            Duration = duration,
                            EasingFunction = ease
                        };
                        solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, progressAnim);
                    }
                    else
                    {
                        solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                        solidBrush.Color = palette.DominantColor;
                    }
                }
                else
                {
                    MediaProgressBar.Foreground = new SolidColorBrush(palette.DominantColor);
                }
            }
        }

        private void RestoreDefaultShadow(bool animate = true)
        {
            var s = SettingsService.Instance.Current;
            var defaultColor = Colors.Black;

            if (IslandDropShadow != null)
            {
                if (animate)
                {
                    var anim = new ColorAnimation
                    {
                        To = defaultColor,
                        Duration = TimeSpan.FromMilliseconds(500),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };
                    anim.Completed += (s_ev, e_ev) =>
                    {
                        IslandDropShadow.BlurRadius = s.ShadowBlur;
                        IslandDropShadow.ShadowDepth = 3;
                        IslandDropShadow.Direction = 270;
                        IslandDropShadow.Opacity = s.ShadowOpacity;
                    };
                    IslandDropShadow.BeginAnimation(DropShadowEffect.ColorProperty, anim);
                }
                else
                {
                    IslandDropShadow.BeginAnimation(DropShadowEffect.ColorProperty, null);
                    IslandDropShadow.Color = defaultColor;
                    IslandDropShadow.BlurRadius = s.ShadowBlur;
                    IslandDropShadow.ShadowDepth = 3;
                    IslandDropShadow.Direction = 270;
                    IslandDropShadow.Opacity = s.ShadowOpacity;
                }
            }

            if (IslandContainer != null)
            {
                var defaultBorderColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(s.BorderColor);
                if (IslandContainer.BorderBrush is SolidColorBrush solidBorder && !solidBorder.IsFrozen)
                {
                    solidBorder.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    solidBorder.Color = defaultBorderColor;
                }
                else
                {
                    IslandContainer.BorderBrush = new SolidColorBrush(defaultBorderColor);
                }
            }

            if (_waveStop0 != null && _waveStop1 != null)
            {
                _waveStop0.BeginAnimation(GradientStop.ColorProperty, null);
                _waveStop1.BeginAnimation(GradientStop.ColorProperty, null);
                _waveStop0.Color = Color.FromRgb(0x1D, 0xB9, 0x54);
                _waveStop1.Color = Color.FromRgb(0x60, 0xCD, 0xFF);
            }

            if (MediaProgressBar != null)
            {
                if (MediaProgressBar.Foreground is SolidColorBrush progressBrush && !progressBrush.IsFrozen)
                {
                    progressBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    progressBrush.Color = Color.FromRgb(0x1D, 0xB9, 0x54);
                }
                else
                {
                    MediaProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
                }
            }
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

            if (SearchView != null) SearchView.Visibility = Visibility.Collapsed;

            switch (newMode)

            {
                case IslandMode.Idle:

                    bool idlePrivActive = (PrivacyService.Instance.IsMicrophoneInUse || PrivacyService.Instance.IsCameraInUse) && s.EnablePrivacyIndicators;

                    targetWidth = idlePrivActive ? Math.Max(s.IdleWidth + 35, 225) : s.IdleWidth;

                    targetHeight = s.IdleHeight;

                    targetRadius = s.CornerRadius;

                    CompactIdleView.Visibility = Visibility.Visible;

                    break;

                case IslandMode.MediaCompact:

                    targetWidth = Math.Max(s.IdleWidth + 120, 310);

                    targetHeight = s.IdleHeight;

                    targetRadius = s.CornerRadius;

                    CompactMediaView.Visibility = Visibility.Visible;

                    break;

                case IslandMode.Expanded:

                    bool expPrivActive = (PrivacyService.Instance.IsMicrophoneInUse || PrivacyService.Instance.IsCameraInUse) && s.EnablePrivacyIndicators;

                    targetWidth = expPrivActive ? Math.Max(s.ExpandedWidth, 400) + 55 : Math.Max(s.ExpandedWidth, 400);

                    targetHeight = 180;

                    targetRadius = 26;

                    ExpandedControlCenterView.Visibility = Visibility.Visible;

                    var fadeInControl = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(200));

                    ExpandedControlCenterView.BeginAnimation(UIElement.OpacityProperty, fadeInControl);

                    UpdateControlCenterContent();

                    break;

                case IslandMode.ExpandedMedia:

                    bool expMediaPrivActive = (PrivacyService.Instance.IsMicrophoneInUse || PrivacyService.Instance.IsCameraInUse) && s.EnablePrivacyIndicators;

                    // Tự động kéo giãn đảo (Dynamic Morphing) sang 680px (hoặc 730px khi có privacy) và cao 225px để bố cục 2 cột Lyrics & Song Info to rõ, vừa vặn không bị lệch

                    targetWidth = expMediaPrivActive ? 730 : 680;

                    targetHeight = 225;

                    targetRadius = 28;

                    ExpandedMediaView.Visibility = Visibility.Visible;

                    var fadeInMedia = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(200));

                    ExpandedMediaView.BeginAnimation(UIElement.OpacityProperty, fadeInMedia);

                    try { System.IO.File.AppendAllText(@"C:\Users\TuVo\Desktop\Islands-For-Windows\lyrics_debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] Switched to ExpandedMedia. CurrentMode={_currentMode}, isMediaActive={_isMediaActive}, track={_currentTrackTitle}\n"); } catch { }

                    RefreshLyricsUI();

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

                case IslandMode.Search:

                    targetWidth = 440;

                    targetHeight = _currentSearchResults.Count == 0 ? 48 : (72 + (_currentSearchResults.Count * 48));

                    targetRadius = _currentSearchResults.Count == 0 ? 24 : 20;

                    if (SearchView != null)

                    {
                        SearchView.Visibility = Visibility.Visible;

                        var fadeInSearch = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(180));

                        SearchView.BeginAnimation(UIElement.OpacityProperty, fadeInSearch);
                    }

                    break;

                default:

                    targetWidth = s.IdleWidth;

                    targetHeight = s.IdleHeight;

                    targetRadius = s.CornerRadius;

                    CompactIdleView.Visibility = Visibility.Visible;

                    break;
            }

            AnimateIsland(targetWidth, targetHeight, targetRadius);

            // Bật Hook bắt sự kiện click ra ngoài khi mở Expanded View hoặc Search Mode

            if (newMode == IslandMode.Expanded || newMode == IslandMode.ExpandedMedia || newMode == IslandMode.Search)

            {
                StartOutsideClickHook();
            }

            else

            {
                StopOutsideClickHook();
            }
        }

        private void AnimateIsland(double width, double height, double cornerRadius)

        {
            var duration = TimeSpan.FromMilliseconds(320);

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var animWidth = new DoubleAnimation { To = width, Duration = duration, EasingFunction = easing };

            var animHeight = new DoubleAnimation { To = height, Duration = duration, EasingFunction = easing };

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

        private static bool IsInteractiveControl(DependencyObject? obj)

        {
            while (obj != null)

            {
                if (obj is System.Windows.Controls.Slider ||

                    obj is System.Windows.Controls.Primitives.Thumb ||

                    obj is System.Windows.Controls.Primitives.RepeatButton ||

                    obj is System.Windows.Controls.Primitives.ButtonBase ||

                    obj is System.Windows.Controls.TextBox ||

                    obj is System.Windows.Controls.ProgressBar)

                {
                    return true;
                }

                if (obj is FrameworkElement fe)

                {
                    if (fe.Name == "MicPrivacyDot" ||

                        fe.Name == "CameraPrivacyDot" ||

                        fe.Name == "ExpPrivacyBadge" ||

                        fe.Name == "SliderExpVolume" ||

                        fe.Name == "SliderExpBrightness" ||

                        fe.Name == "ExpandedCardVolume" ||

                        fe.Name == "ExpandedCardBrightness" ||
                        fe.Name == "HudProgressBar" ||
                        fe.Name == "SliderHudView" ||
                        fe.Name == "BtnExpVolumeIcon" ||
                        fe.Name == "BtnExpBrightnessIcon" ||
                        fe.Name == "MediaProgressBar" ||

                        fe.Tag?.ToString() == "Interactive")

                    {
                        return true;
                    }

                    if (fe.Name == "IslandContainer")

                    {
                        break;
                    }
                }

                if (obj is Window)

                {
                    break;
                }

                if (obj is Visual || obj is System.Windows.Media.Media3D.Visual3D)

                {
                    obj = VisualTreeHelper.GetParent(obj);
                }

                else

                {
                    obj = LogicalTreeHelper.GetParent(obj);
                }
            }

            return false;
        }

        private void SaveCurrentPositionAfterDrag()

        {
            try

            {
                if (_screens.Length == 0 || _currentScreenIndex < 0 || _currentScreenIndex >= _screens.Length)

                    return;

                var screen = _screens[_currentScreenIndex];

                var source = PresentationSource.FromVisual(this);

                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                int physicalWidth = (int)(this.ActualWidth * dpiX);

                int physicalLeft = (int)(this.Left * dpiX);

                int physicalTop = (int)(this.Top * dpiY);

                int centerBaseX = screen.Bounds.X + (screen.Bounds.Width - physicalWidth) / 2;

                double newHorizontalOffset = (physicalLeft - centerBaseX) / dpiX;

                double newTopOffset = Math.Max(0, (physicalTop - screen.Bounds.Y) / dpiY);

                SettingsService.Instance.Update(s =>

                {
                    s.HorizontalOffset = Math.Round(newHorizontalOffset);

                    s.TopOffset = Math.Round(newTopOffset);
                });
            }

            catch (Exception ex)

            {
                System.Diagnostics.Debug.WriteLine($"SaveCurrentPositionAfterDrag error: {ex.Message}");
            }
        }

        private void IslandContainer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)

        {
            if (_isFileTransferInProgress || _currentMode == IslandMode.FileTransferring) return;

            _isInteractingWithControl = IsInteractiveControl(e.OriginalSource as DependencyObject);

            _dragAttempted = false;

            _isDragging = false;

            if (_isInteractingWithControl)

            {
                return;
            }

            if (_currentMode == IslandMode.SliderHud) return;

            _startPoint = e.GetPosition(this);
        }

        private void IslandContainer_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)

        {
            if (_isInteractingWithControl) return;

            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)

            {
                Point currentPoint = e.GetPosition(this);

                Vector diff = _startPoint - currentPoint;

                // Nếu đang ở Expanded View: Nhận diện cử chỉ vuốt ngang để chuyển tab

                if (_currentMode == IslandMode.Expanded || _currentMode == IslandMode.ExpandedMedia)

                {
                    if (Math.Abs(diff.X) > 40 && Math.Abs(diff.X) > Math.Abs(diff.Y) * 1.3)

                    {
                        if ((DateTime.Now - _lastViewSwitchTime).TotalMilliseconds > 350)

                        {
                            _lastViewSwitchTime = DateTime.Now;

                            SwitchExpandedView();

                            _dragAttempted = false;

                            return;
                        }
                    }
                }

                if (Math.Abs(diff.X) > 12 || Math.Abs(diff.Y) > 12)

                {
                    _dragAttempted = true;

                    var s = SettingsService.Instance.Current;

                    if (s.LockPosition)

                    {
                        // Vị trí đã bị khóa -> không cho phép kéo di chuyển cửa sổ

                        return;
                    }

                    // Không kéo di chuyển cửa sổ khi đang ở Expanded View để ưu tiên cử chỉ vuốt và tương tác UI

                    if (_currentMode == IslandMode.Expanded || _currentMode == IslandMode.ExpandedMedia)

                    {
                        return;
                    }

                    _isDragging = true;

                    try

                    {
                        DragMove();
                    }

                    catch

                    {
                        // Bỏ qua lỗi ngắt DragMove khi nhả chuột
                    }

                    finally

                    {
                        _isDragging = false;

                        SaveCurrentPositionAfterDrag();
                    }
                }
            }
        }

        private void IslandContainer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)

        {
            if (_isInteractingWithControl)

            {
                _isInteractingWithControl = false;

                _isDragging = false;

                _dragAttempted = false;

                return;
            }

            if (!_isDragging && !_dragAttempted)

            {
                if (_isFileTransferInProgress ||

                    _currentMode == IslandMode.FileTransferring ||

                    _currentMode == IslandMode.FileResult ||

                    _currentMode == IslandMode.SliderHud)

                {
                    _isDragging = false;

                    _dragAttempted = false;

                    return;
                }

                // Click hợp lệ: Mở rộng Media Player nếu có media, ngược lại mở Control Center

                if (_currentMode != IslandMode.Expanded && _currentMode != IslandMode.ExpandedMedia)

                {
                    SwitchMode(_isMediaActive ? IslandMode.ExpandedMedia : IslandMode.Expanded);
                }
            }

            _isDragging = false;

            _dragAttempted = false;
        }

        #region Quick Search & App Launcher

        private void RegisterSearchHotkey()

        {
            try

            {
                if (_isSearchHotkeyRegistered) return;

                var helper = new WindowInteropHelper(this);

                if (helper.Handle == IntPtr.Zero) return;

                // MOD_ALT = 0x0001, VK_SPACE = 0x0020

                bool success = Win32Helper.RegisterHotKey(helper.Handle, HOTKEY_ID_SEARCH, 0x0001, 0x0020);

                _isSearchHotkeyRegistered = success;

                System.Diagnostics.Debug.WriteLine($"Search hotkey Alt+Space registered: {success}");
            }

            catch (Exception ex)

            {
                System.Diagnostics.Debug.WriteLine($"RegisterSearchHotkey error: {ex.Message}");
            }
        }

        private void UnregisterSearchHotkey()

        {
            try

            {
                if (!_isSearchHotkeyRegistered) return;

                var helper = new WindowInteropHelper(this);

                if (helper.Handle != IntPtr.Zero)

                {
                    Win32Helper.UnregisterHotKey(helper.Handle, HOTKEY_ID_SEARCH);
                }

                _isSearchHotkeyRegistered = false;
            }

            catch (Exception ex)

            {
                System.Diagnostics.Debug.WriteLine($"UnregisterSearchHotkey error: {ex.Message}");
            }
        }

        public void EnterSearchMode()

        {
            if (_isSearchActive) return;

            _isSearchActive = true;

            _modeBeforeSearch = (_currentMode == IslandMode.Search) ? IslandMode.Idle : _currentMode;

            // Khóa tạm thời Auto-Hide để không bị ẩn khi người dùng đang tìm kiếm

            _autoHideTimer.Stop();

            if (_isIslandHidden)

            {
                ShowIsland(instant: true);
            }

            // Reset Search UI

            SearchTextBox.Text = "";

            TxtSearchPlaceholder.Visibility = Visibility.Visible;

            SearchDivider.Visibility = Visibility.Collapsed;

            SearchListBox.Visibility = Visibility.Collapsed;

            SearchListBox.ItemsSource = null;

            _currentSearchResults.Clear();

            SwitchMode(IslandMode.Search);

            // Kích hoạt cửa sổ và Focus thẳng vào SearchTextBox

            var helper = new WindowInteropHelper(this);

            Win32Helper.SetForegroundWindow(helper.Handle);

            this.Activate();

            SearchTextBox.Focus();

            SearchTextBox.SelectAll();
        }

        public void ExitSearchMode()

        {
            if (!_isSearchActive) return;

            _isSearchActive = false;

            // Xóa dữ liệu search và giải phóng icon

            SearchTextBox.Text = "";

            SearchListBox.ItemsSource = null;

            _currentSearchResults.Clear();

            SearchDivider.Visibility = Visibility.Collapsed;

            SearchListBox.Visibility = Visibility.Collapsed;

            // Co viên thuốc về trạng thái trước đó

            var returnMode = (_modeBeforeSearch == IslandMode.Search) ? IslandMode.Idle : _modeBeforeSearch;

            if (returnMode == IslandMode.MediaCompact && !_isMediaActive)

            {
                returnMode = IslandMode.Idle;
            }

            SwitchMode(returnMode);

            if (SettingsService.Instance.Current.AutoHide)

            {
                StartAutoHideTimer();
            }
        }

        public void ToggleSearchMode()

        {
            if (_isSearchActive)

            {
                ExitSearchMode();
            }

            else

            {
                EnterSearchMode();
            }
        }

        private void AnimateSearchHeight(int resultCount)

        {
            double targetWidth = 440;

            double targetHeight = resultCount == 0 ? 48 : (72 + (resultCount * 48));

            double targetRadius = resultCount == 0 ? 24 : 20;

            AnimateIsland(targetWidth, targetHeight, targetRadius);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)

        {
            if (!_isSearchActive) return;

            string query = SearchTextBox.Text;

            TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(query))

            {
                _currentSearchResults.Clear();

                SearchListBox.ItemsSource = null;

                SearchDivider.Visibility = Visibility.Collapsed;

                SearchListBox.Visibility = Visibility.Collapsed;

                AnimateSearchHeight(0);

                return;
            }

            var results = AppLauncherService.Instance.Search(query);

            _currentSearchResults = results;

            SearchListBox.ItemsSource = results;

            if (results.Count > 0)

            {
                SearchDivider.Visibility = Visibility.Visible;

                SearchListBox.Visibility = Visibility.Visible;

                SearchListBox.SelectedIndex = 0;

                AnimateSearchHeight(results.Count);
            }

            else

            {
                SearchDivider.Visibility = Visibility.Collapsed;

                SearchListBox.Visibility = Visibility.Collapsed;

                AnimateSearchHeight(0);
            }
        }

        private void SearchTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)

        {
            if (!_isSearchActive) return;

            if (e.Key == Key.Escape)

            {
                ExitSearchMode();

                e.Handled = true;

                return;
            }

            if (e.Key == Key.Down)

            {
                if (SearchListBox.Items.Count > 0)

                {
                    int next = SearchListBox.SelectedIndex + 1;

                    if (next >= SearchListBox.Items.Count) next = 0;

                    SearchListBox.SelectedIndex = next;

                    SearchListBox.ScrollIntoView(SearchListBox.SelectedItem);
                }

                e.Handled = true;

                return;
            }

            if (e.Key == Key.Up)

            {
                if (SearchListBox.Items.Count > 0)

                {
                    int prev = SearchListBox.SelectedIndex - 1;

                    if (prev < 0) prev = SearchListBox.Items.Count - 1;

                    SearchListBox.SelectedIndex = prev;

                    SearchListBox.ScrollIntoView(SearchListBox.SelectedItem);
                }

                e.Handled = true;

                return;
            }

            if (e.Key == Key.Enter)

            {
                bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                bool runAsAdmin = isCtrl && isShift;

                ExecuteCurrentSearchResult(runAsAdmin);

                e.Handled = true;

                return;
            }
        }

        private void ExecuteCurrentSearchResult(bool runAsAdmin = false)

        {
            var item = SearchListBox.SelectedItem as LauncherResultItem;

            if (item == null && _currentSearchResults.Count > 0)

            {
                item = _currentSearchResults[0];
            }

            if (item != null)

            {
                try

                {
                    item.ExecuteAction?.Invoke(runAsAdmin);
                }

                catch (Exception ex)

                {
                    System.Diagnostics.Debug.WriteLine($"ExecuteSearchResult error: {ex.Message}");
                }
            }

            ExitSearchMode();
        }

        private void SearchListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)

        {
            ExecuteCurrentSearchResult(runAsAdmin: false);
        }

        private void BtnCloseSearch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)

        {
            ExitSearchMode();
        }

        private void MenuSearch_Click(object sender, RoutedEventArgs e)

        {
            EnterSearchMode();
        }

        #endregion

        private void BtnCollapse_Click(object sender, RoutedEventArgs e)

        {
            CollapseToDefault();
        }

        public void CollapseToDefault()

        {
            if (_isSearchActive)

            {
                ExitSearchMode();

                return;
            }

            if (_currentMode != IslandMode.Expanded && _currentMode != IslandMode.ExpandedMedia)

                return;

            SwitchMode(_isMediaActive && SettingsService.Instance.Current.AutoSwitchMedia ? IslandMode.MediaCompact : IslandMode.Idle);

            StartAutoHideTimer();
        }

        #endregion

        #region Outside Click to Collapse (Tự động thu nhỏ khi click ra ngoài vùng Island)

        private const int WH_MOUSE_LL = 14;

        private const int WM_LBUTTONDOWN = 0x0201;

        private const int WM_RBUTTONDOWN = 0x0204;

        private const int WM_NCLBUTTONDOWN = 0x00A1;

        private const int WM_NCRBUTTONDOWN = 0x00A4;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private IntPtr _mouseHookId = IntPtr.Zero;

        private LowLevelMouseProc? _mouseProc;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private void StartOutsideClickHook()

        {
            if (_mouseHookId != IntPtr.Zero) return;

            _mouseProc = OutsideMouseHookCallback;

            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;

            if (curModule?.ModuleName != null)

            {
                _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private void StopOutsideClickHook()

        {
            if (_mouseHookId != IntPtr.Zero)

            {
                UnhookWindowsHookEx(_mouseHookId);

                _mouseHookId = IntPtr.Zero;
            }
        }

        private IntPtr OutsideMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)

        {
            if (nCode >= 0)

            {
                int msg = wParam.ToInt32();

                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_NCLBUTTONDOWN || msg == WM_NCRBUTTONDOWN)

                {
                    if (Win32Helper.GetCursorPos(out var pt))

                    {
                        if (!IsCursorOverIsland(pt) && !SettingsWindow.IsCursorOverSettings(pt))

                        {
                            Dispatcher.BeginInvoke(new Action(() =>

                            {
                                if (IslandContextMenu != null && IslandContextMenu.IsOpen)

                                    return;

                                if (_isSearchActive)

                                {
                                    ExitSearchMode();

                                    return;
                                }

                                CollapseToDefault();
                            }));
                        }
                    }
                }
            }

            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private bool IsCursorOverIsland(Win32Helper.POINT pt)

        {
            try

            {
                if (IslandContainer == null || !IslandContainer.IsVisible) return false;

                Point localPt = IslandContainer.PointFromScreen(new Point(pt.X, pt.Y));

                return localPt.X >= 0 && localPt.X <= IslandContainer.ActualWidth &&

                       localPt.Y >= 0 && localPt.Y <= IslandContainer.ActualHeight;
            }

            catch

            {
                return IslandContainer?.IsMouseOver ?? false;
            }
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
                SwitchMode(_isMediaActive ? IslandMode.ExpandedMedia : IslandMode.Expanded);
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)

        {
            System.Windows.Application.Current.Shutdown();
        }

        private void MenuLockPosition_Click(object sender, RoutedEventArgs e)

        {
            SettingsService.Instance.Update(s => s.LockPosition = MenuLockPosition.IsChecked);
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

            if (_isSearchActive) return;

            if (!IslandContainer.IsMouseOver && !IslandContextMenu.IsOpen && _currentMode != IslandMode.Expanded && _currentMode != IslandMode.ExpandedMedia && _currentMode != IslandMode.Search)

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
                        Dispatcher.BeginInvoke(() =>

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

            // 1. Kiểm tra giữ phím Shift khi đang ở Expanded View: Chuyển đổi qua lại giữa Control Center và Media Player

            bool isShift = Win32Helper.IsKeyDown(0x10) || // VK_SHIFT (Shift key toàn hệ thống)

                           Keyboard.IsKeyDown(Key.LeftShift) ||

                           Keyboard.IsKeyDown(Key.RightShift) ||

                           (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (isShift && (_currentMode == IslandMode.Expanded || _currentMode == IslandMode.ExpandedMedia))

            {
                if ((DateTime.Now - _lastViewSwitchTime).TotalMilliseconds > 300)

                {
                    _lastViewSwitchTime = DateTime.Now;

                    SwitchExpandedView();
                }

                return;
            }

            bool isAlt = Win32Helper.IsKeyDown(0x12) || // VK_MENU (Alt key toàn hệ thống)

                         Keyboard.IsKeyDown(Key.LeftAlt) ||

                         Keyboard.IsKeyDown(Key.RightAlt) ||

                         (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            bool isOverBrightness = false;

            if (SliderExpBrightness != null && (SliderExpBrightness.IsMouseOver || (ExpandedCardBrightness != null && ExpandedCardBrightness.IsMouseOver)))

            {
                isOverBrightness = true;
            }

            if (isAlt || isOverBrightness)

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

        #region Touchpad & Touch Manipulation Gestures

        private void IslandContainer_ManipulationStarting(object sender, ManipulationStartingEventArgs e)

        {
            e.ManipulationContainer = IslandContainer;

            e.Mode = ManipulationModes.TranslateX;

            e.Handled = true;
        }

        private void IslandContainer_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)

        {
            if (_currentMode == IslandMode.Expanded || _currentMode == IslandMode.ExpandedMedia)

            {
                double diffX = e.CumulativeManipulation.Translation.X;

                if (Math.Abs(diffX) > 35)

                {
                    if ((DateTime.Now - _lastViewSwitchTime).TotalMilliseconds > 320)

                    {
                        _lastViewSwitchTime = DateTime.Now;

                        SwitchExpandedView();
                    }

                    e.Handled = true;
                }
            }
        }

        private void SwitchExpandedView()

        {
            if (_currentMode == IslandMode.Expanded)

            {
                SwitchMode(IslandMode.ExpandedMedia);
            }

            else if (_currentMode == IslandMode.ExpandedMedia)

            {
                SwitchMode(IslandMode.Expanded);
            }
        }

        #endregion

        private void VolumeService_VolumeChanged(object? sender, VolumeChangedEventArgs e)

        {
            Dispatcher.BeginInvoke(() =>

            {
                UpdateExpVolumeUI(e.VolumePercent, e.IsMuted);

                if (SettingsService.Instance.Current.VolumeBrightnessHudEnabled && _currentMode != IslandMode.Expanded && _currentMode != IslandMode.ExpandedMedia && !_isInteractingWithControl)

                {
                    ShowVolumeHud(e.VolumePercent, e.IsMuted);
                }
            });
        }

        private void BrightnessService_BrightnessChanged(object? sender, BrightnessChangedEventArgs e)

        {
            Dispatcher.BeginInvoke(() =>

            {
                UpdateExpBrightnessUI(e.Brightness);

                if (SettingsService.Instance.Current.VolumeBrightnessHudEnabled && _currentMode != IslandMode.Expanded && _currentMode != IslandMode.ExpandedMedia && !_isInteractingWithControl)

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

            if (TxtExpClock != null)

            {
                TxtExpClock.Text = DateTime.Now.ToString(SettingsService.Instance.Current.Use24HourClock ? "HH:mm" : "hh:mm tt");
            }

            // Đồng bộ trạng thái Privacy và Mic Mute ngay khi mở Control Center

            UpdateControlCenterPrivacyUI(new PrivacyStatusChangedEventArgs

            {
                IsMicrophoneInUse = PrivacyService.Instance.IsMicrophoneInUse,

                MicrophoneApps = PrivacyService.Instance.MicrophoneApps,

                IsCameraInUse = PrivacyService.Instance.IsCameraInUse,

                CameraApps = PrivacyService.Instance.CameraApps,

                IsMicrophoneMuted = PrivacyService.Instance.IsMicrophoneMuted
            });
        }

        private void UpdateExpVolumeUI(int percent, bool isMuted)

        {
            if (SliderExpVolume == null) return;

            _isUpdatingSliders = true;

            try

            {
                if (!SliderExpVolume.IsMouseCaptured)

                {
                    SliderExpVolume.Value = percent;
                }

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

                if (!SliderExpBrightness.IsMouseCaptured)

                {
                    SliderExpBrightness.Value = percent;
                }

                TxtExpBrightnessPercent.Text = $"{percent}%";

                SliderExpBrightness.Foreground = sunBrush;

                TxtExpBrightnessPercent.Foreground = sunBrush;
            }

            finally

            {
                _isUpdatingSliders = false;
            }
        }

        private void UpdateSliderValueFromMouse(Slider slider, Point pos)
        {
            double thumbRadius = 12;
            double trackWidth = slider.ActualWidth - 2 * thumbRadius;
            if (trackWidth <= 0) return;
            double x = pos.X - thumbRadius;
            double ratio = Math.Clamp(x / trackWidth, 0.0, 1.0);
            double newVal = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
            slider.Value = Math.Round(newVal);
        }

        private void SliderExpVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingSliders || TxtExpVolumePercent == null) return;
            int percent = (int)Math.Round(e.NewValue);
            TxtExpVolumePercent.Text = $"{percent}%";
            VolumeService.Instance.SetVolume(percent / 100f);
        }

        private void ExpandedCardVolume_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep && IsChildOf(dep, BtnExpVolumeIcon)) return;
            if (e.OriginalSource is DependencyObject dep2 && IsChildOf(dep2, SliderExpVolume)) return;

            if (SliderExpVolume != null)
            {
                Point pos = e.GetPosition(SliderExpVolume);
                UpdateSliderValueFromMouse(SliderExpVolume, pos);
            }
        }

        private void ExpandedCardBrightness_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep && IsChildOf(dep, SliderExpBrightness)) return;

            if (SliderExpBrightness != null)
            {
                Point pos = e.GetPosition(SliderExpBrightness);
                UpdateSliderValueFromMouse(SliderExpBrightness, pos);
            }
        }

        private void HudProgressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isInteractingWithControl = true;
            if (HudProgressBar != null)
            {
                HudProgressBar.CaptureMouse();
                UpdateHudValueFromMouse(e.GetPosition(HudProgressBar));
                e.Handled = true;
            }
        }

        private void HudProgressBar_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (HudProgressBar != null && HudProgressBar.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateHudValueFromMouse(e.GetPosition(HudProgressBar));
                e.Handled = true;
            }
        }

        private void HudProgressBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (HudProgressBar != null && HudProgressBar.IsMouseCaptured)
            {
                HudProgressBar.ReleaseMouseCapture();
                _isInteractingWithControl = false;
                e.Handled = true;
            }
        }

        private void UpdateHudValueFromMouse(Point pos)
        {
            if (HudProgressBar == null || HudProgressBar.ActualWidth <= 0) return;
            double ratio = Math.Clamp(pos.X / HudProgressBar.ActualWidth, 0.0, 1.0);
            int percent = (int)Math.Round(ratio * 100);

            if (_isAdjustingBrightness)
            {
                HudProgressBar.Value = percent;
                TxtHudValue.Text = $"{percent}%";
                BrightnessService.Instance.SetBrightness(percent);
            }
            else
            {
                HudProgressBar.Value = percent;
                TxtHudValue.Text = $"{percent}%";
                VolumeService.Instance.SetVolume(percent / 100f);
            }

            _hudAutoDismissTimer.Stop();
            _hudAutoDismissTimer.Start();
        }

        private static bool IsChildOf(DependencyObject? obj, DependencyObject? parent)
        {
            while (obj != null)
            {
                if (obj == parent) return true;
                if (obj is Visual || obj is System.Windows.Media.Media3D.Visual3D)
                    obj = VisualTreeHelper.GetParent(obj);
                else
                    obj = LogicalTreeHelper.GetParent(obj);
            }
            return false;
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
            Dispatcher.BeginInvoke(() =>

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

                    // 4. Tự động co giãn kích thước Dynamic Island khi phát hiện hoặc tắt Cam / Mic

                    var s = SettingsService.Instance.Current;

                    bool isAnyPrivacyActive = (e.IsMicrophoneInUse || e.IsCameraInUse) && showPrivacy;

                    if (_currentMode == IslandMode.Expanded)

                    {
                        double targetWidth = isAnyPrivacyActive ? Math.Max(s.ExpandedWidth, 400) + 55 : Math.Max(s.ExpandedWidth, 400);

                        AnimateIsland(targetWidth, 180, 26);
                    }

                    else if (_currentMode == IslandMode.Idle)

                    {
                        double targetWidth = isAnyPrivacyActive ? Math.Max(s.IdleWidth + 35, 225) : s.IdleWidth;

                        AnimateIsland(targetWidth, s.IdleHeight, s.CornerRadius);
                    }
                }

                catch (Exception ex)

                {
                    System.Diagnostics.Debug.WriteLine($"PrivacyStatusChanged error: {ex.Message}");
                }
            });
        }

        private Storyboard? _privacyPulseStoryboard;

        private void StartPrivacyPulseAnimation()

        {
            if (_privacyPulseStoryboard == null)

            {
                _privacyPulseStoryboard = new Storyboard

                {
                    RepeatBehavior = RepeatBehavior.Forever,

                    AutoReverse = true
                };

                // Pulse 1: Chấm tròn mở rộng nhịp nhàng (Scale 0.85 -> 1.25)

                if (ExpDotScale != null)

                {
                    var scaleXAnim = new DoubleAnimation(0.85, 1.25, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(scaleXAnim, ExpDotScale);

                    Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath(ScaleTransform.ScaleXProperty));

                    _privacyPulseStoryboard.Children.Add(scaleXAnim);

                    var scaleYAnim = new DoubleAnimation(0.85, 1.25, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(scaleYAnim, ExpDotScale);

                    Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath(ScaleTransform.ScaleYProperty));

                    _privacyPulseStoryboard.Children.Add(scaleYAnim);
                }

                // Pulse 2: Chấm tròn glow phát sáng mạnh dần (Blur 6 -> 14, Opacity 0.65 -> 1.0)

                if (ExpDotGlow != null)

                {
                    var dotGlowBlur = new DoubleAnimation(6.0, 14.0, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(dotGlowBlur, ExpDotGlow);

                    Storyboard.SetTargetProperty(dotGlowBlur, new PropertyPath(DropShadowEffect.BlurRadiusProperty));

                    _privacyPulseStoryboard.Children.Add(dotGlowBlur);

                    var dotGlowOpacity = new DoubleAnimation(0.65, 1.0, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(dotGlowOpacity, ExpDotGlow);

                    Storyboard.SetTargetProperty(dotGlowOpacity, new PropertyPath(DropShadowEffect.OpacityProperty));

                    _privacyPulseStoryboard.Children.Add(dotGlowOpacity);
                }

                // Pulse 3: Aura tỏa sáng xung quanh background của ExpPrivacyBadge (Blur 10 -> 18, Opacity 0.35 -> 0.75)

                if (ExpPrivacyGlow != null)

                {
                    var badgeGlowBlur = new DoubleAnimation(10.0, 18.0, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(badgeGlowBlur, ExpPrivacyGlow);

                    Storyboard.SetTargetProperty(badgeGlowBlur, new PropertyPath(DropShadowEffect.BlurRadiusProperty));

                    _privacyPulseStoryboard.Children.Add(badgeGlowBlur);

                    var badgeGlowOpacity = new DoubleAnimation(0.35, 0.75, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(badgeGlowOpacity, ExpPrivacyGlow);

                    Storyboard.SetTargetProperty(badgeGlowOpacity, new PropertyPath(DropShadowEffect.OpacityProperty));

                    _privacyPulseStoryboard.Children.Add(badgeGlowOpacity);
                }

                // Pulse 4: Chấm xanh lá ở Compact Idle View (IdleCameraDotScale & IdleCameraDotGlow)

                if (IdleCameraDotScale != null)

                {
                    var idleScaleX = new DoubleAnimation(0.85, 1.25, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(idleScaleX, IdleCameraDotScale);

                    Storyboard.SetTargetProperty(idleScaleX, new PropertyPath(ScaleTransform.ScaleXProperty));

                    _privacyPulseStoryboard.Children.Add(idleScaleX);

                    var idleScaleY = new DoubleAnimation(0.85, 1.25, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(idleScaleY, IdleCameraDotScale);

                    Storyboard.SetTargetProperty(idleScaleY, new PropertyPath(ScaleTransform.ScaleYProperty));

                    _privacyPulseStoryboard.Children.Add(idleScaleY);
                }

                if (IdleCameraDotGlow != null)

                {
                    var idleGlowBlur = new DoubleAnimation(6.0, 13.0, TimeSpan.FromMilliseconds(900))

                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };

                    Storyboard.SetTarget(idleGlowBlur, IdleCameraDotGlow);

                    Storyboard.SetTargetProperty(idleGlowBlur, new PropertyPath(DropShadowEffect.BlurRadiusProperty));

                    _privacyPulseStoryboard.Children.Add(idleGlowBlur);
                }
            }

            try

            {
                _privacyPulseStoryboard.Begin(this, true);
            }

            catch (Exception ex)

            {
                System.Diagnostics.Debug.WriteLine($"Privacy pulse start error: {ex.Message}");
            }
        }

        private void StopPrivacyPulseAnimation()

        {
            try

            {
                _privacyPulseStoryboard?.Stop(this);
            }

            catch (Exception ex)

            {
                System.Diagnostics.Debug.WriteLine($"Privacy pulse stop error: {ex.Message}");
            }
        }

        private void UpdateControlCenterPrivacyUI(PrivacyStatusChangedEventArgs e)

        {
            if (BtnGlobalMicMute != null)

            {
                if (e.IsMicrophoneMuted)

                {
                    TxtGlobalMicIcon.Text = "\uE740"; // Mic Off / Muted

                    TxtGlobalMicIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A)); // Red #FF453A

                    BtnGlobalMicMute.ToolTip = "Microphone đang tắt (Click để Bật lại)";
                }

                else

                {
                    TxtGlobalMicIcon.Text = "\uE720"; // Mic On

                    TxtGlobalMicIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59)); // Green #34C759

                    BtnGlobalMicMute.ToolTip = "Microphone đang bật (Click để Tắt mic toàn hệ thống)";
                }
            }

            bool showPrivacy = SettingsService.Instance.Current.EnablePrivacyIndicators;

            if (ExpPrivacyBadge != null)

            {
                if ((e.IsMicrophoneInUse || e.IsCameraInUse) && showPrivacy)

                {
                    ExpPrivacyBadge.Visibility = Visibility.Visible;

                    StartPrivacyPulseAnimation();

                    if (e.IsMicrophoneInUse && e.IsCameraInUse)

                    {
                        // Cả Camera và Mic đều đang hoạt động

                        ExpPrivacyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x28, 0x34, 0xC7, 0x59));

                        ExpPrivacyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                        if (ExpPrivacyGlow != null) ExpPrivacyGlow.Color = System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59);

                        if (ExpDotGlow != null) ExpDotGlow.Color = System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59);

                        ExpPrivacyDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                        TxtExpPrivacyIcon.Text = "\uE722"; // Camera icon

                        TxtExpPrivacyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                        TxtExpPrivacyText.Text = "Cam & Mic";

                        TxtExpPrivacyText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));

                        if (TxtExpPrivacyTooltipDetail != null)

                        {
                            TxtExpPrivacyTooltipDetail.Text = $"📷 Webcam: {e.CameraSummary}\n🎤 Mic: {e.MicrophoneSummary}\n(Click để tắt/bật Micro)";
                        }

                        // Cập nhật thông tin chi tiết ở chân trang (Row 3)

                        if (ExpPrivacyFooter != null && ExpHintFooter != null)

                        {
                            ExpHintFooter.Visibility = Visibility.Collapsed;

                            ExpPrivacyFooter.Visibility = Visibility.Visible;

                            TxtExpPrivacyFooterIcon.Text = "\uE722";

                            TxtExpPrivacyFooterIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                            TxtExpPrivacyFooterText.Text = $"{e.CameraSummary}  •  🎤 {e.MicrophoneSummary}";
                        }
                    }

                    else if (e.IsCameraInUse)

                    {
                        // Chỉ có Camera đang dùng: Hiệu ứng Glow Neon Xanh Lá đẳng cấp

                        ExpPrivacyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x28, 0x34, 0xC7, 0x59));

                        ExpPrivacyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                        if (ExpPrivacyGlow != null) ExpPrivacyGlow.Color = System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59);

                        if (ExpDotGlow != null) ExpDotGlow.Color = System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59);

                        ExpPrivacyDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                        TxtExpPrivacyIcon.Text = "\uE722";

                        TxtExpPrivacyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                        TxtExpPrivacyText.Text = "Cam";

                        TxtExpPrivacyText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                        if (TxtExpPrivacyTooltipDetail != null)

                        {
                            TxtExpPrivacyTooltipDetail.Text = $"📷 Webcam: {e.CameraSummary}";
                        }

                        // Cập nhật Footer hàng 3

                        if (ExpPrivacyFooter != null && ExpHintFooter != null)

                        {
                            ExpHintFooter.Visibility = Visibility.Collapsed;

                            ExpPrivacyFooter.Visibility = Visibility.Visible;

                            TxtExpPrivacyFooterIcon.Text = "\uE722";

                            TxtExpPrivacyFooterIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

                            TxtExpPrivacyFooterText.Text = $"{e.CameraSummary} đang sử dụng Webcam";
                        }
                    }

                    else // IsMicrophoneInUse

                    {
                        // Chỉ có Microphone đang dùng: Hiệu ứng Glow Cam Hổ Phách

                        ExpPrivacyBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x28, 0xFF, 0x95, 0x00));

                        ExpPrivacyBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));

                        if (ExpPrivacyGlow != null) ExpPrivacyGlow.Color = System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00);

                        if (ExpDotGlow != null) ExpDotGlow.Color = System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00);

                        ExpPrivacyDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));

                        TxtExpPrivacyIcon.Text = "\uE720";

                        TxtExpPrivacyIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));

                        TxtExpPrivacyText.Text = "Mic";

                        TxtExpPrivacyText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));

                        if (TxtExpPrivacyTooltipDetail != null)

                        {
                            TxtExpPrivacyTooltipDetail.Text = $"🎤 Mic: {e.MicrophoneSummary}\n(Click để tắt/bật Micro)";
                        }

                        // Cập nhật Footer hàng 3

                        if (ExpPrivacyFooter != null && ExpHintFooter != null)

                        {
                            ExpHintFooter.Visibility = Visibility.Collapsed;

                            ExpPrivacyFooter.Visibility = Visibility.Visible;

                            TxtExpPrivacyFooterIcon.Text = "\uE720";

                            TxtExpPrivacyFooterIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));

                            TxtExpPrivacyFooterText.Text = $"{e.MicrophoneSummary} đang sử dụng Microphone";
                        }
                    }
                }

                else

                {
                    ExpPrivacyBadge.Visibility = Visibility.Collapsed;

                    StopPrivacyPulseAnimation();

                    if (ExpPrivacyFooter != null && ExpHintFooter != null)

                    {
                        ExpPrivacyFooter.Visibility = Visibility.Collapsed;

                        ExpHintFooter.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private void ExpPrivacyBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)

        {
            e.Handled = true;

            if (PrivacyService.Instance.IsMicrophoneInUse)

            {
                bool newMuted = PrivacyService.Instance.ToggleMicrophoneMute();

                ShowMicToast(newMuted);
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
            Dispatcher.BeginInvoke(() =>

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

            AudioVisualizerService.Instance.Dispose();
        }
    }
}