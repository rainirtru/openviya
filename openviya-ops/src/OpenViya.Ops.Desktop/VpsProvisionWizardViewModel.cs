using OpenViya.Ops.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenViya.Ops.Desktop;

/// <summary>Owns the wizard's step progression and the freshness of connection checks.</summary>
internal sealed class VpsProvisionWizardViewModel : INotifyPropertyChanged
{
    private int _step;
    private bool _connectionReady;
    private string _connectionResult = "Подключение ещё не проверено.";

    public event PropertyChangedEventHandler? PropertyChanged;
    public int Step { get => _step; private set { if (_step != value) { _step = value; Changed(); Changed(nameof(StepTitle)); } } }
    public bool ConnectionReady { get => _connectionReady; private set { if (_connectionReady != value) { _connectionReady = value; Changed(); } } }
    public string ConnectionResult { get => _connectionResult; private set { if (_connectionResult != value) { _connectionResult = value; Changed(); } } }
    public string StepTitle => Step switch
    {
        0 => "1 · Подключение",
        1 => "2 · Параметры сервера",
        2 => "3 · Доступ к панели",
        _ => "4 · Проверка и установка"
    };

    public void InvalidateConnection(string reason)
    {
        ConnectionReady = false;
        ConnectionResult = reason;
    }

    public void SetConnectionResult(bool ready, string message)
    {
        ConnectionReady = ready;
        ConnectionResult = message;
    }

    public bool CanAdvance(int step, string? validationError = null, bool hostKeyTrusted = false, bool confirmationReady = false) => step switch
    {
        0 => hostKeyTrusted && ConnectionReady,
        1 => validationError is null,
        2 => validationError is null,
        3 => validationError is null && confirmationReady,
        _ => false
    };

    public void MoveTo(int step)
    {
        if (step is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(step));
        Step = step;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
