using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Imaging = System.Windows.Interop.Imaging;
using Clipboard = System.Windows.Clipboard;

namespace DynamicIsland.Services
{
    public enum LauncherItemType
    {
        App,
        Calculator,
        WebSearch,
        SystemCommand
    }

    /// <summary>
    /// Lưu trữ thông tin siêu nhẹ của ứng dụng đã cài đặt trong RAM (Không lưu Icon)
    /// </summary>
    public class AppItem
    {
        public string Name { get; set; } = "";
        public string ShortcutPath { get; set; } = "";
        public string SearchKeywords { get; set; } = "";
        public string Acronym { get; set; } = "";
    }

    /// <summary>
    /// Kết quả tìm kiếm trả về hiển thị trên UI (Tối đa 5 mục)
    /// </summary>
    public class LauncherResultItem
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string ActionHint { get; set; } = "↵ Mở";
        public LauncherItemType ItemType { get; set; } = LauncherItemType.App;
        public ImageSource? Icon { get; set; }
        public string? IconGlyph { get; set; }
        public Brush IconGlyphBrush { get; set; } = Brushes.White;
        public Action<bool>? ExecuteAction { get; set; }
    }

    /// <summary>
    /// Dịch vụ quét Start Menu và tìm kiếm siêu nhẹ (Zero-Bloat)
    /// </summary>
    public class AppLauncherService : IDisposable
    {
        private static readonly Lazy<AppLauncherService> _lazy = new(() => new AppLauncherService());
        public static AppLauncherService Instance => _lazy.Value;

        private readonly object _lock = new();
        private List<AppItem> _apps = new();
        private readonly List<FileSystemWatcher> _watchers = new();
        private System.Threading.Timer? _debounceTimer;
        private bool _isInitialized = false;

        // Frozen Brushes cho icon Glyph để không cấp phát bộ nhớ mỗi lần search
        private static readonly Brush CalculatorBrush = CreateFrozenBrush(52, 199, 89);   // #34C759 Accent Green
        private static readonly Brush GoogleBrush = CreateFrozenBrush(66, 133, 244);       // #4285F4 Google Blue
        private static readonly Brush YouTubeBrush = CreateFrozenBrush(255, 59, 48);       // #FF3B30 YouTube Red
        private static readonly Brush SystemBrush = CreateFrozenBrush(255, 149, 0);        // #FF9500 System Orange
        private static readonly Brush SettingsBrush = CreateFrozenBrush(142, 142, 147);    // #8E8E93 Settings Gray

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private AppLauncherService()
        {
        }

        /// <summary>
        /// Khởi tạo và quét Start Menu bất đồng bộ trong background Task
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            Task.Run(() =>
            {
                ReloadApps();
                SetupWatchers();
            });
        }

        /// <summary>
        /// Quét Start Menu cả người dùng hiện tại và toàn hệ thống
        /// </summary>
        public void ReloadApps()
        {
            var list = new List<AppItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] startMenuDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };

            foreach (var dir in startMenuDirs)
            {
                if (!Directory.Exists(dir)) continue;
                ScanDirectory(dir, list, seenPaths, seenNames);
            }

            lock (_lock)
            {
                _apps = list.OrderBy(a => a.Name).ToList();
            }
        }

        private void ScanDirectory(string dir, List<AppItem> list, HashSet<string> seenPaths, HashSet<string> seenNames)
        {
            try
            {
                var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".url", StringComparison.OrdinalIgnoreCase));

                foreach (var file in files)
                {
                    if (seenPaths.Contains(file)) continue;

                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;

                    // Lọc bỏ các shortcut không cần thiết (gỡ cài đặt, tài liệu, web support)
                    string lower = fileName.ToLowerInvariant();
                    if (lower.Contains("uninstall") ||
                        lower.Contains("gỡ cài đặt") ||
                        lower.Contains("remove") ||
                        lower.Contains("setup") ||
                        lower.Contains("help") ||
                        lower.Contains("readme") ||
                        lower.Contains("read me") ||
                        lower.Contains("documentation") ||
                        lower.Contains("website"))
                    {
                        continue;
                    }

                    if (seenNames.Contains(fileName)) continue;

                    seenPaths.Add(file);
                    seenNames.Add(fileName);

                    string normalized = RemoveDiacritics(fileName).ToLowerInvariant();
                    string acronym = GenerateAcronym(fileName);

                    list.Add(new AppItem
                    {
                        Name = fileName,
                        ShortcutPath = file,
                        SearchKeywords = normalized,
                        Acronym = acronym
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppLauncherService: Lỗi quét thư mục {dir}: {ex.Message}");
            }
        }

        /// <summary>
        /// Tạo chữ viết tắt từ tên ứng dụng (VD: "Visual Studio Code" -> "vsc")
        /// </summary>
        private static string GenerateAcronym(string name)
        {
            var words = Regex.Split(name, @"[\s\-_\.]+").Where(w => !string.IsNullOrEmpty(w));
            var sb = new StringBuilder();
            foreach (var w in words)
            {
                char c = w[0];
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Theo dõi thay đổi trong thư mục Start Menu bằng FileSystemWatcher
        /// </summary>
        private void SetupWatchers()
        {
            string[] startMenuDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };

            foreach (var dir in startMenuDirs)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnFileSystemChanged;
                    watcher.Deleted += OnFileSystemChanged;
                    watcher.Renamed += OnFileSystemChanged;
                    watcher.Changed += OnFileSystemChanged;

                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"AppLauncherService: Không thể cài đặt FileSystemWatcher cho {dir}: {ex.Message}");
                }
            }
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce 1.5 giây để tránh quét liên tục khi cài đặt app lớn
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                ReloadApps();
            }, null, 1500, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// Trích xuất Icon liên kết một cách Lazy (chỉ gọi khi hiển thị kết quả)
        /// </summary>
        public static ImageSource? ExtractIcon(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                if (sysIcon == null) return null;

                var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                    sysIcon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze();
                return bitmapSource;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Tìm kiếm đa năng: Máy tính nhanh -> Web Bangs -> Lệnh hệ thống -> Ứng dụng
        /// </summary>
        public List<LauncherResultItem> Search(string query)
        {
            var results = new List<LauncherResultItem>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            string rawQuery = query.Trim();
            string normQuery = RemoveDiacritics(rawQuery).ToLowerInvariant();

            // 1. Máy tính nhanh (Inline Calculator)
            var calcItem = TryParseCalculator(rawQuery);
            if (calcItem != null)
            {
                results.Add(calcItem);
            }

            // 2. Tìm kiếm Web (Web Bangs: gg, yt)
            var webItem = TryParseWebBangs(rawQuery);
            if (webItem != null)
            {
                results.Add(webItem);
            }

            // 3. Lệnh hệ thống (System Commands)
            var sysCommands = MatchSystemCommands(normQuery);
            foreach (var cmd in sysCommands)
            {
                if (results.Count >= 5) break;
                results.Add(cmd);
            }

            // 4. Tìm kiếm ứng dụng (Fuzzy / Acronym / Substring match)
            if (results.Count < 5)
            {
                List<AppItem> snapshot;
                lock (_lock)
                {
                    snapshot = _apps;
                }

                // Ưu tiên 1: Tên bắt đầu bằng query
                var p1 = snapshot.Where(a =>
                    a.SearchKeywords.StartsWith(normQuery) ||
                    a.Name.StartsWith(rawQuery, StringComparison.OrdinalIgnoreCase)).ToList();

                // Ưu tiên 2: Khớp chữ cái đầu (Acronym match)
                var p2 = snapshot.Where(a =>
                    !p1.Contains(a) &&
                    !string.IsNullOrEmpty(a.Acronym) &&
                    (a.Acronym.StartsWith(normQuery) || a.Acronym.Equals(normQuery))).ToList();

                // Ưu tiên 3: Tên chứa từ khóa
                var p3 = snapshot.Where(a =>
                    !p1.Contains(a) &&
                    !p2.Contains(a) &&
                    a.SearchKeywords.Contains(normQuery)).ToList();

                var orderedApps = p1.Concat(p2).Concat(p3);
                int slotsRemaining = 5 - results.Count;
                var chosenApps = orderedApps.Take(slotsRemaining).ToList();

                // LAZY ICON EXTRACTION: Chỉ trích xuất Icon cho đúng số lượng kết quả được hiển thị
                foreach (var app in chosenApps)
                {
                    var icon = ExtractIcon(app.ShortcutPath);
                    results.Add(new LauncherResultItem
                    {
                        Title = app.Name,
                        Subtitle = "Ứng dụng",
                        ActionHint = "↵ Mở",
                        ItemType = LauncherItemType.App,
                        Icon = icon,
                        IconGlyph = "\uE71D",
                        IconGlyphBrush = Brushes.White,
                        ExecuteAction = (isAdmin) =>
                        {
                            try
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = app.ShortcutPath,
                                    UseShellExecute = true
                                };
                                if (isAdmin)
                                {
                                    psi.Verb = "runas";
                                }
                                Process.Start(psi);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"AppLauncherService: Lỗi khởi chạy {app.Name}: {ex.Message}");
                            }
                        }
                    });
                }
            }

            return results;
        }

        private LauncherResultItem? TryParseCalculator(string rawQuery)
        {
            string query = rawQuery.Trim();
            if (query.Length < 2) return null;

            // Kiểm tra chỉ gồm số và toán tử
            if (query.Any(char.IsLetter)) return null;
            if (!query.All(c => char.IsDigit(c) || "+-*/%^()., ".Contains(c))) return null;
            if (!query.Any(char.IsDigit)) return null;
            if (!query.Any(c => "+-*/%^".Contains(c))) return null;

            char lastChar = query.TrimEnd().Last();
            if ("+-*/%^".Contains(lastChar)) return null;

            try
            {
                string expr = query.Replace(',', '.');

                // Xử lý toán tử lũy thừa ^
                var powerRegex = new Regex(@"(\d+(?:\.\d+)?)\s*\^\s*(\d+(?:\.\d+)?)");
                int safetyCount = 0;
                while (powerRegex.IsMatch(expr) && safetyCount++ < 10)
                {
                    expr = powerRegex.Replace(expr, m =>
                    {
                        double b = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        double p = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        return Math.Pow(b, p).ToString(CultureInfo.InvariantCulture);
                    }, 1);
                }

                using var dt = new DataTable();
                var val = dt.Compute(expr, null);
                if (val != null && double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                {
                    if (double.IsInfinity(dVal) || double.IsNaN(dVal)) return null;

                    string formatted = (dVal % 1 == 0 && Math.Abs(dVal) < 1e15)
                        ? ((long)dVal).ToString("N0", CultureInfo.InvariantCulture)
                        : dVal.ToString("G10", CultureInfo.InvariantCulture);

                    return new LauncherResultItem
                    {
                        Title = $"= {formatted}",
                        Subtitle = $"Biểu thức: {query}",
                        ActionHint = "↵ Copy kết quả",
                        ItemType = LauncherItemType.Calculator,
                        IconGlyph = "\uE1D0",
                        IconGlyphBrush = CalculatorBrush,
                        ExecuteAction = _ =>
                        {
                            try
                            {
                                Clipboard.SetText(formatted);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"AppLauncherService Clipboard error: {ex.Message}");
                            }
                        }
                    };
                }
            }
            catch
            {
                // Biểu thức chưa hoàn chỉnh hoặc có lỗi cú pháp
            }

            return null;
        }

        private LauncherResultItem? TryParseWebBangs(string rawQuery)
        {
            string trimmed = rawQuery.TrimStart();
            if (trimmed.StartsWith("gg ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("gg:", StringComparison.OrdinalIgnoreCase))
            {
                string term = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                if (!string.IsNullOrEmpty(term))
                {
                    string url = $"https://www.google.com/search?q={Uri.EscapeDataString(term)}";
                    return new LauncherResultItem
                    {
                        Title = $"Tìm kiếm Google: \"{term}\"",
                        Subtitle = url,
                        ActionHint = "↵ Mở trình duyệt",
                        ItemType = LauncherItemType.WebSearch,
                        IconGlyph = "\uE774",
                        IconGlyphBrush = GoogleBrush,
                        ExecuteAction = _ =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"AppLauncherService Web error: {ex.Message}");
                            }
                        }
                    };
                }
            }
            else if (trimmed.StartsWith("yt ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("yt:", StringComparison.OrdinalIgnoreCase))
            {
                string term = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                if (!string.IsNullOrEmpty(term))
                {
                    string url = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(term)}";
                    return new LauncherResultItem
                    {
                        Title = $"Tìm kiếm YouTube: \"{term}\"",
                        Subtitle = url,
                        ActionHint = "↵ Mở trình duyệt",
                        ItemType = LauncherItemType.WebSearch,
                        IconGlyph = "\uE714",
                        IconGlyphBrush = YouTubeBrush,
                        ExecuteAction = _ =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"AppLauncherService YouTube error: {ex.Message}");
                            }
                        }
                    };
                }
            }

            return null;
        }

        private IEnumerable<LauncherResultItem> MatchSystemCommands(string normQuery)
        {
            var results = new List<LauncherResultItem>();

            if ("lock".StartsWith(normQuery) || "khoa".StartsWith(normQuery))
            {
                results.Add(new LauncherResultItem
                {
                    Title = "Khóa máy tính (Lock)",
                    Subtitle = "Khóa phiên làm việc màn hình máy tính",
                    ActionHint = "↵ Khóa máy",
                    ItemType = LauncherItemType.SystemCommand,
                    IconGlyph = "\uE72E",
                    IconGlyphBrush = SystemBrush,
                    ExecuteAction = _ => Win32Helper.LockWorkStation()
                });
            }

            if ("sleep".StartsWith(normQuery) || "ngu".StartsWith(normQuery))
            {
                results.Add(new LauncherResultItem
                {
                    Title = "Chế độ ngủ (Sleep)",
                    Subtitle = "Đưa máy tính vào trạng thái Sleep tiết kiệm năng lượng",
                    ActionHint = "↵ Ngủ máy",
                    ItemType = LauncherItemType.SystemCommand,
                    IconGlyph = "\uE708",
                    IconGlyphBrush = SystemBrush,
                    ExecuteAction = _ => Win32Helper.SetSuspendState(false, true, true)
                });
            }

            if ("trash".StartsWith(normQuery) || "emptybin".StartsWith(normQuery) || "thung rac".StartsWith(normQuery) || "don rac".StartsWith(normQuery))
            {
                results.Add(new LauncherResultItem
                {
                    Title = "Dọn sạch thùng rác (Empty Recycle Bin)",
                    Subtitle = "Xóa vĩnh viễn các tệp tin trong Thùng rác",
                    ActionHint = "↵ Dọn rác",
                    ItemType = LauncherItemType.SystemCommand,
                    IconGlyph = "\uE74D",
                    IconGlyphBrush = SystemBrush,
                    ExecuteAction = _ => Win32Helper.SHEmptyRecycleBin(IntPtr.Zero, null, 7)
                });
            }

            if ("settings".StartsWith(normQuery) || "cai dat".StartsWith(normQuery))
            {
                results.Add(new LauncherResultItem
                {
                    Title = "Cài đặt Dynamic Island",
                    Subtitle = "Mở cửa sổ tùy chỉnh giao diện và chức năng",
                    ActionHint = "↵ Mở cài đặt",
                    ItemType = LauncherItemType.SystemCommand,
                    IconGlyph = "\uE713",
                    IconGlyphBrush = SettingsBrush,
                    ExecuteAction = _ => SettingsWindow.ShowOrActivate()
                });
            }

            return results;
        }

        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string normalizedString = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D');
        }

        public void Dispose()
        {
            _debounceTimer?.Dispose();
            foreach (var w in _watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
        }
    }
}
