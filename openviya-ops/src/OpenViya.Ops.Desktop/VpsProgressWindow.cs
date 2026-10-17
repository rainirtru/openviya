using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Desktop;

public sealed class VpsProgressWindow : Window
{
    private readonly VpsProgressViewModel _viewModel = new();
    private readonly StackPanel _summary = new();
    private readonly StackPanel _dns = new();
    private readonly TextBlock _state = new();
    private readonly TextBlock _operation = new();
    private readonly TextBlock _elapsed = new();
    private readonly TextBlock _contact = new();
    private readonly TextBlock _nextPoll = new();
    private DispatcherTimer? _clock;

    public VpsProgressWindow(IReadOnlyList<VpsProvisionRun> history, VpsProvisionRun latest)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "OpenViya Ops · Журнал настройки VPS";
        Width = 880; Height = 650; MinWidth = 620; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "Журнал настройки VPS", FontSize = 22, FontWeight = FontWeights.SemiBold });
        var note = new TextBlock { Text = "Сохранённые шаги этого запуска, до 200 событий. Текущий ход установки обновляется в мастере настройки.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetRow(note, 1); grid.Children.Add(note);
        _viewModel.SetHistory(history);
        _viewModel.Update(latest);
        _state.TextWrapping = TextWrapping.Wrap;
        _state.FontWeight = FontWeights.SemiBold;
        _operation.TextWrapping = TextWrapping.Wrap;
        _contact.TextWrapping = TextWrapping.Wrap;
        foreach (var text in new[] { _contact, _nextPoll }) text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var summary = new StackPanel();
        summary.Children.Add(_state);
        var line = new WrapPanel { Margin = new Thickness(0, 4, 0, 5) };
        line.Children.Add(new TextBlock { Text = "Текущий шаг: ", Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        line.Children.Add(_operation);
        line.Children.Add(new TextBlock { Text = "   ·   прошло ", Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        line.Children.Add(_elapsed);
        summary.Children.Add(line);
        summary.Children.Add(_contact);
        summary.Children.Add(_nextPoll);
        _summary.Margin = new Thickness(0, 6, 0, 0);
        summary.Children.Add(_summary);
        _dns.Margin = new Thickness(0, 4, 0, 0);
        summary.Children.Add(_dns);
        var summaryBorder = new Border { Background = (System.Windows.Media.Brush)FindResource("Surface"), BorderBrush = (System.Windows.Media.Brush)FindResource("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = summary };
        Grid.SetRow(summaryBorder, 2); grid.Children.Add(summaryBorder);

        var journal = new ProvisionProgressLog();
        foreach (var item in history) journal.Append(item);
        journal.Append(latest);
        Grid.SetRow(journal, 3); grid.Children.Add(journal);
        var close = new Button { Content = "Закрыть", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), MinWidth = 100 };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 4); grid.Children.Add(close);
        Content = grid;
        RenderSummary();
        _clock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => { _viewModel.RefreshClock(DateTimeOffset.UtcNow); RenderClock(); };
        _clock.Start();
        Closed += (_, _) => _clock?.Stop();
    }

    private void RenderSummary()
    {
        _state.Text = _viewModel.StateText;
        _operation.Text = _viewModel.CurrentOperation;
        _contact.Text = _viewModel.LastContact;
        _nextPoll.Text = _viewModel.NextPoll;
        _elapsed.Text = _viewModel.CurrentElapsed;
        _summary.Children.Clear();
        foreach (var stage in _viewModel.Stages)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock { Text = stage.Title + "  ·  ", Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
            row.Children.Add(new TextBlock { Text = stage.StateText });
            _summary.Children.Add(row);
        }
        _dns.Children.Clear();
        if (_viewModel.DnsItems.Count == 0) return;
        var heading = new TextBlock { Text = "DNS · проверка имён", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Teal");
        _dns.Children.Add(heading);
        foreach (var item in _viewModel.DnsItems)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{item.Name}  ·  {item.RecordType} → {item.ExpectedAddress}", TextWrapping = TextWrapping.Wrap });
            var status = new TextBlock { Text = item.StateText, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            status.SetResourceReference(TextBlock.ForegroundProperty, item.Status == ProbeStatus.Success ? "Teal" : "Brass");
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
            _dns.Children.Add(row);
        }
        var count = new TextBlock { Text = _viewModel.DnsSummary, Margin = new Thickness(0, 2, 0, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _dns.Children.Add(count);
    }

    private void RenderClock()
    {
        _elapsed.Text = _viewModel.CurrentElapsed;
        _nextPoll.Text = _viewModel.NextPoll;
    }
}
