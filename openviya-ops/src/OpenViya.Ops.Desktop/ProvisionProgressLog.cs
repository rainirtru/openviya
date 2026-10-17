using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Desktop;

/// <summary>Shared bounded, selectable view of the provisioner's safe status events.</summary>
public sealed class ProvisionProgressLog : TextBox
{
    private readonly Queue<string> _lines = new();
    private (VpsProvisionStage Stage, string Message, VpsProvisionRunState State, string StepId)? _last;

    public ProvisionProgressLog()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        AcceptsReturn = true;
        TextWrapping = TextWrapping.Wrap;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        FontSize = 12;
        AutomationProperties.SetName(this, "Журнал настройки VPS");
    }

    public int EventCount => _lines.Count;

    public void Append(VpsProvisionRun run)
    {
        var identity = (run.Stage, run.Message, run.State, run.StepId);
        if (_last == identity) return;
        var follow = SelectionLength == 0 && VerticalOffset + ViewportHeight >= ExtentHeight - 2;
        var previousOffset = VerticalOffset;
        _last = identity;
        var state = run.State switch
        {
            VpsProvisionRunState.StatusUnknown => "СТАТУС НЕИЗВЕСТЕН",
            VpsProvisionRunState.Paused => "ПАУЗА",
            VpsProvisionRunState.Failed => "ОШИБКА",
            VpsProvisionRunState.NeedsOperator => "ТРЕБУЕТСЯ ДЕЙСТВИЕ",
            VpsProvisionRunState.Complete => "ЗАВЕРШЕНО",
            _ => "ШАГ"
        };
        _lines.Enqueue($"{run.UpdatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {state}  {run.Message}");
        if (_lines.Count > 200)
        {
            _lines.Dequeue();
            Text = string.Join(Environment.NewLine, _lines) + Environment.NewLine;
        }
        else AppendText(_lines.Last() + Environment.NewLine);
        if (follow) ScrollToEnd();
        else ScrollToVerticalOffset(previousOffset);
    }
}
