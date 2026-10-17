using OpenViya.Ops.Core;
using System.Windows;
using System.Windows.Controls;

namespace OpenViya.Ops.Desktop;

public partial class VpsCredentialsWindow : Window
{
    private readonly VpsPanelCredentials _credentials;

    public VpsCredentialsWindow(VpsPanelCredentials credentials)
    {
        InitializeComponent();
        _credentials = credentials;
        PanelAddressText.Text = $"https://{credentials.PanelDomain}:{credentials.PanelPort} · путь скрыт";
        UserMasked.Password = credentials.UserName;
        PasswordMasked.Password = credentials.Password;
        PathMasked.Password = credentials.PanelPath;
        UserRevealed.Text = credentials.UserName;
        PasswordRevealed.Text = credentials.Password;
        PathRevealed.Text = credentials.PanelPath;
    }

    internal void SetSmokeReveal(bool reveal) => RevealCheck.IsChecked = reveal;

    private void Reveal_Changed(object sender, RoutedEventArgs e)
    {
        var reveal = RevealCheck.IsChecked == true;
        UserMasked.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        PasswordMasked.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        PathMasked.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        UserRevealed.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        PasswordRevealed.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        PathRevealed.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field }) return;
        var value = field switch
        {
            "User" => _credentials.UserName,
            "Password" => _credentials.Password,
            "Path" => _credentials.PanelPath,
            _ => ""
        };
        try
        {
            Clipboard.SetText(value);
            CopyStatus.Text = "Выбранное поле скопировано. Буфер обмена теперь содержит секрет.";
        }
        catch (Exception)
        {
            CopyStatus.Text = "Не удалось записать выбранное поле в буфер обмена.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
