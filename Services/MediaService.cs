using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TheGrandNotch.Services;

public class MediaService : INotifyPropertyChanged
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    private DispatcherTimer? _positionTimer;
    private TimeSpan _smtcPosition;
    private DateTime _smtcPositionUtc;
    private TimeSpan _smtcStartTime;

    private string? _title;
    private string? _artist;
    private BitmapSource? _albumArt;
    private BitmapSource? _sourceAppIcon;
    private static readonly Color DefaultAccent = Color.FromRgb(60, 60, 60);
    private Color _accentColor = DefaultAccent;
    private Color _accentLeft = DefaultAccent;
    private Color _accentCenter = DefaultAccent;
    private Color _accentRight = DefaultAccent;
    private bool _isPlaying;
    private bool _hasSession;
    private TimeSpan _position;
    private TimeSpan _duration;

    public string? Title
    {
        get => _title;
        private set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    public string? Artist
    {
        get => _artist;
        private set { if (_artist != value) { _artist = value; OnPropertyChanged(); } }
    }

    public BitmapSource? AlbumArt
    {
        get => _albumArt;
        private set { _albumArt = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Couleur d'accent dominante extraite de la pochette (halo externe, conservé pour plus tard).
    /// </summary>
    public Color AccentColor
    {
        get => _accentColor;
        private set { if (_accentColor != value) { _accentColor = value; OnPropertyChanged(); } }
    }

    /// <summary>Couleurs dominantes des bandes gauche / centre / droite de la pochette (blobs internes).</summary>
    public Color AccentLeft
    {
        get => _accentLeft;
        private set { if (_accentLeft != value) { _accentLeft = value; OnPropertyChanged(); } }
    }

    public Color AccentCenter
    {
        get => _accentCenter;
        private set { if (_accentCenter != value) { _accentCenter = value; OnPropertyChanged(); } }
    }

    public Color AccentRight
    {
        get => _accentRight;
        private set { if (_accentRight != value) { _accentRight = value; OnPropertyChanged(); } }
    }

    public BitmapSource? SourceAppIcon
    {
        get => _sourceAppIcon;
        private set { _sourceAppIcon = value; OnPropertyChanged(); }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (_isPlaying != value) { _isPlaying = value; OnPropertyChanged(); } }
    }

    public bool HasSession
    {
        get => _hasSession;
        private set { if (_hasSession != value) { _hasSession = value; OnPropertyChanged(); } }
    }

    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (_position != value)
            {
                _position = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(ProgressFraction));
            }
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set
        {
            if (_duration != value)
            {
                _duration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(ProgressFraction));
            }
        }
    }

    public string PositionText => FormatTime(Position);
    public string DurationText => FormatTime(Duration);

    public double ProgressFraction =>
        Duration.TotalSeconds > 0
            ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0, 1)
            : 0;

    public async Task PlayPauseAsync()
    {
        if (_currentSession == null) return;
        try
        {
            if (IsPlaying) await _currentSession.TryPauseAsync();
            else await _currentSession.TryPlayAsync();
        }
        catch { }
    }

    public async Task NextAsync()
    {
        if (_currentSession == null) return;
        try { await _currentSession.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        if (_currentSession == null) return;
        try { await _currentSession.TrySkipPreviousAsync(); } catch { }
    }

    /// <summary>
    /// Déplace la lecture à une fraction (0..1) de la durée totale.
    /// </summary>
    public async Task SeekToFractionAsync(double fraction)
    {
        if (_currentSession == null || Duration <= TimeSpan.Zero) return;
        fraction = Math.Clamp(fraction, 0, 1);

        var offsetTicks = (long)(Duration.Ticks * fraction);
        var targetTicks = _smtcStartTime.Ticks + offsetTicks;

        try
        {
            await _currentSession.TryChangePlaybackPositionAsync(targetTicks);

            // Recale immédiatement la baseline locale pour un retour visuel instantané
            await OnUi(() =>
            {
                _smtcPosition = TimeSpan.FromTicks(offsetTicks);
                _smtcPositionUtc = DateTime.UtcNow;
                Position = _smtcPosition;
            });
        }
        catch { }
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            UpdateCurrentSession();

            _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _positionTimer.Tick += (_, _) => TickPosition();
            _positionTimer.Start();
        }
        catch
        {
            // SMTC unavailable — leave properties null
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => UpdateCurrentSession();

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => UpdateCurrentSession();

    private void UpdateCurrentSession()
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _currentSession = _manager?.GetCurrentSession();

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        _ = RefreshMediaInfoAsync();
        RefreshPlaybackInfo();
        RefreshTimeline();
        _ = RefreshSourceAppIconAsync();

        var hasSession = _currentSession != null;
        _ = OnUi(() => HasSession = hasSession);
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => await RefreshMediaInfoAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => RefreshPlaybackInfo();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => RefreshTimeline();

    private async Task RefreshMediaInfoAsync()
    {
        if (_currentSession == null)
        {
            await OnUi(() =>
            {
                Title = null;
                Artist = null;
                AlbumArt = null;
            });
            return;
        }

        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            BitmapSource? art = null;
            if (props?.Thumbnail != null)
                art = await LoadBitmapAsync(props.Thumbnail);

            Palette? palette = art != null ? ComputePalette(art) : null;

            await OnUi(() =>
            {
                Title = string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title;
                Artist = string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist;
                AlbumArt = art;
                AccentColor = palette?.Overall ?? DefaultAccent;
                AccentLeft = palette?.Left ?? DefaultAccent;
                AccentCenter = palette?.Center ?? DefaultAccent;
                AccentRight = palette?.Right ?? DefaultAccent;
            });
        }
        catch
        {
        }
    }

    private readonly record struct Palette(Color Overall, Color Left, Color Center, Color Right);

    /// <summary>
    /// Extrait une petite palette de la pochette : couleur globale + dominantes des bandes
    /// gauche / centre / droite. Chaque couleur est une moyenne pondérée par saturation²×luminosité
    /// (favorise les couleurs franches plutôt que le gris).
    /// </summary>
    private static Palette ComputePalette(BitmapSource source)
    {
        try
        {
            const int sample = 36;
            double sx = sample / (double)source.PixelWidth;
            double sy = sample / (double)source.PixelHeight;

            var scaled = new TransformedBitmap(source, new ScaleTransform(sx, sy));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[h * stride];
            bgra.CopyPixels(pixels, stride, 0);

            int third = Math.Max(1, w / 3);
            return new Palette(
                WeightedColor(pixels, w, h, stride, 0, w),
                WeightedColor(pixels, w, h, stride, 0, third),
                WeightedColor(pixels, w, h, stride, third, 2 * third),
                WeightedColor(pixels, w, h, stride, 2 * third, w));
        }
        catch
        {
            return new Palette(DefaultAccent, DefaultAccent, DefaultAccent, DefaultAccent);
        }
    }

    private static Color WeightedColor(byte[] pixels, int w, int h, int stride, int x0, int x1)
    {
        double wr = 0, wg = 0, wb = 0, wsum = 0;
        double ar = 0, ag = 0, ab = 0;
        int n = 0;

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a < 16) continue;

                ar += r; ag += g; ab += b; n++;

                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double saturation = max <= 0 ? 0 : (max - min) / max;
                double value = max / 255.0;
                double weight = saturation * saturation * value;

                wr += r * weight;
                wg += g * weight;
                wb += b * weight;
                wsum += weight;
            }
        }

        if (wsum > 0.0001)
            return Color.FromRgb((byte)(wr / wsum), (byte)(wg / wsum), (byte)(wb / wsum));
        if (n > 0)
            return Color.FromRgb((byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
        return DefaultAccent;
    }

    private void RefreshPlaybackInfo()
    {
        if (_currentSession == null)
        {
            _ = OnUi(() => IsPlaying = false);
            return;
        }

        try
        {
            var info = _currentSession.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _ = OnUi(() =>
            {
                IsPlaying = playing;
                // Reset interpolation baseline on play/pause change
                _smtcPositionUtc = DateTime.UtcNow;
            });
        }
        catch { }
    }

    private void RefreshTimeline()
    {
        if (_currentSession == null)
        {
            _ = OnUi(() =>
            {
                Duration = TimeSpan.Zero;
                Position = TimeSpan.Zero;
            });
            return;
        }

        try
        {
            var tl = _currentSession.GetTimelineProperties();
            var duration = tl.EndTime - tl.StartTime;
            var position = tl.Position - tl.StartTime;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            var startTime = tl.StartTime;
            _ = OnUi(() =>
            {
                Duration = duration;
                _smtcStartTime = startTime;
                _smtcPosition = position;
                _smtcPositionUtc = DateTime.UtcNow;
                Position = position;
            });
        }
        catch { }
    }

    private void TickPosition()
    {
        if (_currentSession == null || Duration == TimeSpan.Zero)
            return;

        if (!IsPlaying)
        {
            // Position already reflects the SMTC value
            return;
        }

        var elapsed = DateTime.UtcNow - _smtcPositionUtc;
        var newPos = _smtcPosition + elapsed;
        if (newPos > Duration) newPos = Duration;
        if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
        Position = newPos;
    }

    private async Task RefreshSourceAppIconAsync()
    {
        if (_currentSession == null)
        {
            await OnUi(() => SourceAppIcon = null);
            return;
        }

        var aumid = _currentSession.SourceAppUserModelId;
        BitmapSource? icon = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(aumid))
            {
                var appInfo = AppInfo.GetFromAppUserModelId(aumid);
                if (appInfo?.DisplayInfo != null)
                {
                    var logo = appInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
                    if (logo != null)
                        icon = await LoadBitmapAsync(logo);
                }
            }
        }
        catch
        {
            icon = null;
        }

        await OnUi(() => SourceAppIcon = icon);
    }

    private static async Task<BitmapSource?> LoadBitmapAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static Task OnUi(Action action)
    {
        var app = Application.Current;
        if (app == null)
        {
            action();
            return Task.CompletedTask;
        }
        return app.Dispatcher.InvokeAsync(action).Task;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
