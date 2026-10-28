using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Forms;
using DynamicIsland.Services;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace DynamicIsland
{
    public partial class SettingsWindow : Window
    {
        private static SettingsWindow? _instance;
        private bool _isInitializing = true;

        public static void ShowOrActivate()
        {
            if (_instance == null || !_instance.IsLoaded)
            {
                _instance = new SettingsWindow();
                _instance.Closed += (s, e) => _instance = null;
                _instance.Show();
            }
            else
            {
                if (_instance.WindowState == WindowState.Minimized)
                {
                    _instance.WindowState = WindowState.Normal;
                }
                _instance.Activate();
                _instance.Focus();
            }
        }

        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadMonitors();
            LoadCurrentSettings();
            _isInitializing = false;
        }

        private void LoadMonitors()
        {
            CmbMonitors.Items.Clear();
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                string name = $"Màn hình {i + 1} ({s.Bounds.Width}x{s.Bounds.Height})";
                if (s.Primary) name += " [Chính]";
                CmbMonitors.Items.Add(name);
            }

            var settings = SettingsService.Instance.Current;
            if (settings.MonitorIndex >= 0 && settings.MonitorIndex < screens.Length)
            {
                CmbMonitors.SelectedIndex = settings.MonitorIndex;
            }
            else if (screens.Length > 0)
            {
                CmbMonitors.SelectedIndex = 0;
            }
        }

        private void LoadCurrentSettings()
        {
            var s = SettingsService.Instance.Current;

            // Display
            SliderTopOffset.Value = s.TopOffset;
            LblTopOffset.Text = $"{(int)s.TopOffset} px";

            SliderHorizontalOffset.Value = s.HorizontalOffset;
            LblHorizontalOffset.Text = s.HorizontalOffset == 0 ? "0 px (Chính giữa)" : $"{(int)s.HorizontalOffset} px";

            ChkAlwaysOnTop.IsChecked = s.AlwaysOnTop;
            ChkAutoHide.IsChecked = s.AutoHide;
            SliderAutoHideDelay.Value = s.AutoHideDelay;
            LblAutoHideDelay.Text = $"{s.AutoHideDelay:0.0} giây";

            // Appearance
            SliderIdleWidth.Value = s.IdleWidth;
            LblIdleWidth.Text = $"{(int)s.IdleWidth} px";

            SliderExpandedWidth.Value = s.ExpandedWidth;
            LblExpandedWidth.Text = $"{(int)s.ExpandedWidth} px";

            ChkEnableShadow.IsChecked = s.EnableShadow;
            SliderShadowOpacity.Value = s.ShadowOpacity;
            LblShadowOpacity.Text = $"Độ đậm: {(int)(s.ShadowOpacity * 100)}%";

            ChkHoverAnimation.IsChecked = s.HoverAnimation;

            // Features
            ChkShowClock.IsChecked = s.ShowClock;
            ChkUse24HourClock.IsChecked = s.Use24HourClock;
            ChkShowBattery.IsChecked = s.ShowBattery;
            ChkAutoSwitchMedia.IsChecked = s.AutoSwitchMedia;
            ChkEqualizerEnabled.IsChecked = s.EqualizerEnabled;
            ChkVolumeBrightnessHud.IsChecked = s.VolumeBrightnessHudEnabled;
            ChkMouseWheelVolume.IsChecked = s.MouseWheelVolumeEnabled;
            ChkPrivacyIndicators.IsChecked = s.EnablePrivacyIndicators;
            ChkLockKeyNotifications.IsChecked = s.EnableLockKeyNotifications;

            // LocalSend & File Transfer
            if (s.FileTransferMethod == "LocalSend")
            {
                RadMethodLocalSend.IsChecked = true;
            }
            else
            {
                RadMethodWindowsShare.IsChecked = true;
            }

            ChkLocalSendAutoDiscovery.IsChecked = s.LocalSendAutoDiscovery;
            TxtLocalSendIp.Text = s.LocalSendTargetIp;
            TxtLocalSendPort.Text = s.LocalSendPort.ToString();
            if (!string.IsNullOrEmpty(s.LocalSendTargetName))
            {
                TxtLocalSendStatus.Text = $"Đã lưu: {s.LocalSendTargetName}";
            }

            // System
            ChkStartWithWindows.IsChecked = s.StartWithWindows;
        }

        #region Title Bar & Window Control
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnSaveClose_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Save();
            Close();
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var res = System.Windows.MessageBox.Show(
                "Bạn có chắc muốn khôi phục toàn bộ cài đặt về mặc định?", 
                "Dynamic Island", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                _isInitializing = true;
                SettingsService.Instance.ResetToDefaults();
                LoadCurrentSettings();
                _isInitializing = false;
            }
        }
        #endregion

        #region Sidebar Navigation
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelDisplay == null || PanelAppearance == null || PanelFeatures == null || PanelSystem == null || PanelLocalSend == null) 
                return;

            PanelDisplay.Visibility = Visibility.Collapsed;
            PanelAppearance.Visibility = Visibility.Collapsed;
            PanelFeatures.Visibility = Visibility.Collapsed;
            PanelSystem.Visibility = Visibility.Collapsed;
            PanelLocalSend.Visibility = Visibility.Collapsed;

            if (sender is RadioButton rb)
            {
                if (rb == NavDisplay) PanelDisplay.Visibility = Visibility.Visible;
                else if (rb == NavAppearance) PanelAppearance.Visibility = Visibility.Visible;
                else if (rb == NavFeatures) PanelFeatures.Visibility = Visibility.Visible;
                else if (rb == NavLocalSend) PanelLocalSend.Visibility = Visibility.Visible;
                else if (rb == NavSystem) PanelSystem.Visibility = Visibility.Visible;
            }
        }
        #endregion

        #region Display Settings Events
        private void CmbMonitors_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (CmbMonitors.SelectedIndex >= 0)
            {
                SettingsService.Instance.Update(s => s.MonitorIndex = CmbMonitors.SelectedIndex);
            }
        }

        private void SliderTopOffset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblTopOffset != null)
            {
                LblTopOffset.Text = $"{(int)e.NewValue} px";
            }
            if (_isInitializing) return;

            SettingsService.Instance.Update(s => s.TopOffset = e.NewValue);
        }

        private void SliderHorizontalOffset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblHorizontalOffset != null)
            {
                LblHorizontalOffset.Text = e.NewValue == 0 ? "0 px (Chính giữa)" : $"{(int)e.NewValue} px";
            }
            if (_isInitializing) return;

            SettingsService.Instance.Update(s => s.HorizontalOffset = e.NewValue);
        }

        private void BtnQuickOffsetLeft_Click(object sender, RoutedEventArgs e)
        {
            SliderHorizontalOffset.Value = -180;
        }

        private void BtnQuickOffsetCenter_Click(object sender, RoutedEventArgs e)
        {
            SliderHorizontalOffset.Value = 0;
        }

        private void BtnQuickOffsetRight_Click(object sender, RoutedEventArgs e)
        {
            SliderHorizontalOffset.Value = 180;
        }

        private void ChkAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.AlwaysOnTop = ChkAlwaysOnTop.IsChecked == true);
        }

        private void ChkAutoHide_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.AutoHide = ChkAutoHide.IsChecked == true);
        }

        private void SliderAutoHideDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblAutoHideDelay != null)
            {
                LblAutoHideDelay.Text = $"{e.NewValue:0.0} giây";
            }
            if (_isInitializing) return;

            SettingsService.Instance.Update(s => s.AutoHideDelay = Math.Round(e.NewValue, 1));
        }
        #endregion

        #region Appearance Settings Events
        private void SliderIdleWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblIdleWidth != null)
            {
                LblIdleWidth.Text = $"{(int)e.NewValue} px";
            }
            if (_isInitializing) return;

            SettingsService.Instance.Update(s => s.IdleWidth = e.NewValue);
        }

        private void SliderExpandedWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblExpandedWidth != null)
            {
                LblExpandedWidth.Text = $"{(int)e.NewValue} px";
            }
            if (_isInitializing) return;

            SettingsService.Instance.Update(s => s.ExpandedWidth = e.NewValue);
        }

        private void BtnThemeOled_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Update(s =>
            {
                s.ThemeBackground = "#080809";
                s.BorderColor = "#2A2A30";
            });
        }

        private void BtnThemeObsidian_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Update(s =>
            {
                s.ThemeBackground = "#141418";
                s.BorderColor = "#323242";
            });
        }

        private void BtnThemeMidnight_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Update(s =>
            {
                s.ThemeBackground = "#161618";
                s.BorderColor = "#2E2E34";
            });
        }

        private void BtnThemeCyber_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Update(s =>
            {
                s.ThemeBackground = "#1F1F24";
                s.BorderColor = "#3A3A42";
            });
        }

        private void ChkEnableShadow_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.EnableShadow = ChkEnableShadow.IsChecked == true);
        }

        private void SliderShadowOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblShadowOpacity != null)
            {
                LblShadowOpacity.Text = $"Độ đậm: {(int)(e.NewValue * 100)}%";
            }
            if (_isInitializing) return;

            SettingsService.Instance.Update(s => s.ShadowOpacity = e.NewValue);
        }

        private void ChkHoverAnimation_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.HoverAnimation = ChkHoverAnimation.IsChecked == true);
        }
        #endregion

        #region Features Settings Events
        private void ChkShowClock_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.ShowClock = ChkShowClock.IsChecked == true);
        }

        private void ChkUse24HourClock_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.Use24HourClock = ChkUse24HourClock.IsChecked == true);
        }

        private void ChkShowBattery_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.ShowBattery = ChkShowBattery.IsChecked == true);
        }

        private void ChkAutoSwitchMedia_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.AutoSwitchMedia = ChkAutoSwitchMedia.IsChecked == true);
        }

        private void ChkEqualizerEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.EqualizerEnabled = ChkEqualizerEnabled.IsChecked == true);
        }

        private void ChkVolumeBrightnessHud_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.VolumeBrightnessHudEnabled = ChkVolumeBrightnessHud.IsChecked == true);
        }

        private void ChkMouseWheelVolume_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.MouseWheelVolumeEnabled = ChkMouseWheelVolume.IsChecked == true);
        }

        private void ChkPrivacyIndicators_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.EnablePrivacyIndicators = ChkPrivacyIndicators.IsChecked == true);
        }

        private void ChkLockKeyNotifications_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.EnableLockKeyNotifications = ChkLockKeyNotifications.IsChecked == true);
        }
        #endregion

        #region System Settings Events
        private void ChkStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            bool enable = ChkStartWithWindows.IsChecked == true;
            SettingsService.Instance.SetStartup(enable);
            SettingsService.Instance.Save();
        }
        #endregion

        #region LocalSend Settings Events
        private void RadMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            string method = RadMethodLocalSend.IsChecked == true ? "LocalSend" : "WindowsShare";
            SettingsService.Instance.Update(s => s.FileTransferMethod = method);
        }

        private void ChkLocalSendAutoDiscovery_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.LocalSendAutoDiscovery = ChkLocalSendAutoDiscovery.IsChecked == true);
        }

        private void TxtLocalSendIp_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsService.Instance.Update(s => s.LocalSendTargetIp = TxtLocalSendIp.Text.Trim());
        }

        private void TxtLocalSendPort_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (int.TryParse(TxtLocalSendPort.Text.Trim(), out int port) && port > 0 && port <= 65535)
            {
                SettingsService.Instance.Update(s => s.LocalSendPort = port);
            }
        }

        private async void BtnLocalSendScan_Click(object sender, RoutedEventArgs e)
        {
            BtnLocalSendScan.IsEnabled = false;
            TxtLocalSendStatus.Text = "⏳ Đang quét mạng Wi-Fi...";
            TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAE, 0xAE, 0xB2));

            try
            {
                var s = SettingsService.Instance.Current;
                var device = await LocalSendService.Instance.DiscoverDeviceAsync(s.LocalSendTargetIp, s.LocalSendPort);
                if (device != null)
                {
                    _isInitializing = true;
                    TxtLocalSendIp.Text = device.IpAddress;
                    TxtLocalSendPort.Text = device.Port.ToString();
                    _isInitializing = false;

                    SettingsService.Instance.Update(cfg =>
                    {
                        cfg.LocalSendTargetIp = device.IpAddress;
                        cfg.LocalSendPort = device.Port;
                        cfg.LocalSendTargetName = device.Alias;
                    });

                    TxtLocalSendStatus.Text = $"✅ Đã tìm thấy: {device.DisplayName} ({device.IpAddress})";
                    TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                }
                else
                {
                    TxtLocalSendStatus.Text = "⚠️ Không tìm thấy thiết bị nào (Hãy mở app LocalSend)";
                    TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
                }
            }
            catch (Exception ex)
            {
                TxtLocalSendStatus.Text = $"Lỗi: {ex.Message}";
                TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
            }
            finally
            {
                BtnLocalSendScan.IsEnabled = true;
            }
        }

        private async void BtnLocalSendTest_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtLocalSendIp.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                TxtLocalSendStatus.Text = "⚠️ Vui lòng nhập địa chỉ IP hoặc bấm Quét";
                TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAE, 0xAE, 0xB2));
                return;
            }

            int port = 53317;
            int.TryParse(TxtLocalSendPort.Text.Trim(), out port);

            BtnLocalSendTest.IsEnabled = false;
            TxtLocalSendStatus.Text = "⏳ Đang kiểm tra kết nối...";
            TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAE, 0xAE, 0xB2));

            try
            {
                var device = await LocalSendService.Instance.TestDeviceAsync(ip, port);
                if (device != null)
                {
                    SettingsService.Instance.Update(cfg =>
                    {
                        cfg.LocalSendTargetIp = ip;
                        cfg.LocalSendPort = port;
                        cfg.LocalSendTargetName = device.Alias;
                    });

                    TxtLocalSendStatus.Text = $"✅ Kết nối thành công tới: {device.DisplayName}";
                    TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                }
                else
                {
                    TxtLocalSendStatus.Text = "❌ Không thể kết nối tới IP/Port này";
                    TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
                }
            }
            catch (Exception ex)
            {
                TxtLocalSendStatus.Text = $"Lỗi: {ex.Message}";
                TxtLocalSendStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
            }
            finally
            {
                BtnLocalSendTest.IsEnabled = true;
            }
        }
        #endregion
    }
}
