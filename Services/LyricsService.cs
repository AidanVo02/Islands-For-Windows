using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicIsland.Services
{
    public class LyricLine
    {
        public TimeSpan Timestamp { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public class SongLyrics
    {
        public string TrackTitle { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<LyricLine> Lines { get; set; } = new();
        public bool HasSyncedLyrics => Lines.Count > 0;
        public string PlainLyrics { get; set; } = string.Empty;
        public bool IsNotFound { get; set; } = false;
    }

    public class LrclibResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("albumName")]
        public string? AlbumName { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }

    public class LyricsService
    {
        private static LyricsService? _instance;
        public static LyricsService Instance => _instance ??= new LyricsService();

        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, SongLyrics> _lyricsCache = new();
        private CancellationTokenSource? _currentCts;

        public SongLyrics? CurrentLyrics { get; private set; }
        public string CurrentTrackKey { get; private set; } = string.Empty;

        public event Action<SongLyrics>? LyricsLoaded;
        public event Action<LyricLine?, LyricLine?, LyricLine?>? ActiveLinesChanged;

        private LyricLine? _lastActiveCurrent;

        private LyricsService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "DynamicIsland/1.0 (Windows; +https://github.com/AidanVo02/Islands-For-Windows)");
            _httpClient.DefaultRequestHeaders.Add("Lrclib-Client", "DynamicIsland/1.0");
        }

        public async Task LoadLyricsAsync(string rawTitle, string rawArtist, double durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle == "No Media Playing" || rawTitle == "Unknown Track")
            {
                CurrentLyrics = null;
                CurrentTrackKey = string.Empty;
                _lastActiveCurrent = null;
                LyricsLoaded?.Invoke(new SongLyrics { IsNotFound = true });
                return;
            }

            var (cleanTitle, cleanArtist, baseTitle) = CleanMetadata(rawTitle, rawArtist);
            string cacheKey = $"{cleanTitle.ToLowerInvariant()}__{cleanArtist.ToLowerInvariant()}";

            if (CurrentTrackKey == cacheKey && CurrentLyrics != null)
            {
                // Vẫn kích hoạt event để UI cập nhật nếu chuyển đổi chế độ hiển thị
                LyricsLoaded?.Invoke(CurrentLyrics);
                return;
            }

            CurrentTrackKey = cacheKey;
            _lastActiveCurrent = null;

            // Kiểm tra trong Cache trước
            if (_lyricsCache.TryGetValue(cacheKey, out var cachedLyrics))
            {
                CurrentLyrics = cachedLyrics;
                LyricsLoaded?.Invoke(cachedLyrics);
                return;
            }

            // Hủy request cũ nếu có
            _currentCts?.Cancel();
            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            try
            {
                var lyrics = await FetchLyricsFromApiAsync(cleanTitle, cleanArtist, baseTitle, durationSeconds, ct);
                if (lyrics != null)
                {
                    _lyricsCache[cacheKey] = lyrics;
                    CurrentLyrics = lyrics;
                    LyricsLoaded?.Invoke(lyrics);
                }
                else
                {
                    var notFound = new SongLyrics
                    {
                        TrackTitle = cleanTitle,
                        Artist = cleanArtist,
                        IsNotFound = true
                    };
                    _lyricsCache[cacheKey] = notFound;
                    CurrentLyrics = notFound;
                    LyricsLoaded?.Invoke(notFound);
                }
            }
            catch (OperationCanceledException)
            {
                // Bỏ qua khi bị hủy do chuyển bài nhanh
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LyricsService fetch error: {ex.Message}");
                var errorLyrics = new SongLyrics
                {
                    TrackTitle = cleanTitle,
                    Artist = cleanArtist,
                    IsNotFound = true
                };
                CurrentLyrics = errorLyrics;
                LyricsLoaded?.Invoke(errorLyrics);
            }
        }

        public void UpdatePlaybackPosition(TimeSpan position)
        {
            if (CurrentLyrics == null || !CurrentLyrics.HasSyncedLyrics)
            {
                return;
            }

            var (prev, current, next) = GetActiveLines(position);
            if (current != _lastActiveCurrent)
            {
                _lastActiveCurrent = current;
                ActiveLinesChanged?.Invoke(prev, current, next);
            }
        }

        public (LyricLine? prev, LyricLine? current, LyricLine? next) GetActiveLines(TimeSpan position)
        {
            if (CurrentLyrics == null || CurrentLyrics.Lines.Count == 0)
                return (null, null, null);

            var lines = CurrentLyrics.Lines;
            int currentIndex = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Timestamp <= position)
                {
                    currentIndex = i;
                }
                else
                {
                    break;
                }
            }

            if (currentIndex == -1)
            {
                return (null, null, lines.Count > 0 ? lines[0] : null);
            }

            LyricLine? prev = currentIndex > 0 ? lines[currentIndex - 1] : null;
            LyricLine current = lines[currentIndex];
            LyricLine? next = currentIndex + 1 < lines.Count ? lines[currentIndex + 1] : null;

            return (prev, current, next);
        }

        private async Task<SongLyrics?> FetchLyricsFromApiAsync(string title, string artist, string baseTitle, double duration, CancellationToken ct)
        {
            // 1. Danh sách các candidate để thử với endpoint /api/get (nhanh & có chỉ mục B-tree)
            var getCandidates = new List<(string t, string a)>();

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
            {
                getCandidates.Add((title, artist));
            }

            if (!string.IsNullOrWhiteSpace(baseTitle) && baseTitle != title && !string.IsNullOrWhiteSpace(artist))
            {
                getCandidates.Add((baseTitle, artist));
            }

            string noParenTitle = Regex.Replace(title, @"\s*[\(\[][^\)\]]+[\)\]]", "").Trim();
            if (!string.IsNullOrWhiteSpace(noParenTitle) && noParenTitle != title && noParenTitle != baseTitle && !string.IsNullOrWhiteSpace(artist))
            {
                getCandidates.Add((noParenTitle, artist));
            }

            // Thử gọi /api/get theo từng candidate không kèm duration (tránh bị lệch thời lượng)
            foreach (var (candTitle, candArtist) in getCandidates)
            {
                if (ct.IsCancellationRequested) return null;
                var result = await TryLrclibGetAsync(candTitle, candArtist, ct);
                if (result != null) return result;
            }

            // 2. Thử gọi /api/get kèm duration nếu bài hát có thời lượng cụ thể
            if (duration > 15 && !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
            {
                if (ct.IsCancellationRequested) return null;
                var resultWithDuration = await TryLrclibGetAsync(title, artist, ct, (int)Math.Round(duration));
                if (resultWithDuration != null) return resultWithDuration;
            }

            // 3. Fallback sang /api/search
            if (ct.IsCancellationRequested) return null;
            await Task.Delay(250, ct); // Giảm thiểu chạm rate limit

            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            var searchResult = await TryLrclibSearchAsync(query, title, artist, ct);
            if (searchResult != null) return searchResult;

            // 4. Nếu baseTitle khác title, thử search với baseTitle
            if (!string.IsNullOrWhiteSpace(baseTitle) && baseTitle != title)
            {
                if (ct.IsCancellationRequested) return null;
                await Task.Delay(250, ct);
                string baseQuery = string.IsNullOrWhiteSpace(artist) ? baseTitle : $"{baseTitle} {artist}";
                var baseSearchResult = await TryLrclibSearchAsync(baseQuery, baseTitle, artist, ct);
                if (baseSearchResult != null) return baseSearchResult;
            }

            return null;
        }

        private async Task<SongLyrics?> TryLrclibGetAsync(string trackName, string artistName, CancellationToken ct, int? durationSec = null)
        {
            try
            {
                string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(trackName)}&artist_name={Uri.EscapeDataString(artistName)}";
                if (durationSec.HasValue && durationSec.Value > 0)
                {
                    url += $"&duration={durationSec.Value}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, ct);

                // Xử lý khi server phản hồi quá tải 503 ServerOverloaded
                if ((int)response.StatusCode == 503)
                {
                    int delayMs = 1000;
                    if (response.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        delayMs = (int)response.Headers.RetryAfter.Delta.Value.TotalMilliseconds;
                    }
                    delayMs = Math.Clamp(delayMs, 800, 2500);
                    await Task.Delay(delayMs, ct);

                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, url);
                    using var retryResp = await _httpClient.SendAsync(retryReq, ct);
                    if (retryResp.IsSuccessStatusCode)
                    {
                        var retryJson = await retryResp.Content.ReadAsStringAsync(ct);
                        var retryItem = JsonSerializer.Deserialize<LrclibResponse>(retryJson);
                        if (retryItem != null && (!string.IsNullOrWhiteSpace(retryItem.SyncedLyrics) || !string.IsNullOrWhiteSpace(retryItem.PlainLyrics)))
                        {
                            return BuildLyricsObject(trackName, artistName, retryItem);
                        }
                    }
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var item = JsonSerializer.Deserialize<LrclibResponse>(json);
                    if (item != null && (!string.IsNullOrWhiteSpace(item.SyncedLyrics) || !string.IsNullOrWhiteSpace(item.PlainLyrics)))
                    {
                        return BuildLyricsObject(trackName, artistName, item);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LRCLIB GET error for '{trackName}' - '{artistName}': {ex.Message}");
            }

            return null;
        }

        private async Task<SongLyrics?> TryLrclibSearchAsync(string query, string title, string artist, CancellationToken ct)
        {
            try
            {
                string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                using var response = await _httpClient.SendAsync(request, ct);

                if ((int)response.StatusCode == 503)
                {
                    int delayMs = 1200;
                    if (response.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        delayMs = (int)response.Headers.RetryAfter.Delta.Value.TotalMilliseconds;
                    }
                    delayMs = Math.Clamp(delayMs, 1000, 3000);
                    await Task.Delay(delayMs, ct);

                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    using var retryResp = await _httpClient.SendAsync(retryReq, ct);
                    if (retryResp.IsSuccessStatusCode)
                    {
                        var retryJson = await retryResp.Content.ReadAsStringAsync(ct);
                        var retryItems = JsonSerializer.Deserialize<List<LrclibResponse>>(retryJson);
                        return PickBestSearchResult(retryItems, title, artist);
                    }
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var items = JsonSerializer.Deserialize<List<LrclibResponse>>(json);
                    return PickBestSearchResult(items, title, artist);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LRCLIB SEARCH error for '{query}': {ex.Message}");
            }

            return null;
        }

        private static SongLyrics? PickBestSearchResult(List<LrclibResponse>? items, string title, string artist)
        {
            if (items == null || items.Count == 0) return null;

            // 1. Ưu tiên bài có SyncedLyrics và khớp một phần tên nghệ sĩ
            var bestMatch = items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SyncedLyrics)
                && !string.IsNullOrWhiteSpace(artist)
                && x.ArtistName != null
                && x.ArtistName.Contains(artist, StringComparison.OrdinalIgnoreCase));

            // 2. Tiếp theo là bài bất kỳ có SyncedLyrics
            bestMatch ??= items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SyncedLyrics));

            // 3. Cuối cùng là PlainLyrics
            bestMatch ??= items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PlainLyrics));

            if (bestMatch != null)
            {
                return BuildLyricsObject(title, artist, bestMatch);
            }

            return null;
        }

        private static SongLyrics BuildLyricsObject(string title, string artist, LrclibResponse response)
        {
            var songLyrics = new SongLyrics
            {
                TrackTitle = !string.IsNullOrWhiteSpace(response.TrackName) ? response.TrackName : title,
                Artist = !string.IsNullOrWhiteSpace(response.ArtistName) ? response.ArtistName : artist,
                PlainLyrics = response.PlainLyrics ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(response.SyncedLyrics))
            {
                songLyrics.Lines = ParseLrc(response.SyncedLyrics);
            }

            return songLyrics;
        }

        public static List<LyricLine> ParseLrc(string lrcContent)
        {
            var list = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(lrcContent)) return list;

            // Pattern: [mm:ss.xx] hoặc [mm:ss.xxx]
            var regex = new Regex(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)");
            using var reader = new StringReader(lrcContent);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    int min = int.Parse(match.Groups[1].Value);
                    int sec = int.Parse(match.Groups[2].Value);
                    int ms = 0;
                    if (match.Groups[3].Success)
                    {
                        string msStr = match.Groups[3].Value;
                        if (msStr.Length == 2) ms = int.Parse(msStr) * 10;
                        else if (msStr.Length == 3) ms = int.Parse(msStr);
                        else if (msStr.Length == 1) ms = int.Parse(msStr) * 100;
                    }

                    var time = new TimeSpan(0, 0, min, sec, ms);
                    string text = match.Groups[4].Value.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        text = "•••"; // Ký hiệu dạo nhạc
                    }

                    list.Add(new LyricLine { Timestamp = time, Text = text });
                }
            }

            var ordered = list.OrderBy(x => x.Timestamp).ToList();
            var deduplicated = new List<LyricLine>();
            for (int i = 0; i < ordered.Count; i++)
            {
                // Tránh lặp liên tiếp nhiều dòng •••
                if (ordered[i].Text == "•••" && deduplicated.Count > 0 && deduplicated[^1].Text == "•••")
                {
                    continue;
                }
                deduplicated.Add(ordered[i]);
            }

            return deduplicated;
        }

        public static (string title, string artist, string baseTitle) CleanMetadata(string rawTitle, string rawArtist)
        {
            string title = rawTitle?.Trim() ?? string.Empty;
            string artist = rawArtist?.Trim() ?? string.Empty;

            bool artistIsGeneric = string.IsNullOrWhiteSpace(artist)
                || artist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Chrome", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Edge", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Firefox", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Opera", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Brave", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("YouTube", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Windows Audio", StringComparison.OrdinalIgnoreCase)
                || artist.Equals("Media", StringComparison.OrdinalIgnoreCase);

            // Nếu artist rỗng hoặc là tên chung chung, cố gắng tách "Artist - Title" (YouTube style)
            if (artistIsGeneric)
            {
                string[] seps = new[] { " - ", " – ", " — ", " | " };
                foreach (var sep in seps)
                {
                    int idx = title.IndexOf(sep, StringComparison.Ordinal);
                    if (idx > 0 && idx < title.Length - sep.Length)
                    {
                        artist = title.Substring(0, idx).Trim();
                        title = title.Substring(idx + sep.Length).Trim();
                        break;
                    }
                }
            }

            // Loại bỏ các hậu tố video trong ngoặc đơn / ngoặc vuông
            title = Regex.Replace(title, @"\s*[\(\[](?:Official\s+)?(?:Music\s+)?(?:Video|Audio|Lyric\s+Video|Lyrics|Visualizer|HD|4K|Remastered|MV)[\)\]]", "", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\s*[\(\[](?:feat|ft)\.?\s+[^\)\]]+[\)\]]", "", RegexOptions.IgnoreCase);
            title = title.Trim('"', '\'', '“', '”', '‘', '’', ' ');

            artist = Regex.Replace(artist, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
            artist = Regex.Replace(artist, @"VEVO$", "", RegexOptions.IgnoreCase);
            artist = artist.Trim('"', '\'', '“', '”', '‘', '’', ' ');

            // Trích xuất baseTitle (phần trước dấu gạch ngang nếu có)
            string baseTitle = title;
            int dashIdx = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0)
            {
                baseTitle = title.Substring(0, dashIdx).Trim();
            }
            else
            {
                dashIdx = title.IndexOf(" – ", StringComparison.Ordinal);
                if (dashIdx > 0)
                {
                    baseTitle = title.Substring(0, dashIdx).Trim();
                }
            }

            return (title, artist, baseTitle);
        }
    }
}
