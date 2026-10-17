using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Desktop;

/// <summary>Safe presentation projection for persisted and live VPS provisioning observations.</summary>
internal sealed class VpsProgressViewModel : INotifyPropertyChanged
{
    private static readonly (VpsProvisionStage Stage, string Title)[] Steps =
    [
        (VpsProvisionStage.Preflight, "Проверка VPS"),
        (VpsProvisionStage.PreparingDns, "Публичный DNS"),
        (VpsProvisionStage.PreparingAccess, "Подготовка SSH-доступа"),
        (VpsProvisionStage.VerifyingAdmin, "Проверка администратора"),
        (VpsProvisionStage.InstallingPanel, "Установка панели и TLS"),
        (VpsProvisionStage.RetrievingCredentials, "Получение данных панели")
    ];

    private VpsProvisionRun? _latest;
    private DateTimeOffset _clock = DateTimeOffset.UtcNow;
    private string _currentElapsed = "—";
    private string _lastContact = "Успешный ответ VPS ещё не получен.";
    private string _nextPoll = "";
    private string _stateText = "Ожидает запуска.";
    private string _currentOperation = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<VpsProgressStageRow> Stages { get; } = [];
    public ObservableCollection<VpsProgressDnsRow> DnsItems { get; } = [];
    public string CurrentElapsed { get => _currentElapsed; private set => Set(ref _currentElapsed, value); }
    public string LastContact { get => _lastContact; private set => Set(ref _lastContact, value); }
    public string NextPoll { get => _nextPoll; private set => Set(ref _nextPoll, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string CurrentOperation { get => _currentOperation; private set => Set(ref _currentOperation, value); }
    public string DnsSummary => DnsItems.Count == 0 ? "" : $"Подтверждено {DnsItems.Count(x => x.Status == ProbeStatus.Success)} из {DnsItems.Count} имён";

    public VpsProgressViewModel() => RebuildStages(null);

    public void SetHistory(IEnumerable<VpsProvisionRun> history)
    {
        foreach (var run in history) Update(run);
    }

    public void Update(VpsProvisionRun run)
    {
        if (run.ActiveStage is null && (run.Stage is VpsProvisionStage.NeedsAttention or VpsProvisionStage.Failed) && _latest is not null)
            run = run with { ActiveStage = _latest.ActiveStage ?? _latest.Stage };
        if (run.State == VpsProvisionRunState.Running)
        {
            var inferredState = run.Stage switch
            {
                VpsProvisionStage.Complete => VpsProvisionRunState.Complete,
                VpsProvisionStage.Failed => VpsProvisionRunState.Failed,
                VpsProvisionStage.NeedsAttention => VpsProvisionRunState.NeedsOperator,
                _ when run.Message.Contains("состояние задания временно недоступно", StringComparison.OrdinalIgnoreCase) => VpsProvisionRunState.StatusUnknown,
                _ => VpsProvisionRunState.Running
            };
            run = run with { State = inferredState };
        }
        _latest = run;
        _clock = DateTimeOffset.UtcNow;
        RebuildStages(run);
        DnsItems.Clear();
        foreach (var item in run.DnsItems)
            DnsItems.Add(new VpsProgressDnsRow(item.Name, item.RecordType, item.ExpectedAddress, DnsLabel(item.Status), item.Status));
        OnPropertyChanged(nameof(DnsSummary));
        StateText = StateLabel(run.State, run.Stage);
        CurrentOperation = OperationLabel(run);
        RefreshClock(_clock);
    }

    public void RefreshClock(DateTimeOffset now)
    {
        _clock = now;
        var latest = _latest;
        if (latest is null)
        {
            CurrentElapsed = "—";
            LastContact = "Успешный ответ VPS ещё не получен.";
            NextPoll = "";
            return;
        }

        if (latest.StepStartedAt is { } started)
        {
            var end = latest.State is VpsProvisionRunState.Running or VpsProvisionRunState.StatusUnknown ? now : latest.UpdatedAt;
            CurrentElapsed = FormatDuration(end - started);
        }
        else CurrentElapsed = "—";

        LastContact = latest.LastSuccessfulVpsContactAt is { } contact
            ? "Последний успешный ответ VPS: " + contact.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "Успешный ответ VPS ещё не получен.";
        NextPoll = latest.NextPollAt is { } due && (latest.State is VpsProvisionRunState.Running or VpsProvisionRunState.StatusUnknown)
            ? "Следующая проверка через " + FormatDuration(due - now)
            : "";
    }

    private void RebuildStages(VpsProvisionRun? latest)
    {
        Stages.Clear();
        var active = latest?.ActiveStage ?? latest?.Stage;
        var activeIndex = Array.FindIndex(Steps, step => step.Stage == active);
        var completed = latest?.State == VpsProvisionRunState.Complete || latest?.Stage == VpsProvisionStage.Complete;
        for (var index = 0; index < Steps.Length; index++)
        {
            var state = completed || index < activeIndex ? VpsProvisionStepState.Done
                : index > activeIndex ? VpsProvisionStepState.Pending
                : activeIndex < 0 ? VpsProvisionStepState.Pending
                : latest?.State is VpsProvisionRunState.NeedsOperator or VpsProvisionRunState.Failed or VpsProvisionRunState.Paused
                    ? VpsProvisionStepState.NeedsAttention : VpsProvisionStepState.Running;
            Stages.Add(new VpsProgressStageRow(Steps[index].Title, state, StepLabel(state)));
        }
    }

    private static string OperationLabel(VpsProvisionRun run)
    {
        var stage = run.ActiveStage ?? run.Stage;
        if (stage is VpsProvisionStage.NeedsAttention or VpsProvisionStage.Failed or VpsProvisionStage.Complete)
            stage = run.ActiveStage ?? stage;
        var step = run.StepId switch
        {
            "preflight" => "Проверка параметров и ОС",
            "public-dns" => "Ожидание публичного DNS",
            "prepare-access" => "Подготовка доступа",
            "verify-admin" => "Проверка нового администратора",
            "install-panel" => "Установка панели и TLS",
            "retrieve-credentials" => "Получение данных панели",
            "prepare-status" or "finalize-status" => "Повторная проверка того же удалённого задания",
            "prepare-start" or "finalize-start" => "Передача команды тому же сохранённому запуску",
            "startup" => "Запуск установщика",
            "validate" => "Проверка параметров",
            "apt-index" => "Обновление списка пакетов",
            "apt-install" => "Установка системных пакетов",
            "validate-access" => "Проверка SSH и правил доступа",
            "apt-upgrade" => "Обновление пакетов ОС",
            "admin-create" => "Создание администратора",
            "rollback" => "Настройка таймера восстановления",
            "firewall" => "Настройка UFW и портов",
            "prepare-done" => "Подготовка доступа завершена",
            "ssh-hardening" => "Защита SSH",
            "fail2ban" => "Настройка fail2ban",
            "security-updates" => "Автоматические обновления безопасности",
            "log-limits" => "Ограничение журналов и настройка сети",
            "tls-check" => "Проверка готовности HTTPS",
            "tls-issue" => "Выпуск сертификата Let’s Encrypt",
            "tls-deploy" => "Настройка сертификата и продления",
            "panel-download" => "Загрузка 3x-ui",
            "panel-verify" => "Проверка архива 3x-ui",
            "panel-configure" => "Настройка 3x-ui",
            "panel-start" => "Запуск 3x-ui",
            "backup" => "Настройка резервных копий",
            "health" => "Проверка панели и firewall",
            "finalize-done" => "Установка VPS завершена",
            _ => StageTitle(stage)
        };
        return step;
    }

    private static string StageTitle(VpsProvisionStage? stage) => stage switch
    {
        VpsProvisionStage.Preflight => "Проверка VPS",
        VpsProvisionStage.PreparingDns => "Публичный DNS",
        VpsProvisionStage.PreparingAccess => "Подготовка SSH-доступа",
        VpsProvisionStage.VerifyingAdmin => "Проверка администратора",
        VpsProvisionStage.InstallingPanel => "Установка панели и TLS",
        VpsProvisionStage.RetrievingCredentials => "Получение данных панели",
        _ => "Настройка VPS"
    };

    private static string StateLabel(VpsProvisionRunState state, VpsProvisionStage stage) => state switch
    {
        VpsProvisionRunState.Running => "Выполняется · без расчётного процента",
        VpsProvisionRunState.StatusUnknown => "Связь есть, состояние задания временно неизвестно; проверяется тот же запуск.",
        VpsProvisionRunState.Paused => "Наблюдение остановлено. Удалённое задание могло продолжиться.",
        VpsProvisionRunState.Failed => "Операция завершилась ошибкой. Проверьте сохранённое состояние перед повтором.",
        VpsProvisionRunState.NeedsOperator => "Требуется действие оператора перед продолжением.",
        VpsProvisionRunState.Complete => "Сервер подтвердил завершение установки.",
        _ => stage.ToString()
    };

    private static string DnsLabel(ProbeStatus status) => status switch
    {
        ProbeStatus.Success => "Подтверждено",
        ProbeStatus.Warning => "Ожидает распространения",
        ProbeStatus.Failure => "Не совпадает / требуется проверка",
        _ => "Ожидает проверки"
    };

    private static string StepLabel(VpsProvisionStepState status) => status switch
    {
        VpsProvisionStepState.Running => "Выполняется",
        VpsProvisionStepState.Done => "Готово",
        VpsProvisionStepState.NeedsAttention => "Требует внимания",
        _ => "Ожидает"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        var seconds = Math.Max(0, (int)duration.TotalSeconds);
        return seconds >= 3600 ? $"{seconds / 3600} ч {seconds % 3600 / 60:D2} мин" : $"{seconds / 60} мин {seconds % 60:D2} с";
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record VpsProgressStageRow(string Title, VpsProvisionStepState State, string StateText);
internal sealed record VpsProgressDnsRow(string Name, string RecordType, string ExpectedAddress, string StateText, ProbeStatus Status);
