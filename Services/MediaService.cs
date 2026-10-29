using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services
{
    public class MediaTrackInfo
    {
        public string Title { get; set; } = "No Media Playing";
        public string Artist { get; set; } = "Windows Audio";
        public BitmapImage? Thumbnail { get; set; }
        public bool IsPlaying { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public string SourceAppId { get; set; } = string.Empty;
    }

    public class MediaService
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        public event Action<MediaTrackInfo>? MediaTrackChanged;
        public event Action<bool>? PlaybackStateChanged;
        public event Action<TimeSpan, TimeSpan>? TimelineChanged;

        public MediaTrackInfo CurrentTrack { get; private set; } = new();

        public async Task InitializeAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;
                    UpdateCurrentSession(_sessionManager.GetCurrentSession());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediaService Init Error: {ex.Message}");
            }
        }

        private void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession(sender.GetCurrentSession());
        }

        private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
            }

            _currentSession = session;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;

                _ = RefreshMediaPropertiesAsync();
            }
            else
            {
                CurrentTrack = new MediaTrackInfo { IsPlaying = false };
                MediaTrackChanged?.Invoke(CurrentTrack);
            }
        }

        private async void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RefreshMediaPropertiesAsync();
        }

        private void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            var playbackInfo = sender.GetPlaybackInfo();
            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            CurrentTrack.IsPlaying = isPlaying;
            PlaybackStateChanged?.Invoke(isPlaying);
        }

        private void CurrentSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            var timeline = sender.GetTimelineProperties();
            if (timeline != null)
            {
                CurrentTrack.Position = timeline.Position;
                CurrentTrack.Duration = timeline.EndTime;
                TimelineChanged?.Invoke(timeline.Position, timeline.EndTime);
            }
        }

        public async Task RefreshMediaPropertiesAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var props = await _currentSession.TryGetMediaPropertiesAsync();
                var playback = _currentSession.GetPlaybackInfo();
                var timeline = _currentSession.GetTimelineProperties();

                BitmapImage? thumb = null;
                if (props?.Thumbnail != null)
                {
                    thumb = await LoadThumbnailAsync(props.Thumbnail);
                }

                bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                CurrentTrack = new MediaTrackInfo
                {
                    Title = string.IsNullOrWhiteSpace(props?.Title) ? "Unknown Track" : props.Title,
                    Artist = string.IsNullOrWhiteSpace(props?.Artist) ? "Unknown Artist" : props.Artist,
                    Thumbnail = thumb,
                    IsPlaying = isPlaying,
                    Position = timeline?.Position ?? TimeSpan.Zero,
                    Duration = timeline?.EndTime ?? TimeSpan.Zero,
                    SourceAppId = _currentSession.SourceAppUserModelId ?? string.Empty
                };

                MediaTrackChanged?.Invoke(CurrentTrack);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading media properties: {ex.Message}");
            }
        }

        private async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference streamRef)
        {
            try
            {
                using var streamWithContent = await streamRef.OpenReadAsync();
                using var netStream = streamWithContent.AsStreamForRead();
                using var memoryStream = new MemoryStream();
                await netStream.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memoryStream;
                bitmap.EndInit();
                bitmap.Freeze(); // Cho phép dùng trên mọi UI thread
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_currentSession != null)
            {
                await _currentSession.TryTogglePlayPauseAsync();
            }
        }

        public async Task SkipNextAsync()
        {
            if (_currentSession != null)
            {
                await _currentSession.TrySkipNextAsync();
            }
        }

        public async Task SkipPreviousAsync()
        {
            if (_currentSession != null)
            {
                await _currentSession.TrySkipPreviousAsync();
            }
        }

        public async Task<bool> ChangePlaybackPositionAsync(TimeSpan position)
        {
            if (_currentSession != null)
            {
                try
                {
                    return await _currentSession.TryChangePlaybackPositionAsync(position.Ticks);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ChangePlaybackPositionAsync error: {ex.Message}");
                }
            }
            return false;
        }
    }
}
