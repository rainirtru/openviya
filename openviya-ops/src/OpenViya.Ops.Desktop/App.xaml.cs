using System.Threading;
using System.IO;
using System.Windows;
namespace OpenViya.Ops.Desktop;
public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        var dataDir = DefaultDataDirectory(); string? smokePath = null; var explicitDataDir = false;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) { dataDir = Path.GetFullPath(e.Args[++i]); explicitDataDir = true; }
            else if (e.Args[i] == "--smoke-test" && i + 1 < e.Args.Length) smokePath = Path.GetFullPath(e.Args[++i]);
        }
        var ownedSmokeDirectory = smokePath is not null
            ? Path.Combine(explicitDataDir ? dataDir : Path.Combine(Path.GetTempPath(), "OpenViya.Ops.Smoke"), Guid.NewGuid().ToString("N"))
            : null;
        if (ownedSmokeDirectory is not null) dataDir = ownedSmokeDirectory;
        _mutex = new Mutex(true, "Local\\OpenViya.Ops.Desktop.SingleInstance", out var created);
        if (!created && smokePath is null) { Shutdown(); return; }
        Directory.CreateDirectory(dataDir);
        try
        {
            var remoteConfig = smokePath is null ? RemoteConnectionConfig.Load(dataDir) : null;
            var window = new MainWindow(dataDir, smokePath is not null, remoteConfig: remoteConfig);
            MainWindow = window;
            window.SmokePath = smokePath;
            window.DeleteDataDirectoryOnSmokeClose = ownedSmokeDirectory;
            if (smokePath is null) window.Show();
            else { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.WindowState = WindowState.Normal; window.Width = 1280; window.Height = 820; window.Show(); }
        }
        catch (Exception ex)
        {
            if (smokePath is { } smokeFailure)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(smokeFailure)!); File.WriteAllText(smokeFailure + ".error.txt", $"Desktop smoke startup failed ({ex.GetType().Name}): {ex.Message}"); } catch { }
                if (ownedSmokeDirectory is { } tempSmoke && Directory.Exists(tempSmoke)) Directory.Delete(tempSmoke, recursive: true);
                Environment.ExitCode = 1; Shutdown(1); return;
            }
            if (ownedSmokeDirectory is { } temp && Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            var message = ex is FileNotFoundException
                ? "Не найден Windows OpenSSH Client (ssh.exe / ssh-keyscan.exe). Включите компонент OpenSSH Client в дополнительных компонентах Windows и запустите OpenViya Ops снова. Данные инфраструктуры не изменялись."
                : "Не удалось запустить локальные адаптеры OpenViya Ops. Проверьте установку приложения и доступ к локальной папке данных. Инфраструктура и системный hosts не изменялись.";
            var error = new Window { Title = "OpenViya Ops · запуск", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16,24,32)), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232,238,240)), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI"), Content = new System.Windows.Controls.StackPanel { Margin = new Thickness(24), Children = { new System.Windows.Controls.TextBlock { Text = "OpenViya Ops не запущен", FontSize = 20, FontWeight = FontWeights.SemiBold }, new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,18), LineHeight = 21 }, new System.Windows.Controls.Button { Content = "Закрыть", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 92 } } } };
            error.ShowDialog(); Shutdown();
        }
        base.OnStartup(e);
    }
    internal static string DefaultDataDirectory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenViya", "Ops");
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
