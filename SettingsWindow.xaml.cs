using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TheGrandNotch.Services;
using TheGrandNotch.Settings;

namespace TheGrandNotch;

public partial class SettingsWindow : Window
{
    private readonly NotchSettings _settings;
    private readonly Action _onApply;
    private bool _loaded;
    private bool _updatingStartup;

    public SettingsWindow(NotchSettings settings, Action onApply)
    {
        InitializeComponent();
        _settings = settings;
        _onApply = onApply;
        DataContext = _settings;

        SelectStartupItem(StartupManager.GetCurrentMethod());
        UpdateSwatch();

        _loaded = true;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Quitter The Grand Notch ?",
            "Quitter",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            Application.Current.Shutdown();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded)
            return;
        ApplyAndSave();
    }

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
            return;
        if (UpdateSwatch())
            ApplyAndSave();
    }

    private void OnStartupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _updatingStartup)
            return;

        if (StartupCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        var method = Enum.Parse<StartupMethod>(tag);
        bool ok = StartupManager.SetMethod(method);

        if (!ok)
        {
            MessageBox.Show(
                "Impossible d'appliquer cette méthode de démarrage.\n" +
                "L'élévation (UAC) a probablement été refusée.",
                "Démarrage au boot",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SelectStartupItem(StartupManager.GetCurrentMethod());
            return;
        }

        _settings.Startup = StartupManager.GetCurrentMethod();
        SettingsStore.Save(_settings);
    }

    private void SelectStartupItem(StartupMethod method)
    {
        _updatingStartup = true;
        foreach (ComboBoxItem item in StartupCombo.Items)
        {
            if (item.Tag is string tag && tag == method.ToString())
            {
                StartupCombo.SelectedItem = item;
                break;
            }
        }
        _updatingStartup = false;
    }

    /// <summary>
    /// Valide la couleur saisie et met à jour l'aperçu. Retourne true si valide.
    /// </summary>
    private bool UpdateSwatch()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(_settings.BackgroundColor);
            ColorSwatch.Background = new SolidColorBrush(color);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Réinitialiser tous les paramètres d'apparence aux valeurs par défaut ?\n" +
            "(La méthode de démarrage au boot n'est pas modifiée.)",
            "Réinitialisation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        var d = new NotchSettings();
        _settings.Width = d.Width;
        _settings.Height = d.Height;
        _settings.ExpandedWidth = d.ExpandedWidth;
        _settings.ExpandedHeight = d.ExpandedHeight;
        _settings.CornerRadiusBottom = d.CornerRadiusBottom;
        _settings.ExpandedCornerRadius = d.ExpandedCornerRadius;
        _settings.AnimationDurationMs = d.AnimationDurationMs;
        _settings.EasingAmplitude = d.EasingAmplitude;
        _settings.TopmostReassertionMs = d.TopmostReassertionMs;
        _settings.BackgroundColor = d.BackgroundColor;
        _settings.TopOffset = d.TopOffset;
        _settings.MiniExtraWidth = d.MiniExtraWidth;
        // _settings.Startup conservé volontairement

        // Force le rafraîchissement des bindings (NotchSettings ne notifie pas)
        _loaded = false;
        DataContext = null;
        DataContext = _settings;
        UpdateSwatch();
        _loaded = true;

        ApplyAndSave();
    }

    private void ApplyAndSave()
    {
        SettingsStore.Save(_settings);
        _onApply();
    }
}
