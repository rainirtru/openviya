using OpenViya.Ops.Core;
using System.Windows;

namespace OpenViya.Ops.Desktop;

public partial class DnsSettingsWindow : Window
{
    private readonly IDnsTokenStore _tokens;
    private readonly IDnsProviderService _provider;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;

    public DnsSettingsWindow(IDnsTokenStore tokens, IDnsProviderService provider)
    {
        InitializeComponent();
        _tokens = tokens;
        _provider = provider;
        Loaded += async (_, _) => await RefreshTokenStateAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async Task RefreshTokenStateAsync()
    {
        try
        {
            TokenState.Text = await _tokens.LoadAsync(_lifetime.Token) is null
                ? "Токен не сохранён."
                : "Токен сохранён и зашифрован для текущего пользователя.";
        }
        catch (Exception)
        {
            TokenState.Text = "Не удалось прочитать сохранённый токен. Его содержимое не отображается.";
        }
    }

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        var value = TokenBox.Password;
        if (string.IsNullOrWhiteSpace(value)) { StatusText.Text = "Введите новый токен Timeweb."; return; }
        await RunAsync("Сохраняем токен локально…", async ct =>
        {
            await _tokens.SaveAsync(value, ct);
            TokenBox.Clear();
            TokenState.Text = "Токен сохранён и зашифрован для текущего пользователя.";
            StatusText.Text = "Токен сохранён. Проверка API выполняется только по вашему действию.";
        });
    }

    private async void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        if (!OwnedDialog.Confirm(this, "Удалить токен Timeweb?", "Токен будет удалён из локального хранилища этого пользователя. Сохранённые профили не изменятся.", "Удалить токен")) return;
        await RunAsync("Удаляем локальный токен…", async ct =>
        {
            await _tokens.DeleteAsync(ct);
            TokenBox.Clear();
            TokenState.Text = "Токен не сохранён.";
            StatusText.Text = "Локально сохранённый токен удалён.";
        });
    }

    private async void CheckZone_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TokenBox.Password))
        {
            StatusText.Text = "Сначала сохраните введённый токен. Проверка использует зашифрованный токен из локального хранилища.";
            return;
        }
        var zone = ZoneBox.Text.Trim();
        if (zone.Length == 0) { ZoneResult.Text = "Укажите DNS-зону."; return; }
        await RunAsync("Проверяем доступ к зоне…", async ct =>
        {
            var check = await _provider.CheckZoneAsync(zone, ct);
            var access = check.Accessible ? "Зона доступна для чтения." : check.Message;
            var servers = check.NameServers.Count == 0 ? "Список NS не получен." : "NS: " + string.Join(", ", check.NameServers);
            ZoneResult.Text = $"{access} {servers}";
            StatusText.Text = "Проверка завершена в режиме чтения; DNS-записи не изменялись.";
        });
    }

    private async Task RunAsync(string pending, Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        SetControls(false);
        StatusText.Text = pending;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try { await work(timeout.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { StatusText.Text = "Операция превысила лимит времени. Проверьте сеть и повторите."; }
        catch (Exception ex) { StatusText.Text = SafeError(ex); }
        finally { _busy = false; SetControls(true); }
    }

    private void SetControls(bool enabled)
    {
        SaveTokenButton.IsEnabled = enabled;
        ClearTokenButton.IsEnabled = enabled;
        CheckZoneButton.IsEnabled = enabled;
    }

    private static string SafeError(Exception ex) => ex switch
    {
        ArgumentException => ex.Message,
        UnauthorizedAccessException => "Нет прав для изменения локальных настроек DNS.",
        InvalidOperationException => ex.Message,
        _ => "Операция не выполнена. Проверьте токен, зону и сетевое подключение. Подробности исключения не отображаются."
    };

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
