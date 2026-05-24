using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TheGrandNotch.Animation;
using TheGrandNotch.Models;
using TheGrandNotch.Services;
using TheGrandNotch.Settings;

namespace TheGrandNotch;

public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Détection plein écran via WinEvent (EVENT_SYSTEM_FOREGROUND uniquement)
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // WM_WINDOWPOSCHANGED : envoyé DIRECTEMENT à notre WndProc quand notre Z-order change.
    // C'est la méthode la plus fiable pour détecter qu'un autre overlay nous a poussés en dessous.
    private const int WM_WINDOWPOSCHANGED = 0x0047;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }
    private const uint SWP_NOZORDER = 0x0004; // si ce flag est SET, le Z-order n'a PAS changé

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS       = 0x80000000u;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002u;
    private const uint ES_SYSTEM_REQUIRED  = 0x00000001u;

    private WinEventDelegate? _winEventProc;
    private IntPtr _winEventHookForeground;   // uniquement pour la détection plein écran
    private long _lastReassertTicks;
    private bool _isFullscreenHidden;
    private bool _selfReordering; // true pendant nos propres appels SetWindowPos → évite la boucle WM_WINDOWPOSCHANGED
    private DispatcherTimer? _zOrderRecoverTimer; // réaffirmation différée après poussée en dessous

    public static readonly DependencyProperty ExpansionProgressProperty =
        DependencyProperty.Register(
            nameof(ExpansionProgress),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnExpansionProgressChanged));

    public double ExpansionProgress
    {
        get => (double)GetValue(ExpansionProgressProperty);
        set => SetValue(ExpansionProgressProperty, value);
    }

    /// <summary>
    /// 0 → largeur de base, 1 → largeur de base + MiniExtraWidth.
    /// Animé quand un média devient actif/inactif en mode replié.
    /// Son callback appelle UpdateNotchVisual, ce qui évite tout conflit d'animation
    /// avec ExpansionProgress (les deux DPs alimentent le même calcul de largeur).
    /// </summary>
    public static readonly DependencyProperty MiniExpansionProgressProperty =
        DependencyProperty.Register(
            nameof(MiniExpansionProgress),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnMiniExpansionProgressChanged));

    public double MiniExpansionProgress
    {
        get => (double)GetValue(MiniExpansionProgressProperty);
        set => SetValue(MiniExpansionProgressProperty, value);
    }

    /// <summary>
    /// 0 → notch normale, 1 → notch élargie pour le HUD de volume (effet « live activity »
    /// : l'encoche grandit quand le HUD apparaît, comme le Dynamic Island).
    /// </summary>
    public static readonly DependencyProperty VolumeHudProgressProperty =
        DependencyProperty.Register(
            nameof(VolumeHudProgress),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnVolumeHudProgressChanged));

    public double VolumeHudProgress
    {
        get => (double)GetValue(VolumeHudProgressProperty);
        set => SetValue(VolumeHudProgressProperty, value);
    }

    private static void OnVolumeHudProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MainWindow)d).UpdateNotchVisual();

    /// <summary>
    /// 0 → taille normale, 1 → notch élargie pour le HUD batterie (branchement / alerte faible).
    /// </summary>
    public static readonly DependencyProperty BatteryHudProgressProperty =
        DependencyProperty.Register(
            nameof(BatteryHudProgress),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnBatteryHudProgressChanged));

    public double BatteryHudProgress
    {
        get => (double)GetValue(BatteryHudProgressProperty);
        set => SetValue(BatteryHudProgressProperty, value);
    }

    private static void OnBatteryHudProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MainWindow)d).UpdateNotchVisual();

    private const double GlowMargin = 70;
    private const double VolumeHudExtraWidth  = 96;  // élargissement notch HUD volume
    private const double BatteryHudExtraWidth = 88;  // élargissement notch HUD batterie
    private const double BatteryHudAutoHideMs = 3500;

    private readonly NotchSettings _settings;
    private readonly MediaService     _media     = new();
    private readonly LocalSendService _localSend = new();
    private VolumeService?    _volumeService;
    private DispatcherTimer?  _volumeHideTimer;
    private bool              _volumeHudVisible;
    private DispatcherTimer?  _calendarTimer;
    private readonly ICalService _icalService = new();
    private DateTime _selectedDay = DateTime.Today;   // jour affiché dans la vue calendrier complète
    private DispatcherTimer? _topmostTimer;
    private IntPtr _hwnd;
    private SettingsWindow? _settingsWindow;
    private readonly ObservableCollection<ShelfItem> _networkFiles = new();
    private bool  _showingPeerPicker;
    private IncomingTransfer? _pendingTransfer;
    private bool _keepAwake;

    // ── Audio output ──────────────────────────────────────────────────────
    private readonly AudioDeviceService _audioService = new();

    // ── Lava (blobs animés) ───────────────────────────────────────────────
    private bool _lavaRunning;  // true uniquement quand la notch est ouverte (t > 0)

    // ── Batterie ──────────────────────────────────────────────────────────
    private DispatcherTimer? _batteryHudTimer;
    private bool _batteryHudVisible;
    private bool _batteryAvailable;
    private bool _isCharging;
    private bool _batteryLow;
    private int  _lastBatteryPct = -1;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        DataContext = _media;
        ApplySettings();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        _media.PropertyChanged += OnMediaPropertyChanged;
        ProgressBarContainer.SizeChanged += (_, _) => UpdateProgressBarFill();
    }

    private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaService.ProgressFraction)
            || e.PropertyName == nameof(MediaService.Position)
            || e.PropertyName == nameof(MediaService.Duration))
        {
            UpdateProgressBarFill();
        }
        else if (e.PropertyName == nameof(MediaService.AlbumArt)
            || e.PropertyName == nameof(MediaService.AccentColor))
        {
            UpdateGlow();
        }
        else if (e.PropertyName == nameof(MediaService.IsPlaying))
        {
            if (_lavaRunning) ApplyLavaSpeed();   // vitesse synchro seulement si blobs actifs
            UpdatePlayPauseMorph(_media.IsPlaying, animate: true);
            ApplyEqualizer();
        }
        else if (e.PropertyName == nameof(MediaService.HasSession))
        {
            RefreshMiniOpacity();
            ApplyEqualizer();
            ApplyMiniExpansion(_media.HasSession);
        }
    }

    // ── Mini-mode replié : opacité (inverse du déploiement) + égaliseur ────

    /// <summary>
    /// Anime MiniExpansionProgress (0↔1) pour élargir / rétrécir la notch repliée
    /// de ±MiniExtraWidth/2 de chaque côté, donnant l'impression qu'un « module »
    /// vient d'être ajouté / retiré.
    /// </summary>
    private void ApplyMiniExpansion(bool active)
    {
        // Même famille de ressort que l'ouverture de la notch (TheBoringNotch : response 0.42, damping 0.8)
        var anim = new DoubleAnimation(
            active ? 1.0 : 0.0,
            TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = Motion.Snappy
        };
        BeginAnimation(MiniExpansionProgressProperty, anim);
    }

    /// <summary>
    /// Opacité du mini-mode : plein quand replié + média actif, s'efface dès le déploiement.
    /// Active aussi le hit-test (clic play/pause) uniquement quand la notch est fermée.
    /// </summary>
    private void RefreshMiniOpacity()
    {
        double t = ExpansionProgress;
        bool hasSession = _media.HasSession;
        MiniContent.Opacity = hasSession ? Math.Clamp(1 - t * 3.0, 0, 1) : 0.0;
        // Cliquable seulement quand replié et qu'un média existe
        MiniContent.IsHitTestVisible = hasSession && t < 0.25;
    }

    /// <summary>Anime les barres de l'égaliseur (lecture) ou les fait retomber bas (pause/arrêt).</summary>
    private void ApplyEqualizer()
    {
        if (_media.IsPlaying && _media.HasSession)
        {
            AnimateEqBar(EqBar1Scale, 0.30, 0.95, 0.52);
            AnimateEqBar(EqBar2Scale, 0.45, 1.00, 0.43);
            AnimateEqBar(EqBar3Scale, 0.25, 0.85, 0.61);
            AnimateEqBar(EqBar4Scale, 0.40, 0.90, 0.48);
        }
        else
        {
            SettleEqBar(EqBar1Scale);
            SettleEqBar(EqBar2Scale);
            SettleEqBar(EqBar3Scale);
            SettleEqBar(EqBar4Scale);
        }
    }

    private static void AnimateEqBar(ScaleTransform s, double min, double max, double sec)
    {
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = min,
            To = max,
            Duration = TimeSpan.FromSeconds(sec),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void SettleEqBar(ScaleTransform s)
        => s.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.22, TimeSpan.FromMilliseconds(220)));

    /// <summary>Morph play↔pause : le glyphe actif apparaît (fade + rebond d'échelle), l'autre s'efface.</summary>
    private void UpdatePlayPauseMorph(bool playing, bool animate)
    {
        var ease = Motion.Bouncy;
        var dur = TimeSpan.FromMilliseconds(animate ? 260 : 0);
        AnimateGlyph(PauseGlyph, PauseGlyphScale, playing, dur, ease);
        AnimateGlyph(PlayGlyph, PlayGlyphScale, !playing, dur, ease);
    }

    private static void AnimateGlyph(UIElement glyph, ScaleTransform scale, bool show, TimeSpan dur, IEasingFunction ease)
    {
        glyph.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1.0 : 0.0, dur));
        double to = show ? 1.0 : 0.6;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, dur) { EasingFunction = show ? ease : null });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, dur) { EasingFunction = show ? ease : null });
    }

    private void UpdateProgressBarFill()
    {
        if (_isScrubbing)
            return;

        double available = ProgressBarContainer.ActualWidth;
        if (available <= 0)
        {
            ProgressBarFill.Width = 0;
            return;
        }
        ProgressBarFill.Width = available * _media.ProgressFraction;
    }

    private bool _isScrubbing;

    private void OnProgressBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_media.HasSession)
            return;
        _isScrubbing = true;
        ((UIElement)sender).CaptureMouse();
        SetScrubVisual(true);
        UpdateScrubPreview(e.GetPosition(ProgressBarContainer).X);
    }

    private void OnProgressBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isScrubbing)
            return;
        UpdateScrubPreview(e.GetPosition(ProgressBarContainer).X);
    }

    private async void OnProgressBarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbing)
            return;
        _isScrubbing = false;
        ((UIElement)sender).ReleaseMouseCapture();
        SetScrubVisual(false);
        double fraction = FractionFromX(e.GetPosition(ProgressBarContainer).X);
        await _media.SeekToFractionAsync(fraction);
    }

    private void SetScrubVisual(bool active)
    {
        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        ProgressBarsScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(active ? 2.6 : 1.0, duration) { EasingFunction = ease });

        ScrubThumb.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(active ? 1.0 : 0.0, duration));
    }

    private double FractionFromX(double x)
    {
        double width = ProgressBarContainer.ActualWidth;
        if (width <= 0)
            return 0;
        return Math.Clamp(x / width, 0, 1);
    }

    private void UpdateScrubPreview(double x)
    {
        double width = ProgressBarContainer.ActualWidth;
        double fraction = FractionFromX(x);
        double fillWidth = width * fraction;
        ProgressBarFill.Width = fillWidth;
        ScrubThumb.Margin = new Thickness(fillWidth - ScrubThumb.Width / 2.0, 0, 0, 0);
    }

    private void ApplySettings()
    {
        // Marge autour de la notch pour laisser respirer le halo (sinon il serait coupé
        // par les bords de la fenêtre).
        Width = _settings.ExpandedWidth + GlowMargin * 2;
        Height = CurrentExpandedHeight + _settings.TopOffset + GlowMargin;

        NotchBorder.Width = _settings.Width;
        NotchBorder.Height = _settings.Height;
        NotchBorder.Margin = new Thickness(0, _settings.TopOffset, 0, 0);
        NotchBorder.CornerRadius = new CornerRadius(
            0, 0,
            _settings.CornerRadiusBottom,
            _settings.CornerRadiusBottom);

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(_settings.BackgroundColor);
            NotchBorder.Background = new SolidColorBrush(color);
        }
        catch
        {
            NotchBorder.Background = new SolidColorBrush(Colors.Black);
        }
    }

    private static void OnExpansionProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MainWindow)d).UpdateNotchVisual();

    private static void OnMiniExpansionProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MainWindow)d).UpdateNotchVisual();

    private const double GlowPadding = 16;

    private void UpdateNotchVisual()
    {
        double t    = ExpansionProgress;
        double mt   = MiniExpansionProgress;

        double vh   = VolumeHudProgress;
        double bh   = BatteryHudProgress;

        // Largeur de base modulée par l'expansion mini + HUD volume + HUD batterie
        double miniW = Lerp(_settings.Width, _settings.Width + _settings.MiniExtraWidth, mt)
                       + vh * VolumeHudExtraWidth
                       + bh * BatteryHudExtraWidth;
        double w    = Lerp(miniW, _settings.ExpandedWidth, t);
        double h    = Lerp(_settings.Height, CurrentExpandedHeight, t);
        NotchBorder.Width = w;
        NotchBorder.Height = h;
        double radius = Lerp(_settings.CornerRadiusBottom, _settings.ExpandedCornerRadius, t);
        NotchBorder.CornerRadius = new CornerRadius(0, 0, radius, radius);
        MediaContent.Opacity = Math.Clamp((t - 0.45) * 1.9, 0, 1);
        MediaContent.IsHitTestVisible = t > 0.65;
        RefreshMiniOpacity();
        // Café mini : recule devant l'égaliseur quand un média joue, colle au bord sinon
        CoffeeMiniIndicator.Margin = new Thickness(0, 0, _media.HasSession ? 50 : 14, 0);
        CoffeeMiniIndicator.Opacity = _keepAwake ? Math.Clamp(1 - t * 3.0, 0, 1) : 0.0;

        // Batterie mini : juste à gauche du café quand les deux sont actifs
        bool showBattMini = _batteryAvailable && (_isCharging || _batteryLow);
        double coffeeRight = _media.HasSession ? 50 : 14;
        double battRight   = _keepAwake ? coffeeRight + 13 + 4 : coffeeRight;
        BatteryMiniIndicator.Margin = new Thickness(0, 0, battRight, 0);
        BatteryMiniIndicator.Opacity = showBattMini ? Math.Clamp(1 - t * 3.0, 0, 1) : 0.0;

        // GlowLayer toujours Opacity="0" (fonctionnalité réservée) — pas de mise à jour géométrique.
        UpdateGlow(t);

        // Lava : démarre au premier open, se coupe une fois totalement replié.
        if (t > 0.02)
            ResumeLava();
        else if (t < 0.005)
            PauseLava();
    }

    /// <summary>
    /// Ronds flous colorés DANS la notch (ambiance accordée à la pochette).
    /// Le halo externe (GlowLayer) est conservé mais désactivé — réservé à un futur usage
    /// (déclencheur type Siri).
    /// </summary>
    private void UpdateGlow(double? expansion = null)
    {
        double t = Math.Clamp(expansion ?? ExpansionProgress, 0, 1);
        bool hasArt = _media.AlbumArt != null;

        // Couleurs mises à jour uniquement si une pochette existe (évite ShiftHue + Vivify à vide).
        // GlowLayer reste Opacity="0" (halo externe réservé) → aucune mise à jour de GlowBrush.
        if (hasArt)
        {
            Blob1Brush.Color = ShiftHue(Vivify(_media.AccentLeft), -30);
            Blob2Brush.Color = Vivify(_media.AccentCenter);
            Blob3Brush.Color = ShiftHue(Vivify(_media.AccentRight), 30);
        }
        InnerGlow.Opacity = hasArt && _currentPage == NotchPage.Home ? t * 0.55 : 0.0;
    }

    /// <summary>Décale la teinte (HSV) d'une couleur de <paramref name="deltaDeg"/> degrés.</summary>
    private static Color ShiftHue(Color c, double deltaDeg)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double v = max;
        double s = max <= 0 ? 0 : d / max;

        double h;
        if (d == 0) h = 0;
        else if (max == r) h = (((g - b) / d) % 6 + 6) % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h = h * 60 + deltaDeg;
        h = ((h % 360) + 360) % 360;

        double cc = v * s;
        double x = cc * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - cc;
        double rr = 0, gg = 0, bb = 0;
        if (h < 60) { rr = cc; gg = x; }
        else if (h < 120) { rr = x; gg = cc; }
        else if (h < 180) { gg = cc; bb = x; }
        else if (h < 240) { gg = x; bb = cc; }
        else if (h < 300) { rr = x; bb = cc; }
        else { rr = cc; bb = x; }

        return Color.FromRgb(
            (byte)Math.Round((rr + m) * 255),
            (byte)Math.Round((gg + m) * 255),
            (byte)Math.Round((bb + m) * 255));
    }

    private const double LavaPlayingSpeed = 2.3;   // bulles plus rapides quand le média joue

    /// <summary>
    /// Démarre les blobs si ce n'est pas déjà fait. Appelé lors de la première ouverture
    /// de la notch (t > 0) — inutile de les animer quand la notch est repliée.
    /// </summary>
    private void ResumeLava()
    {
        if (_lavaRunning) return;
        _lavaRunning = true;
        ApplyLavaSpeed();
    }

    /// <summary>Gèle les blobs (notch complètement fermée). Ils repartent proprement au prochain open.</summary>
    private void PauseLava()
    {
        if (!_lavaRunning) return;
        _lavaRunning = false;
        Blob1T.BeginAnimation(TranslateTransform.XProperty, null);
        Blob1T.BeginAnimation(TranslateTransform.YProperty, null);
        Blob2T.BeginAnimation(TranslateTransform.XProperty, null);
        Blob2T.BeginAnimation(TranslateTransform.YProperty, null);
        Blob3T.BeginAnimation(TranslateTransform.XProperty, null);
        Blob3T.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>(Ré)applique les animations des bulles à la vitesse correspondant à l'état de lecture.</summary>
    private void ApplyLavaSpeed()
    {
        double speed = _media.IsPlaying ? LavaPlayingSpeed : 1.0;
        AnimateBlob(Blob1T, 25, 18, 7.5 / speed, 9.3 / speed);
        AnimateBlob(Blob2T, 28, 20, 8.8 / speed, 6.7 / speed);
        AnimateBlob(Blob3T, 25, 18, 6.4 / speed, 8.1 / speed);
    }

    private static void AnimateBlob(TranslateTransform t, double dx, double dy, double secX, double secY)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        bool initializing = t.X == 0 && t.Y == 0;

        // À l'init les blobs sont à l'origine → on fixe From pour couvrir la plage complète.
        // Lors d'un changement de vitesse en cours d'animation, on laisse WPF partir de la
        // valeur courante (From = null = snapshot à l'instant du BeginAnimation) pour éviter
        // tout saut visuel.
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = initializing ? -dx : (double?)null,
            To   = initializing ? dx  : (t.X > 0 ? -dx : dx),
            Duration = TimeSpan.FromSeconds(secX),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = initializing ? -dy : (double?)null,
            To   = initializing ? dy  : (t.Y > 0 ? -dy : dy),
            Duration = TimeSpan.FromSeconds(secY),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        });
    }

    /// <summary>
    /// Rend une couleur plus vive (saturation + luminosité boostées) pour un halo éclatant.
    /// </summary>
    private static Color Vivify(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double v = max;
        double s = max <= 0 ? 0 : (max - min) / max;
        double d = max - min;

        double h;
        if (d == 0) h = 0;
        else if (max == r) h = (((g - b) / d) % 6 + 6) % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;

        s = Math.Min(1.0, s * 1.45 + 0.12);
        v = Math.Min(1.0, v * 1.10 + 0.05);

        double cc = v * s;
        double x = cc * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - cc;
        double rr = 0, gg = 0, bb = 0;
        if (h < 60) { rr = cc; gg = x; }
        else if (h < 120) { rr = x; gg = cc; }
        else if (h < 180) { gg = cc; bb = x; }
        else if (h < 240) { gg = x; bb = cc; }
        else if (h < 300) { rr = x; bb = cc; }
        else { rr = cc; bb = x; }

        return Color.FromRgb(
            (byte)Math.Round((rr + m) * 255),
            (byte)Math.Round((gg + m) * 255),
            (byte)Math.Round((bb + m) * 255));
    }

    /// <summary>Clic sur la zone mini repliée (pochette + égaliseur) → bascule lecture/pause.</summary>
    private async void OnMiniContentClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // n'ouvre pas la notch
        await _media.PlayPauseAsync();
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e) => await _media.PlayPauseAsync();

    private async void OnNextClick(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private async void OnPreviousClick(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    // Specs reprises de TheBoringNotch (SwiftUI spring → WPF) :
    //   ouverture : spring(response 0.42, damping 0.8) → léger dépassement (BackEase)
    //   fermeture : spring(response 0.45, damping 1.0) → AUCUN dépassement (CubicEase)
    private void AnimateExpansion(double target)
    {
        bool opening = target > 0.5;

        // L'ouverture rebondit (ressort sous-amorti), la fermeture se pose sans dépassement.
        // Le réglage EasingAmplitude pilote la « rondeur » du rebond : plus il est élevé,
        // plus l'amortissement ζ baisse → rebond plus marqué.
        double zeta = Math.Clamp(0.85 - Math.Max(0, _settings.EasingAmplitude) * 0.7, 0.40, 0.95);

        var anim = new DoubleAnimation
        {
            To = target,
            // Fermeture un peu plus longue (damping critique = settle plus doux)
            Duration = TimeSpan.FromMilliseconds(opening
                ? _settings.AnimationDurationMs
                : _settings.AnimationDurationMs * 1.3),
            EasingFunction = opening ? Motion.Spring(zeta) : Motion.Smooth,
            FillBehavior = FillBehavior.HoldEnd
        };
        BeginAnimation(ExpansionProgressProperty, anim);
    }

    private void OnNotchMouseEnter(object sender, MouseEventArgs e)
    {
        // Nettoyage défensif : si DragOverlay est resté ouvert sans drag actif
        // (drag abandonné hors fenêtre sans DragLeave), on le libère ici.
        if (DragOverlay.IsHitTestVisible && _pendingTransfer is null)
            HideDragOverlay(collapse: false);
        HideVolumeHud();
        HideBatteryHud();
        AnimateExpansion(1.0);
    }

    private void OnNotchMouseLeave(object sender, MouseEventArgs e)
    {
        // Ne pas replier si un transfert entrant est en attente de réponse
        if (_pendingTransfer is null)
            AnimateExpansion(0.0);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProcHook);

        StartTopmostReassertionTimer();
        StartTopmostEventHook();
        Closed += (_, _) => StopTopmostEventHook();
    }

    private void StartTopmostEventHook()
    {
        _winEventProc = OnWinEvent;
        // Hook minimal : uniquement pour détecter le passage en plein écran.
        // La réaffirmation Z-order se fait via WM_WINDOWPOSCHANGED dans WndProcHook.
        _winEventHookForeground = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void StopTopmostEventHook()
    {
        if (_winEventHookForeground != IntPtr.Zero) { UnhookWinEvent(_winEventHookForeground); _winEventHookForeground = IntPtr.Zero; }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            // CheckFullscreen reste la raison principale du hook.
            // On planifie aussi une recovery : MyDockFinder (et similaires) réagissent souvent
            // à EVENT_SYSTEM_FOREGROUND en se réaffirmant — notamment quand notre propre fenêtre
            // reçoit le focus via SetForegroundWindow (bouton texte, etc.).
            Dispatcher.InvokeAsync(() =>
            {
                CheckFullscreen(hwnd);
                if (!_selfReordering)
                    ScheduleZOrderRecovery();
            });
        }
    }

    // Classes système qui couvrent l'écran mais ne sont pas des apps plein écran
    private static readonly HashSet<string> _desktopClasses =
        new(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW", "Shell_TrayWnd" };

    /// <summary>Masque ou affiche la notch selon que la fenêtre <paramref name="hwnd"/> est plein écran.</summary>
    private void CheckFullscreen(IntPtr hwnd)
    {
        // Notre propre fenêtre : toujours visible
        if (hwnd == IntPtr.Zero || hwnd == _hwnd)
        {
            if (_isFullscreenHidden)
            {
                _isFullscreenHidden = false;
                ApplyFullscreenVisibility(true);
            }
            return;
        }

        // Exclure le bureau et la barre des tâches
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        if (_desktopClasses.Contains(sb.ToString()))
        {
            if (_isFullscreenHidden) { _isFullscreenHidden = false; ApplyFullscreenVisibility(true); }
            return;
        }

        if (!GetWindowRect(hwnd, out var rect))
            return;

        int sw = (int)SystemParameters.PrimaryScreenWidth;
        int sh = (int)SystemParameters.PrimaryScreenHeight;
        bool fullscreen = rect.Left <= 0 && rect.Top <= 0
                       && rect.Right >= sw && rect.Bottom >= sh;

        if (fullscreen == _isFullscreenHidden)
            return;

        _isFullscreenHidden = fullscreen;
        ApplyFullscreenVisibility(!fullscreen);
    }

    /// <summary>Fade-in/out de la notch lors d'une transition plein écran.</summary>
    private void ApplyFullscreenVisibility(bool visible)
    {
        NotchBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(visible ? 1.0 : 0.0,
                TimeSpan.FromMilliseconds(visible ? 380 : 180)));
    }

    /// <summary>Ouvre/ferme la fenêtre Paramètres (toggle via la roue crantée).</summary>
    private void ToggleSettings()
    {
        if (_settingsWindow != null)
            _settingsWindow.Close();
        else
            OpenSettings();
    }

    private void OpenSettings()
    {
        _settingsWindow = new SettingsWindow(_settings, ApplyLiveSettings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Réapplique les réglages en direct (appelé depuis la fenêtre de paramètres).
    /// La notch n'est PAS forcée déployée → on voit la taille repliée changer en temps réel.
    /// </summary>
    public void ApplyLiveSettings()
    {
        ApplySettings();
        UpdateNotchVisual();
        PositionAtTopCenter();
        RestartTopmostTimer();
        UpdateProgressBarFill();
    }

    private void RestartTopmostTimer()
    {
        _topmostTimer?.Stop();
        _topmostTimer = null;
        StartTopmostReassertionTimer();
        ReassertTopmost();
    }

    private void StartTopmostReassertionTimer()
    {
        if (_settings.TopmostReassertionMs <= 0)
            return;

        // Plancher à 100 ms. Avec la vérification du Z-order dans ReassertTopmost,
        // le tick ne fait qu'un GetWindow si la notch est déjà au sommet — overhead négligeable.
        // Les WinEvent hooks assurent la réactivité immédiate ; ce timer est le filet de sécurité
        // contre les apps qui reassertent silencieusement (sans activer leur fenêtre).
        int interval = Math.Max(100, _settings.TopmostReassertionMs);
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(interval)
        };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _topmostTimer.Start();

        // Rafraîchit l'indicateur temps-réel et les opacités "événement passé" chaque minute
        _calendarTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _calendarTimer.Tick += async (_, _) => await RefreshICalEventsAsync();
        _calendarTimer.Start();

        // Met à jour CalFullStatus + visibilité bouton dès que le statut ICS change
        _icalService.StatusChanged += () => Dispatcher.Invoke(UpdateCalendarStatusUI);
    }

    /// <summary>
    /// Planifie une réaffirmation différée du Z-order (30 ms).
    /// Appelé depuis WM_WINDOWPOSCHANGED quand notre Z-order vient d'être modifié.
    /// Le délai laisse MyDockFinder (ou tout autre overlay) terminer sa propre réaffirmation,
    /// puis on se replace au-dessus en DERNIER — stratégie "avoir le dernier mot".
    /// Chaque nouvel appel reporte l'échéance (coalesce les rafales d'événements).
    /// </summary>
    private void ScheduleZOrderRecovery()
    {
        if (_zOrderRecoverTimer == null)
        {
            _zOrderRecoverTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _zOrderRecoverTimer.Tick += (_, _) =>
            {
                _zOrderRecoverTimer.Stop();
                ReassertTopmost();
            };
        }
        // On ne remet PAS le timer à zéro s'il est déjà en cours :
        // cela évite la « famine » quand MDF réaffirme en rafale rapide
        // (le timer expirerait toujours dans les 40 ms suivant le PREMIER événement).
        if (!_zOrderRecoverTimer.IsEnabled)
            _zOrderRecoverTimer.Start();
    }

    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        _lastReassertTicks = Environment.TickCount64;
        // _selfReordering = true pendant l'appel : WM_WINDOWPOSCHANGED qui en résulte
        // sera ignoré par notre handler, coupant la boucle infinie.
        _selfReordering = true;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _selfReordering = false;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGED)
        {
            // Envoyé à notre fenêtre quand son Z-order vient de changer.
            // On ignore si c'est nous qui avons appelé SetWindowPos (_selfReordering)
            // pour couper la boucle infinie : notre SetWindowPos → WM_WINDOWPOSCHANGED → SetWindowPos → ...
            if (!_selfReordering)
            {
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((wp.flags & SWP_NOZORDER) == 0)
                    ScheduleZOrderRecovery();
            }
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST)
            return IntPtr.Zero;

        // Toujours transparent quand masqué pour un plein écran
        if (_isFullscreenHidden)
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }

        int lp = lParam.ToInt32();
        short sx = (short)(lp & 0xFFFF);
        short sy = (short)((lp >> 16) & 0xFFFF);

        var windowPoint = PointFromScreen(new Point(sx, sy));

        double borderWidth = NotchBorder.ActualWidth;
        double borderHeight = NotchBorder.ActualHeight;
        double borderLeft = (ActualWidth - borderWidth) / 2.0;
        double borderTop = _settings.TopOffset;
        double borderRight = borderLeft + borderWidth;
        double borderBottom = borderTop + borderHeight;

        bool insideVisibleNotch =
            windowPoint.X >= borderLeft &&
            windowPoint.X <= borderRight &&
            windowPoint.Y >= borderTop &&
            windowPoint.Y <= borderBottom;

        handled = true;
        return new IntPtr(insideVisibleNotch ? HTCLIENT : HTTRANSPARENT);
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        _icalService.Load();
        PopulateCalendar();
        UpdateCalendarStatusUI();
        if (_icalService.IsConfigured)
            _ = RefreshICalEventsAsync();
        StartBattery();
        // Lava démarre paresseusement au premier open (ResumeLava dans UpdateNotchVisual)
        AudioDeviceList.ItemsSource = _audioService.Devices;
        MicDeviceList.ItemsSource   = _audioService.CaptureDevices;
        NetworkFilesList.ItemsSource = _networkFiles;
        _networkFiles.CollectionChanged += (_, _) => { UpdateNetworkFilesVisibility(); RefreshNetworkLayout(); UpdateNetworkBadge(); };

        PeerList.ItemsSource = _localSend.Peers;
        _localSend.Peers.CollectionChanged += (_, _) => { UpdatePeerVisibility(); RefreshNetworkLayout(); };
        _localSend.TransferRequested += OnTransferRequested;
        _localSend.FileReceived      += OnFileReceived;
        _localSend.Start();

        Closed += (_, _) =>
        {
            _localSend.Dispose();
            _volumeService?.Dispose();
            _audioService.Dispose();
            _batteryHudTimer?.Stop();
            _zOrderRecoverTimer?.Stop();
            if (_keepAwake) SetThreadExecutionState(ES_CONTINUOUS);
        };

        // Slide-in depuis le haut au démarrage
        NotchSlideT.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        UpdatePlayPauseMorph(_media.IsPlaying, animate: false);
        ApplyEqualizer();
        RefreshMiniOpacity();
        await _media.InitializeAsync();

        _volumeService = new VolumeService(Dispatcher);
        _volumeService.VolumeChanged += OnVolumeKeyPressed;
    }

    // ===== Onglets de la barre du haut =====

    private static readonly SolidColorBrush TabActive   = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TabInactive = new(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private enum NotchPage { Home, Network, Audio, Calendar }
    private NotchPage _currentPage = NotchPage.Home;

    private void OnHomeTabClick(object sender, RoutedEventArgs e)     => ShowPage(NotchPage.Home);
    private void OnNetworkTabClick(object sender, RoutedEventArgs e)  => ShowPage(NotchPage.Network);
    private void OnAudioTabClick(object sender, RoutedEventArgs e)    => ShowPage(NotchPage.Audio);
    private void OnCalendarTabClick(object sender, RoutedEventArgs e) => ShowPage(NotchPage.Calendar);

    /// <summary>Clic sur le mini-calendrier de l'Accueil → ouvre l'onglet calendrier complet.</summary>
    private void OnMiniCalendarClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowPage(NotchPage.Calendar);
    }

    /// <summary>Clic sur le bouton calendrier → ouvre la saisie d'URL iCal.</summary>
    private async void OnCalendarConnectClick(object sender, RoutedEventArgs e)
    {
        var currentUrl = _icalService.IsConfigured
            ? File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TheGrandNotch", "ical_url.txt"))
                    ? File.ReadAllText(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "TheGrandNotch", "ical_url.txt")).Trim()
                    : ""
            : "";

        var dialog = new ICalUrlDialog(currentUrl);
        if (dialog.ShowDialog() != true) return;

        if (dialog.Disconnected)
        {
            _icalService.Disconnect();
            BuildTimeline();
        }
        else
        {
            _icalService.SaveUrl(dialog.ResultUrl);
            await RefreshICalEventsAsync();
        }
        UpdateCalendarStatusUI();
    }

    /// <summary>Récupère les événements iCal et reconstruit les deux calendriers.</summary>
    private async Task RefreshICalEventsAsync()
    {
        if (_icalService.IsConfigured)
        {
            var events = await _icalService.GetTodayEventsAsync();
            BuildTimeline(events.Count > 0 ? events : []);
        }
        else
        {
            BuildTimeline(); // données de démo
        }
    }

    /// <summary>Met à jour le texte de statut et la visibilité du bouton dans CalendarView.</summary>
    private void UpdateCalendarStatusUI()
    {
        CalFullStatus.Text = _icalService.StatusText;

        CalFullStatus.Foreground = _icalService.Status switch
        {
            ICalStatus.Connected => new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
            ICalStatus.Error     => new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            ICalStatus.Loading   => new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            _                    => new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
        };

        // Bouton visible quand non connecté ; quand connecté, affiche "Modifier"
        CalConnectBtn.Visibility = Visibility.Visible;
    }

    /// <summary>Clic sur un périphérique audio → bascule le défaut + animation de feedback.</summary>
    private void OnAudioDeviceClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AudioDevice device) return;
        e.Handled = true;
        if (device.IsDefault) return;

        // Feedback visuel : éclat lumineux sur l'item cliqué
        var ease = Motion.Bouncy;
        var pop  = new DoubleAnimation { From = 0.45, To = 1.0,
            Duration = TimeSpan.FromMilliseconds(340), EasingFunction = ease };
        fe.BeginAnimation(OpacityProperty, pop);

        // Bascule + refresh de la liste (ObservableCollection déclenche le re-rendu)
        if (_audioService.SetDefault(device.Id))
            _audioService.Refresh();
    }

    private void ShowPage(NotchPage page)
    {
        var oldPage = _currentPage;
        _currentPage = page;

        HomeIcon.Fill     = page == NotchPage.Home     ? TabActive : TabInactive;
        NetworkIcon.Fill  = page == NotchPage.Network  ? TabActive : TabInactive;
        AudioIcon.Fill    = page == NotchPage.Audio    ? TabActive : TabInactive;
        CalendarIcon.Fill = page == NotchPage.Calendar ? TabActive : TabInactive;

        if (page == NotchPage.Audio)
            _audioService.Refresh();

        if (oldPage == page)
        {
            ViewFor(page).view.Visibility = Visibility.Visible;
        }
        else
        {
            var (oldView, oldT) = ViewFor(oldPage);
            var (newView, newT) = ViewFor(page);
            int dir = page > oldPage ? 1 : -1;   // sens de glissement selon l'ordre des onglets
            AnimatePageTransition(oldView, oldT, newView, newT, dir);
        }

        UpdateGlow();
        RefreshNetworkLayout();

        // La vue complète n'a sa hauteur réelle qu'une fois affichée → re-rendu pour
        // recaler le scroll automatique sur l'heure actuelle.
        if (page == NotchPage.Calendar)
            BuildTimeline();
    }

    private (FrameworkElement view, TranslateTransform t) ViewFor(NotchPage p) => p switch
    {
        NotchPage.Home     => (HomeView,     HomeViewT),
        NotchPage.Audio    => (AudioView,    AudioViewT),
        NotchPage.Calendar => (CalendarView, CalendarViewT),
        _                  => (NetworkView,  NetworkViewT),
    };

    /// <summary>Crossfade + glissement horizontal entre deux pages (dir = +1 vers la droite, -1 vers la gauche).</summary>
    private void AnimatePageTransition(FrameworkElement oldView, TranslateTransform oldT,
                                      FrameworkElement newView, TranslateTransform newT, int dir)
    {
        const double offset = 16;
        // Transitions « smooth(0.35) » de TheBoringNotch : glissement + fondu fluides
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var inDur  = TimeSpan.FromMilliseconds(300);
        var outDur = TimeSpan.FromMilliseconds(190);

        // Page entrante : depuis dir*offset → 0, opacité 0 → 1
        newView.BeginAnimation(OpacityProperty, null);
        newT.BeginAnimation(TranslateTransform.XProperty, null);
        newView.Visibility = Visibility.Visible;
        newView.Opacity = 0;
        newT.X = dir * offset;
        newView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, inDur) { EasingFunction = easeOut });
        newT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, inDur) { EasingFunction = easeOut });

        // Page sortante : 0 → -dir*offset, opacité 1 → 0, puis repli
        var fade = new DoubleAnimation(0, outDur) { EasingFunction = easeOut };
        fade.Completed += (_, _) =>
        {
            // Ne replier que si l'utilisateur n'est pas revenu sur cette page entre-temps
            if (!ReferenceEquals(ViewFor(_currentPage).view, oldView))
            {
                oldView.Visibility = Visibility.Collapsed;
                oldView.BeginAnimation(OpacityProperty, null);
                oldView.Opacity = 1;
                oldT.BeginAnimation(TranslateTransform.XProperty, null);
                oldT.X = 0;
            }
        };
        oldView.BeginAnimation(OpacityProperty, fade);
        oldT.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-dir * offset, outDur) { EasingFunction = easeOut });
    }

    // ===== Étagère (page Boîte) : drag & drop de fichiers =====

    private void OnNotchDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            AnimateExpansion(1.0);
            ShowDragOverlay();
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnNotchDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave se déclenche aussi en passant sur une zone enfant : on ne masque que si
        // le curseur a réellement quitté les limites de la notch.
        var p = e.GetPosition(NotchBorder);
        if (p.X < 0 || p.Y < 0 || p.X > NotchBorder.ActualWidth || p.Y > NotchBorder.ActualHeight)
            HideDragOverlay(collapse: true);
    }

    private void OnNotchDrop(object sender, DragEventArgs e)
    {
        // Fallback : drop hors des deux zones (interstice) → comportement par défaut (envoi).
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
                AddToNetworkFile(path);
            ShowPage(NotchPage.Network);
        }
        HideDragOverlay(collapse: false);
        e.Handled = true;
    }

    // ── Overlay double drop-zone : Garder (Boîte) / Envoyer (Réseau) ──────

    private static readonly SolidColorBrush ZoneIdleBg   = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ZoneIdleBd   = new(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ZoneActiveBg = new(Color.FromArgb(0x33, 0x66, 0xCC, 0xFF));
    private static readonly SolidColorBrush ZoneActiveBd = new(Color.FromArgb(0x99, 0x66, 0xCC, 0xFF));

    private void ShowDragOverlay()
    {
        if (DragOverlay.IsHitTestVisible)
            return;
        DragOverlay.IsHitTestVisible = true;
        DragOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void HideDragOverlay(bool collapse)
    {
        DragOverlay.IsHitTestVisible = false;
        DragOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160)));
        SetZoneActive(DropKeep, DropKeepScale, false);
        SetZoneActive(DropSend, DropSendScale, false);
        if (collapse && _pendingTransfer is null)
            AnimateExpansion(0.0);
    }

    private void OnDropKeepEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        SetZoneActive(DropKeep, DropKeepScale, true);
        SetZoneActive(DropSend, DropSendScale, false);
        e.Handled = true;
    }

    private void OnDropSendEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        SetZoneActive(DropSend, DropSendScale, true);
        SetZoneActive(DropKeep, DropKeepScale, false);
        e.Handled = true;
    }

    private void OnDropZoneLeave(object sender, DragEventArgs e)
    {
        if (sender == DropKeep)      SetZoneActive(DropKeep, DropKeepScale, false);
        else if (sender == DropSend) SetZoneActive(DropSend, DropSendScale, false);
    }

    private void OnDropKeep(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
                AddToNetworkFile(path);
            ShowPage(NotchPage.Network);
        }
        HideDragOverlay(collapse: false);
        e.Handled = true;
    }

    private void OnDropSend(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
                AddToNetworkFile(path);
            ShowPage(NotchPage.Network);
        }
        HideDragOverlay(collapse: false);
        e.Handled = true;
    }

    private static void SetZoneActive(Border zone, ScaleTransform scale, bool active)
    {
        zone.Background  = active ? ZoneActiveBg : ZoneIdleBg;
        zone.BorderBrush = active ? ZoneActiveBd : ZoneIdleBd;
        double to = active ? 1.04 : 1.0;
        var dur = TimeSpan.FromMilliseconds(150);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
    }

    // ===== Onglet Réseau : fichiers en attente d'envoi =====

    private void AddToNetworkFile(string path, string? displayName = null)
    {
        if (string.IsNullOrEmpty(path) || _networkFiles.Any(f => f.Path == path))
            return;
        string name = displayName ?? (Directory.Exists(path)
            ? new DirectoryInfo(path).Name
            : System.IO.Path.GetFileName(path));
        _networkFiles.Add(new ShelfItem { Path = path, Name = name, Icon = GetFileIcon(path) });
    }

    private void OnNetworkFileRemove(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ShelfItem item)
        {
            _networkFiles.Remove(item);
            e.Handled = true;
        }
    }

    private void UpdateNetworkFilesVisibility()
    {
        bool has = _networkFiles.Count > 0;
        NetworkFilesScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SendHint.Visibility           = has ? Visibility.Collapsed : Visibility.Visible;
        SendArrowButton.IsEnabled     = has;
    }

    /// <summary>Met à jour le badge sur l'icône de l'onglet Réseau.</summary>
    private void UpdateNetworkBadge()
    {
        int count = _networkFiles.Count;
        NetworkBadgeText.Text = count > 9 ? "9+" : count.ToString();
        NetworkBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePeerVisibility()
    {
        bool has = _localSend.Peers.Count > 0;
        PeerEmptyText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        PeerList.Visibility      = has ? Visibility.Visible   : Visibility.Collapsed;
    }

    // ── Peer picker (sous-menu appareils) ────────────────────────────────

    private void OnShowPeerPicker(object sender, RoutedEventArgs e) => ShowPeerPicker();
    private void OnHidePeerPicker(object sender, RoutedEventArgs e) => HidePeerPicker();

    private void ShowPeerPicker()
    {
        _showingPeerPicker = true;
        SharePanel.Visibility       = Visibility.Collapsed;
        PeerPickerPanel.Visibility  = Visibility.Visible;
        RefreshNetworkLayout();
    }

    private void HidePeerPicker()
    {
        _showingPeerPicker = false;
        PeerPickerPanel.Visibility  = Visibility.Collapsed;
        SharePanel.Visibility       = Visibility.Visible;
        RefreshNetworkLayout();
    }

    // ── Drop zones internes (notch déjà ouverte) ─────────────────────────

    private void OnSendZoneDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSendZoneDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var path in paths)
                AddToNetworkFile(path);
        e.Handled = true;
    }

    // ── Hauteur dynamique (onglet Réseau) ─────────────────────────────────

    // Hauteur déployée de l'onglet calendrier : plus haute que le défaut pour voir
    // plusieurs heures de timeline d'un coup (passé proche + heures à venir).
    private const double CalendarExpandedHeight = 260;

    /// <summary>Hauteur déployée effective : fixe sur Home/Shelf, plus haute sur Calendar, calculée selon les pairs sur Network.</summary>
    private double CurrentExpandedHeight => _currentPage switch
    {
        NotchPage.Network  => GetNetworkExpandedHeight(),
        NotchPage.Calendar => CalendarExpandedHeight,
        _                  => _settings.ExpandedHeight,
    };

    private double GetNetworkExpandedHeight()
    {
        const double baseH       = 58;   // barre du haut + marges
        const double peerRowH    = 42;   // hauteur d'une ligne pair
        const double textPanelH  = 46;   // panneau saisie texte
        const double incomingH   = 84;   // panneau transfert entrant
        const double shareAreaH  = 120;  // deux colonnes (Boîte + Envoi)
        const double pickerHdrH  = 36;   // en-tête du peer picker

        double incomingZone = (IncomingPanel?.Visibility  == Visibility.Visible) ? incomingH : 0;
        double textZone     = (TextInputPanel?.Visibility == Visibility.Visible) ? textPanelH : 0;

        double mainZone = _showingPeerPicker
            ? pickerHdrH + Math.Max(40, _localSend.Peers.Count * peerRowH)
            : shareAreaH;

        return Math.Max(_settings.ExpandedHeight, baseH + incomingZone + mainZone + textZone);
    }

    /// <summary>Redimensionne la fenêtre et rafraîchit la notch quand le contenu réseau change.</summary>
    private void RefreshNetworkLayout()
    {
        double windowH = CurrentExpandedHeight + _settings.TopOffset + GlowMargin;
        if (Math.Abs(Height - windowH) > 0.5)
        {
            Height = windowH;
            PositionAtTopCenter();
        }
        if (ExpansionProgress > 0)
            UpdateNotchVisual();
    }

    // ── Envoi vers un pair ────────────────────────────────────────────────

    private async void OnPeerClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LocalSendPeer peer)
        {
            HidePeerPicker();
            await SendToPeerAsync(peer);
        }
    }

    private async Task SendToPeerAsync(LocalSendPeer peer)
    {
        if (_networkFiles.Count == 0) return;

        var payloads = new List<SendPayload>();
        foreach (var item in _networkFiles)
        {
            try
            {
                // item.Name peut être un nom d'affichage différent du nom de fichier réel
                // (ex: "clipboard.txt" pour un fichier temp TGN_guid.txt)
                payloads.Add(Directory.Exists(item.Path)
                    ? SendPayload.FromFolder(item.Path)
                    : SendPayload.FromFile(item.Path, item.Name));
            }
            catch { /* fichier inaccessible — on passe */ }
        }

        if (payloads.Count == 0) return;

        SendStatus.Text = $"Envoi vers {peer.Alias}…";
        SendStatus.Visibility = Visibility.Visible;

        bool ok = await _localSend.SendAsync(peer, payloads);

        SendStatus.Text = ok ? $"✓ Envoyé à {peer.Alias}" : "✗ Refusé ou inaccessible";

        // Auto-masquage après 3 s
        await Task.Delay(3000);
        if (SendStatus.Text.StartsWith("✓") || SendStatus.Text.StartsWith("✗"))
        {
            SendStatus.Visibility = Visibility.Collapsed;
            SendStatus.Text = "";
        }
    }

    // ── Presse-papier ─────────────────────────────────────────────────────

    // ── Saisie texte inline ───────────────────────────────────────────────

    private void OnToggleTextInput(object sender, RoutedEventArgs e)
    {
        bool visible = TextInputPanel.Visibility == Visibility.Visible;
        TextInputPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        RefreshNetworkLayout();
        if (!visible)
            EnableTextInput();
        else
            DisableTextInput();
    }

    /// <summary>Retire WS_EX_NOACTIVATE pour permettre la saisie clavier, puis donne le focus au TextBox.</summary>
    private void EnableTextInput()
    {
        if (_hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
            SetForegroundWindow(_hwnd);
        }
        // Différer légèrement pour laisser Windows traiter l'activation
        Dispatcher.InvokeAsync(() =>
        {
            TextInputBox.Focus();
            Keyboard.Focus(TextInputBox);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Remet WS_EX_NOACTIVATE (comportement normal de la notch).</summary>
    private void DisableTextInput()
    {
        if (_hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        }
    }

    private void OnTextInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)       { AddTextToSend(); e.Handled = true; }
        else if (e.Key == Key.Escape) { TextInputPanel.Visibility = Visibility.Collapsed; DisableTextInput(); RefreshNetworkLayout(); }
    }

    private void OnTextInputAdd(object sender, RoutedEventArgs e) => AddTextToSend();

    private void AddTextToSend()
    {
        var text = TextInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Nom d'affichage : 24 premiers caractères + ".txt"
        var shortName = (text.Length > 24 ? text[..24].TrimEnd() + "…" : text)
                         .Replace('\n', ' ').Replace('/', '_').Replace('\\', '_');
        var displayName = shortName + ".txt";

        var tmp = Path.Combine(Path.GetTempPath(), $"TGN_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, text, System.Text.Encoding.UTF8);
        AddToNetworkFile(tmp, displayName);

        TextInputBox.Clear();
        TextInputPanel.Visibility = Visibility.Collapsed;
        DisableTextInput();
        RefreshNetworkLayout();
    }

    private void OnPasteImage(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsImage()) return;

            var img = Clipboard.GetImage();
            if (img == null) return;

            var tmp = Path.Combine(Path.GetTempPath(), $"TGN_{Guid.NewGuid():N}.png");
            using var stream = File.Create(tmp);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            encoder.Save(stream);
            AddToNetworkFile(tmp, "clipboard.png");
        }
        catch { }
    }

    // ── Réception de fichiers (transferts entrants LocalSend) ─────────────

    /// <summary>Appelé depuis LocalSendService sur le thread UI quand un appareil veut envoyer.</summary>
    private void OnTransferRequested(IncomingTransfer transfer)
    {
        _pendingTransfer = transfer;

        IncomingSender.Text = transfer.SenderAlias + " veut vous envoyer :";
        IncomingFiles.Text  = $"{transfer.FileSummary}  •  {transfer.SizeSummary}";
        IncomingPanel.Visibility = Visibility.Visible;

        // Naviguer vers l'onglet Réseau et ouvrir la notch
        ShowPage(NotchPage.Network);
        AnimateExpansion(1.0);
        RefreshNetworkLayout();
    }

    private void OnAcceptTransfer(object sender, RoutedEventArgs e)
    {
        _pendingTransfer?.Accept();
        HideIncomingPanel("Téléchargement en cours…");
    }

    private void OnDeclineTransfer(object sender, RoutedEventArgs e)
    {
        _pendingTransfer?.Decline();
        HideIncomingPanel("");
    }

    private void HideIncomingPanel(string statusMsg)
    {
        _pendingTransfer = null;
        IncomingPanel.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(statusMsg))
        {
            SendStatus.Text = statusMsg;
            SendStatus.Visibility = Visibility.Visible;
        }
        RefreshNetworkLayout();
    }

    /// <summary>Appelé quand un fichier entrant a été enregistré dans Téléchargements.</summary>
    private void OnFileReceived(string alias, string path)
    {
        var name = System.IO.Path.GetFileName(path);
        Dispatcher.InvokeAsync(async () =>
        {
            SendStatus.Text = $"📥 Reçu de {alias} : {name}";
            SendStatus.Visibility = Visibility.Visible;
            await Task.Delay(4000);
            if (SendStatus.Text.StartsWith("📥"))
            {
                SendStatus.Visibility = Visibility.Collapsed;
                SendStatus.Text = "";
            }
        });
    }

    // ===== Icône de fichier (Win32) =====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    private static BitmapSource? GetFileIcon(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            DestroyIcon(info.hIcon);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private double _gearAngle;
    private bool _gearHover;

    private void OnSettingsTabClick(object sender, RoutedEventArgs e)
    {
        _gearAngle += 180;                                   // la roue tourne un peu à chaque clic
        AnimateGear(_gearAngle + (_gearHover ? 22 : 0), 520);
        ToggleSettings();
    }

    private void OnSettingsHover(object sender, MouseEventArgs e)
    {
        _gearHover = true;
        AnimateGear(_gearAngle + 22, 220);                  // un tout petit peu au survol
    }

    private void OnSettingsLeave(object sender, MouseEventArgs e)
    {
        _gearHover = false;
        AnimateGear(_gearAngle, 320);
    }

    private void AnimateGear(double toAngle, double ms)
    {
        GearRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = toAngle,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // ===== Batterie (Win32) =====

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private DispatcherTimer? _batteryTimer;

    private void StartBattery()
    {
        UpdateBattery();
        _batteryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _batteryTimer.Tick += (_, _) => UpdateBattery();
        _batteryTimer.Start();
    }

    private void UpdateBattery()
    {
        if (!GetSystemPowerStatus(out var status))
            return;

        bool charging  = status.ACLineStatus == 1;
        int  pct       = status.BatteryLifePercent;
        bool hasBattery = pct >= 0 && pct <= 100;

        _batteryAvailable = hasBattery;

        if (hasBattery)
        {
            // ── Barre du haut (affichage permanent) ─────────────────────
            BatteryText.Text   = pct + "%";
            BatteryFill.Width  = Math.Max(2, 22.0 * pct / 100.0);
            BatteryFill.Background = new SolidColorBrush(
                pct <= 20 ? Color.FromRgb(0xFF, 0x3B, 0x30) : Color.FromRgb(0x34, 0xC7, 0x59));
            BatteryBolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;

            // ── Détection des événements déclencheurs ────────────────────
            bool newLow      = !charging && pct <= 20;
            bool justPlugged = charging && !_isCharging;
            bool justLow     = newLow && !_batteryLow;

            _isCharging     = charging;
            _batteryLow     = newLow;
            _lastBatteryPct = pct;

            // ── Live activity ────────────────────────────────────────────
            if (justPlugged || justLow)
                TriggerBatteryHud();

            // ── Indicateur mini persistant ───────────────────────────────
            UpdateBatteryMiniIndicator(pct);
            UpdateNotchVisual();
        }
        else
        {
            BatteryText.Text = "—";
            BatteryFill.Width = 22;
            BatteryBolt.Visibility = Visibility.Collapsed;
        }
    }

    // ===== Battery Live Activity HUD =====

    private void TriggerBatteryHud()
    {
        if (ExpansionProgress > 0.15 || _volumeHudVisible) return;

        UpdateBatteryHudContent();
        ShowBatteryHud();

        if (_batteryHudTimer == null)
        {
            _batteryHudTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(BatteryHudAutoHideMs)
            };
            _batteryHudTimer.Tick += (_, _) => { _batteryHudTimer.Stop(); HideBatteryHud(); };
        }
        _batteryHudTimer.Stop();
        _batteryHudTimer.Start();
    }

    private void UpdateBatteryHudContent()
    {
        // Couleur selon niveau
        Color barColor = _batteryLow        ? Color.FromRgb(0xFF, 0x3B, 0x30)  // rouge critique
                       : _lastBatteryPct <= 50 ? Color.FromRgb(0xFF, 0x95, 0x00)  // orange
                       :                         Color.FromRgb(0x30, 0xD1, 0x58); // vert

        var colorEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var colorDur  = TimeSpan.FromMilliseconds(420);
        BatteryHudFillColor.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(barColor, colorDur) { EasingFunction = colorEase });
        BatteryHudBarColor.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(barColor, colorDur) { EasingFunction = colorEase });

        BatteryHudBolt.Visibility = _isCharging ? Visibility.Visible : Visibility.Collapsed;
        BatteryHudText.Text       = _isCharging ? $"↗ {_lastBatteryPct} %" : $"{_lastBatteryPct} %";
        BatteryHudText.Foreground = _batteryLow
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30))
            : Brushes.White;

        double frac = Math.Clamp(_lastBatteryPct / 100.0, 0, 1);
        var springBar  = Motion.Snappy;
        var springIcon = Motion.Snappy;

        // Barre horizontale
        BatteryHudBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            To = frac, Duration = TimeSpan.FromMilliseconds(540),
            EasingFunction = springBar, FillBehavior = FillBehavior.HoldEnd
        });
        // Fill icône batterie
        BatteryHudFillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            To = frac, Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = springIcon, FillBehavior = FillBehavior.HoldEnd
        });
        // Pop sur l'icône
        var popEase = Motion.Bouncy;
        var pop     = new DoubleAnimation { From = 0.55, To = 1.0,
            Duration = TimeSpan.FromMilliseconds(420), EasingFunction = popEase };
        BatteryHudIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        BatteryHudIconScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation { From = 0.55, To = 1.0,
                Duration = TimeSpan.FromMilliseconds(420), EasingFunction = popEase });
    }

    private void ShowBatteryHud()
    {
        bool wasVisible = _batteryHudVisible;
        _batteryHudVisible = true;

        BeginAnimation(BatteryHudProgressProperty, new DoubleAnimation
        {
            To = 1.0, Duration = TimeSpan.FromMilliseconds(440),
            EasingFunction = Motion.Snappy,
            FillBehavior = FillBehavior.HoldEnd
        });
        BatteryHudPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var slide = new DoubleAnimation
        {
            To = 0, Duration = TimeSpan.FromMilliseconds(440),
            EasingFunction = Motion.Snappy
        };
        if (!wasVisible) slide.From = -12;
        BatteryHudSlideT.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void HideBatteryHud()
    {
        if (!_batteryHudVisible) return;
        _batteryHudVisible = false;

        BeginAnimation(BatteryHudProgressProperty, new DoubleAnimation
        {
            To = 0.0, Duration = TimeSpan.FromMilliseconds(360),
            EasingFunction = Motion.Smooth,
            FillBehavior = FillBehavior.HoldEnd
        });
        BatteryHudPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void UpdateBatteryMiniIndicator(int pct)
    {
        Color miniColor = _isCharging
            ? Color.FromRgb(0x30, 0xD1, 0x58)  // vert : en charge
            : Color.FromRgb(0xFF, 0x3B, 0x30); // rouge : batterie faible

        BatteryMiniColor.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(miniColor, TimeSpan.FromMilliseconds(500)));
        BatteryMiniBolt.Visibility = _isCharging ? Visibility.Visible : Visibility.Collapsed;

        double frac = Math.Clamp(pct / 100.0, 0, 1);
        if (_isCharging) StartChargingPulse(frac);
        else             StopChargingPulse(frac);
    }

    /// <summary>
    /// Animation iOS de recharge : le fill balaye de currentFrac → 100 % (ease-in-out 1.6 s),
    /// maintient 100 % pendant 0.4 s, puis repart du niveau réel — boucle infinie.
    /// </summary>
    private void StartChargingPulse(double currentFrac)
    {
        var sweep = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            Duration = new Duration(TimeSpan.FromMilliseconds(2000))
        };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            currentFrac, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        BatteryMiniFillScale.BeginAnimation(ScaleTransform.ScaleXProperty, sweep);
    }

    private void StopChargingPulse(double targetFrac)
    {
        BatteryMiniFillScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(targetFrac, TimeSpan.FromMilliseconds(400))
            { EasingFunction = Motion.Snappy });
    }

    // ===== Keep-Awake (Caffeine) Toggle =====

    private void OnCoffeeToggleClick(object sender, RoutedEventArgs e) => ToggleKeepAwake();

    private void ToggleKeepAwake()
    {
        _keepAwake = !_keepAwake;
        SetThreadExecutionState(_keepAwake
            ? ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED
            : ES_CONTINUOUS);

        // Bouton étendu : opacité + rebond d'échelle
        var ease = Motion.Bouncy;
        var dur  = TimeSpan.FromMilliseconds(300);
        CoffeeIcon.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_keepAwake ? 0.80 : 0.60, dur));
        var pop = new DoubleAnimation { From = 0.65, To = 1.0, Duration = dur, EasingFunction = ease };
        CoffeeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        CoffeeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation { From = 0.65, To = 1.0, Duration = dur, EasingFunction = ease });

        // Indicateur mini : UpdateNotchVisual recalcule l'opacité selon t courant
        UpdateNotchVisual();
    }

    // ===== Calendrier =====

    private void PopulateCalendar()
    {
        var ci = CultureInfo.CurrentCulture;
        var today = DateTime.Now;

        // Mini : date chip minimaliste
        CalMiniNum.Text       = today.Day.ToString();
        CalMiniWeekday.Text   = ci.TextInfo.ToTitleCase(today.ToString("ddd", ci).TrimEnd('.'));
        CalMiniMonthYear.Text = today.ToString("MMM yyyy", ci);

        // Pleine page : en-tête classique (mois + bande de jours)
        _selectedDay = today.Date;   // réinitialiser sur aujourd'hui à chaque PopulateCalendar
        CalFullMonth.Text = ci.TextInfo.ToTitleCase(today.ToString("MMM", ci).TrimEnd('.'));
        BuildDayStrip(CalFullDays, _selectedDay, cellWidth: 30);

        BuildTimeline();
    }

    /// <summary>Remplit le mini avec les 2 prochains événements du jour, ou un état vide élégant.</summary>
    private void BuildMiniEvents(IReadOnlyList<CalendarEventItem> events)
    {
        CalMiniEventList.Children.Clear();
        var upcoming = events
            .Where(e => e.End > DateTime.Now)
            .OrderBy(e => e.Start)
            .Take(2)
            .ToList();

        if (upcoming.Count == 0)
        {
            CalMiniEventList.Children.Add(new TextBlock
            {
                Text = "Journée libre",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                FontStyle = FontStyles.Italic
            });
            return;
        }

        foreach (var ev in upcoming)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 5)
            };

            // Dot coloré de l'événement
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 5, Height = 5,
                Fill = ev.ColorBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            // Heure
            row.Children.Add(new TextBlock
            {
                Text = ev.Start.ToString("H:mm"),
                FontSize = 10, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            // Titre (tronqué)
            row.Children.Add(new TextBlock
            {
                Text = ev.Title,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 88,
                VerticalAlignment = VerticalAlignment.Center
            });

            CalMiniEventList.Children.Add(row);
        }
    }

    /// <summary>Construit une bande de 6 jours (J-2 → J+3) centrée sur aujourd'hui, chaque cellule cliquable.</summary>
    private void BuildDayStrip(StackPanel target, DateTime selectedDay, double cellWidth)
    {
        var ci    = CultureInfo.CurrentCulture;
        var blue  = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        var dim   = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var today = DateTime.Today;
        double dot = cellWidth - 1;

        target.Children.Clear();
        var start = today.AddDays(-2);

        for (int i = 0; i < 6; i++)
        {
            var day        = start.AddDays(i);
            bool isSelected = day.Date == selectedDay.Date;
            bool isToday    = day.Date == today;

            var cell = new StackPanel
            {
                Width = cellWidth,
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand
            };

            // Label du jour : blanc si sélectionné, grisé sinon
            cell.Children.Add(new TextBlock
            {
                Text = ci.TextInfo.ToTitleCase(day.ToString("ddd", ci).TrimEnd('.')),
                FontSize = 10,
                Foreground = isSelected ? Brushes.White : dim,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Cercle indicateur
            var numGrid = new Grid { Width = dot, Height = dot, Margin = new Thickness(0, 3, 0, 0) };

            if (isSelected)
                // Disque bleu plein = jour sélectionné
                numGrid.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = dot, Height = dot, Fill = blue
                });
            else if (isToday)
                // Anneau bleu fin = aujourd'hui mais pas sélectionné
                numGrid.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = dot, Height = dot,
                    Stroke = blue, StrokeThickness = 1.5,
                    Fill = Brushes.Transparent
                });

            numGrid.Children.Add(new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 12,
                FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            cell.Children.Add(numGrid);

            // Clic sur la cellule → navigation
            var captured = day;
            cell.MouseLeftButtonDown += (_, e) => { e.Handled = true; OnDayClick(captured); };

            target.Children.Add(cell);
        }
    }

    /// <summary>Bascule la vue de la timeline vers un autre jour avec transition glissante.</summary>
    private void OnDayClick(DateTime day)
    {
        if (day.Date == _selectedDay.Date) return;

        int dir = day.Date > _selectedDay.Date ? 1 : -1;
        _selectedDay = day.Date;

        // Mettre à jour le mois affiché si on change de mois
        var ci = CultureInfo.CurrentCulture;
        CalFullMonth.Text = ci.TextInfo.ToTitleCase(_selectedDay.ToString("MMM", ci).TrimEnd('.'));

        // Rafraîchir la bande (nouveau disque bleu sur le bon jour)
        BuildDayStrip(CalFullDays, _selectedDay, cellWidth: 30);

        // Récupérer les événements depuis le cache (pas de réseau)
        var events = _icalService.IsConfigured
            ? _icalService.GetEventsForDate(_selectedDay)
            : (_selectedDay.Date == DateTime.Today ? GetMockEvents() : []);

        // Rendre la timeline sans stagger (la transition elle-même est l'animation)
        bool isToday = _selectedDay.Date == DateTime.Today;
        RenderTimeline(CalFullTimeline, CalFullScroll, 600, events,
                       nowViewportFraction: 0.40, staggerPills: false,
                       showNowLine: isToday);

        // Glissement latéral
        AnimateTimelineSlide(dir);
    }

    /// <summary>Fait entrer le contenu de la timeline depuis le côté indiqué (dir = +1 droite, -1 gauche).</summary>
    private void AnimateTimelineSlide(int dir)
    {
        const double offset = 22;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur  = TimeSpan.FromMilliseconds(270);

        // Annuler toute animation en cours
        CalFullTimeline.BeginAnimation(OpacityProperty, null);
        CalTimelineT.BeginAnimation(TranslateTransform.XProperty, null);

        // Position & opacité de départ
        CalFullTimeline.Opacity = 0;
        CalTimelineT.X = dir * offset;

        // Entrée : glisse vers 0 + fondu
        CalFullTimeline.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, dur) { EasingFunction = ease });
        CalTimelineT.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, dur) { EasingFunction = ease });
    }

    // ── Timeline 24 h ───────────────────────────────────────────────────────

    private const double HourH  = 36.0;   // pixels par heure dans le canvas
    private const double LabelW = 26.0;   // largeur de la colonne heure

    // Source de vérité des événements affichés (mini + complet). Alimentée par GetMockEvents
    // au Sprint 1, puis par GoogleCalendarService au Sprint 2.
    private IReadOnlyList<CalendarEventItem> _calendarEvents = [];

    // Reconstruit les deux timelines (mini Accueil + page complète). Appelé à l'ouverture et chaque minute.
    internal void BuildTimeline(IReadOnlyList<CalendarEventItem>? events = null)
    {
        if (events != null) _calendarEvents = events;
        else if (!_icalService.IsConfigured) _calendarEvents = GetMockEvents();
        // Si connecté mais events=null : on conserve les données précédentes

        // Mini : toujours les événements du jour actuel
        BuildMiniEvents(_calendarEvents);

        // Pleine vue : événements du jour sélectionné (depuis cache si différent d'aujourd'hui)
        var fullEvents = _selectedDay.Date == DateTime.Today
            ? _calendarEvents
            : (_icalService.IsConfigured ? _icalService.GetEventsForDate(_selectedDay) : []);

        RenderTimeline(CalFullTimeline, CalFullScroll, 600, fullEvents, nowViewportFraction: 0.40,
                       staggerPills: _currentPage == NotchPage.Calendar,
                       showNowLine: _selectedDay.Date == DateTime.Today);
    }

    /// <summary>Rend une timeline 24 h dans un canvas cible, à la largeur donnée.
    /// <paramref name="nowViewportFraction"/> = position de la ligne « maintenant » dans le viewport
    /// (0.40 = haut → plus d'heures à venir visibles).</summary>
    private void RenderTimeline(Canvas canvas, ScrollViewer scroll, double width,
                               IReadOnlyList<CalendarEventItem> items, double nowViewportFraction,
                               bool staggerPills = false, bool showNowLine = true)
    {
        var now = DateTime.Now;
        canvas.Children.Clear();

        if (items.Count == 0)
        {
            canvas.Height = 80;
            // Icône calendrier vide (SVG path) — pas d'emoji (taste-skill §2 ANTI-EMOJI)
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M19 3h-1V1h-2v2H8V1H6v2H5c-1.11 0-1.99.9-1.99 2L3 19c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H5V8h14v11z"),
                Fill = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                Stretch = Stretch.Uniform, Width = 18, Height = 18
            };
            Canvas.SetTop(icon, 18);
            Canvas.SetLeft(icon, width / 2 - 9);
            canvas.Children.Add(icon);

            var msg = new TextBlock
            {
                Text = "Journée libre",
                FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                TextAlignment = TextAlignment.Center, Width = width - 20
            };
            Canvas.SetTop(msg, 42);
            Canvas.SetLeft(msg, 10);
            canvas.Children.Add(msg);
            return;
        }

        canvas.Height = 24 * HourH;

        // Tokens visuels alignés sur le design system de l'app
        var lineBrush  = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)); // #14FFFFFF
        var labelBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)); // #55FFFFFF
        var nowBrush   = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));        // Apple system red

        for (int h = 0; h < 24; h++)
        {
            double y = h * HourH;

            // Ligne séparatrice — part après le label, pas de toute la largeur (évite l'effet grille)
            var sep = new System.Windows.Shapes.Rectangle
            {
                Width = width - LabelW - 2, Height = 0.5, Fill = lineBrush
            };
            Canvas.SetTop(sep, y);
            Canvas.SetLeft(sep, LabelW + 2);
            canvas.Children.Add(sep);

            // Label heure aligné à droite dans sa colonne
            var lbl = new TextBlock
            {
                Text = $"{h:D2}", FontSize = 9,
                Foreground = labelBrush,
                Width = LabelW - 4, TextAlignment = TextAlignment.Right
            };
            Canvas.SetTop(lbl, y + 2);
            Canvas.SetLeft(lbl, 2);
            canvas.Children.Add(lbl);

            // Pilules des événements démarrant dans cette heure
            double pillY = y + 4;
            foreach (var ev in items.Where(e => e.Start.Hour == h))
            {
                var pill = BuildEventPill(ev, width);
                Canvas.SetTop(pill, pillY);
                Canvas.SetLeft(pill, LabelW + 4);
                canvas.Children.Add(pill);
                pillY += 30;
            }
        }

        if (showNowLine)
        {
            // Indicateur heure actuelle : point + ligne (style Apple Calendar)
            double timeY = (now.Hour + now.Minute / 60.0) * HourH;

            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = nowBrush };
            Canvas.SetTop(dot, timeY - 3.5);
            Canvas.SetLeft(dot, LabelW - 1);
            canvas.Children.Add(dot);

            var timeLine = new System.Windows.Shapes.Rectangle
            {
                Width = width - LabelW - 8, Height = 1.5, Fill = nowBrush
            };
            Canvas.SetTop(timeLine, timeY - 0.75);
            Canvas.SetLeft(timeLine, LabelW + 8);
            canvas.Children.Add(timeLine);

            // Scroll : heure actuelle positionnée selon nowViewportFraction dans le viewport
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                double target = timeY - scroll.ActualHeight * nowViewportFraction;
                scroll.ScrollToVerticalOffset(Math.Max(0, target));
            });
        }
        else
        {
            // Autre jour : scroll sur le premier événement (15 % de marge depuis le haut)
            var first = items.OrderBy(e => e.Start).FirstOrDefault();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                double firstY = first is not null
                    ? (first.Start.Hour + first.Start.Minute / 60.0) * HourH
                    : 0;
                scroll.ScrollToVerticalOffset(Math.Max(0, firstY - scroll.ActualHeight * 0.15));
            });
        }

        // Stagger d'entrée (taste-skill §4) : les pills apparaissent en cascade plutôt qu'en bloc.
        if (staggerPills)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                int idx = 0;
                foreach (Border pill in canvas.Children.OfType<Border>())
                {
                    pill.Opacity = 0;
                    pill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                        TimeSpan.FromMilliseconds(260))
                    {
                        BeginTime    = TimeSpan.FromMilliseconds(idx * 55),
                        EasingFunction = ease
                    });
                    idx++;
                }
            });
    }

    private static Border BuildEventPill(CalendarEventItem ev, double width)
    {
        double pillW = width - LabelW - 8;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Barre accent colorée (couleur du calendrier)
        var accent = new System.Windows.Shapes.Rectangle { Fill = ev.ColorBrush };
        Grid.SetColumn(accent, 0);
        grid.Children.Add(accent);

        // Contenu : titre + heure, centré verticalement
        var stack = new StackPanel
        {
            Margin = new Thickness(6, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = ev.Title,
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = ev.TimeDisplay,
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        // Fond teinté par la couleur propre de l'événement (au lieu du blanc générique) —
        // chaque calendrier Google a sa teinte, le pill la reflète subtilement.
        var c = ev.ColorBrush.Color;
        return new Border
        {
            Width = pillW, Height = 28,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)),
            ClipToBounds = true,
            Opacity = ev.EndedOpacity,
            Child = grid
        };
    }

    // TEMPORAIRE (Sprint 1) : événements de démo pour valider le rendu.
    // Remplacé par GoogleCalendarService au Sprint 2.
    private static List<CalendarEventItem> GetMockEvents()
    {
        var today = DateTime.Today;
        SolidColorBrush C(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
        return
        [
            new() { Title = "Stand-up équipe", Start = today.AddHours(9.5),  End = today.AddHours(10),    ColorBrush = C(0x0A, 0x84, 0xFF) },
            new() { Title = "Déjeuner",         Start = today.AddHours(12),   End = today.AddHours(13),    ColorBrush = C(0xFF, 0x9F, 0x0A) },
            new() { Title = "Revue de code",    Start = today.AddHours(14),   End = today.AddHours(15.5),  ColorBrush = C(0x30, 0xD1, 0x58) },
            new() { Title = "Point projet",     Start = today.AddHours(16.5), End = today.AddHours(17),    ColorBrush = C(0xBF, 0x5A, 0xF2) },
        ];
    }

    private void PositionAtTopCenter()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2.0;
        Top = 0;
    }

    // ===== Volume HUD =====

    private const double VolumeHudAutoHideMs = 2500;

    /// <summary>Appelé sur le thread UI dès qu'une touche volume est détectée.</summary>
    private void OnVolumeKeyPressed()
    {
        // Ne pas interrompre le mode déployé (l'utilisateur pilote les médias)
        if (ExpansionProgress > 0.15)
            return;

        HideBatteryHud();   // volume prend la priorité
        UpdateVolumeHudContent();
        ShowVolumeHud();

        // Redémarre le timer d'auto-masquage à chaque nouvelle pression
        if (_volumeHideTimer == null)
        {
            _volumeHideTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(VolumeHudAutoHideMs)
            };
            _volumeHideTimer.Tick += (_, _) =>
            {
                _volumeHideTimer.Stop();
                HideVolumeHud();
            };
        }
        _volumeHideTimer.Stop();
        _volumeHideTimer.Start();
    }

    private void UpdateVolumeHudContent()
    {
        if (_volumeService == null) return;

        bool muted = _volumeService.IsMuted;
        VolumeIconSound.Visibility  = muted ? Visibility.Collapsed : Visibility.Visible;
        VolumeIconMuted.Visibility  = muted ? Visibility.Visible   : Visibility.Collapsed;
        VolumePercentText.Text      = muted ? "Muet" : $"{(int)(_volumeService.Volume * 100)} %";

        AnimateVolumeBar();
        PopVolumeIcon();
    }

    /// <summary>Le remplissage glisse vers la nouvelle valeur avec un léger dépassement (ressort).</summary>
    private void AnimateVolumeBar()
    {
        if (_volumeService == null) return;
        double frac = _volumeService.IsMuted ? 0.0 : Math.Clamp(_volumeService.Volume, 0, 1);
        VolumeBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            To = frac,
            Duration = TimeSpan.FromMilliseconds(430),
            EasingFunction = Motion.Snappy,
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    /// <summary>Petit rebond d'échelle sur l'icône active à chaque changement de volume.</summary>
    private void PopVolumeIcon()
    {
        if (_volumeService == null) return;
        var scale = _volumeService.IsMuted ? VolumeIconMutedScale : VolumeIconSoundScale;
        var pop = new DoubleAnimation
        {
            From = 0.62,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(360),
            EasingFunction = Motion.Bouncy
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void ShowVolumeHud()
    {
        bool wasVisible = _volumeHudVisible;
        _volumeHudVisible = true;

        // La notch grandit (live activity) — ressort
        BeginAnimation(VolumeHudProgressProperty, new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(440),
            EasingFunction = Motion.Snappy,
            FillBehavior = FillBehavior.HoldEnd
        });

        // Apparition : fondu rapide + glissement vertical depuis le haut (seulement au 1er affichage)
        VolumeHudPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var slide = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(440),
            EasingFunction = Motion.Snappy
        };
        if (!wasVisible) slide.From = -12;
        VolumeHudSlideT.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void HideVolumeHud()
    {
        if (!_volumeHudVisible) return;
        _volumeHudVisible = false;

        // La notch reprend sa taille
        BeginAnimation(VolumeHudProgressProperty, new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(360),
            EasingFunction = Motion.Smooth,
            FillBehavior = FillBehavior.HoldEnd
        });

        VolumeHudPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }
}
