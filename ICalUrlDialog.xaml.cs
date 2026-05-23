using System.Windows;
using System.Windows.Input;

namespace TheGrandNotch;

public partial class ICalUrlDialog : Window
{
    public string ResultUrl { get; private set; } = "";
    public bool Disconnected { get; private set; }

    public ICalUrlDialog(string existingUrl = "")
    {
        InitializeComponent();
        UrlBox.Text = existingUrl;
        if (!string.IsNullOrEmpty(existingUrl))
            DisconnectBtn.Visibility = Visibility.Visible;

        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        ResultUrl = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(ResultUrl)) return;
        DialogResult = true;
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        Disconnected = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnUrlBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnConfirm(sender, e);
        else if (e.Key == Key.Escape) OnCancel(sender, e);
    }
}
