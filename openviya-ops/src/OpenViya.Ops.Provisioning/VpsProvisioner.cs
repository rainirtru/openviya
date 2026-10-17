using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Provisioning;

public sealed record VpsProvisionTiming
{
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PrepareTimeout { get; init; } = TimeSpan.FromMinutes(45);
    public TimeSpan FinalizeTimeout { get; init; } = TimeSpan.FromMinutes(25);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan DnsPollInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Fresh-host workflow. Remote mutations are confined to the bundled bootstrap protocol.</summary>
public sealed class VpsProvisioner : IVpsProvisioner
{
    private readonly IProfileStore _profiles;
    private readonly IVpsProvisionStore _store;
    private readonly IVpsRemoteSessionFactory _sessions;
    private readonly IVpsKeyService _keys;
    private readonly IDnsProviderService _dns;
    private readonly IPublicDnsService _publicDns;
    private readonly VpsProvisionTiming _timing;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VpsProvisioner(AppPaths paths, IProfileStore profiles, IVpsProvisionStore store,
        IVpsRemoteSessionFactory sessions, IVpsKeyService keys, IDnsProviderService dns,
        IPublicDnsService publicDns, VpsProvisionTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _profiles = profiles; _store = store; _sessions = sessions; _keys = keys;
        _dns = dns; _publicDns = publicDns; _timing = timing ?? new();
        if (_timing.DnsTimeout <= TimeSpan.Zero || _timing.PrepareTimeout <= TimeSpan.Zero ||
            _timing.FinalizeTimeout <= TimeSpan.Zero || _timing.PollInterval < TimeSpan.Zero || _timing.DnsPollInterval < TimeSpan.Zero)
            throw new ArgumentException("Некорректные интервалы наблюдения за настройкой VPS.", nameof(timing));
    }

    public async Task<VpsProvisionResult> RunAsync(VpsProvisionRequest request,
        IProgress<VpsProvisionRun>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = Validate(request);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Другая полная настройка уже выполняется. Дождитесь её завершения.");
        var run = new VpsProvisionRun(request.RunId, profile.Id, VpsProvisionStage.Created, "Подготовка локального запуска.", DateTimeOffset.UtcNow);
        IVpsRemoteSession? initial = null;
        IVpsRemoteSession? admin = null;
        var remoteJobStarted = false;
        try
        {
            var previous = await _store.LoadLatestRunAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            if (previous is not null && previous.RunId != request.RunId && previous.Stage != VpsProvisionStage.Complete)
                throw new SetupException("Для этого сервера уже есть запуск. Используйте его продолжение, чтобы не запускать prepare повторно.");
            if (previous is not null && previous.RunId == request.RunId)
                run = run with
                {
                    StepId = previous.StepId,
                    ActiveStage = previous.ActiveStage,
                    StepStartedAt = previous.StepStartedAt,
                    LastSuccessfulVpsContactAt = previous.LastSuccessfulVpsContactAt,
                    DnsItems = previous.DnsItems
                };
            await Report(VpsProvisionStage.Preflight, "Проверка параметров и подготовка локального ключа администратора.").ConfigureAwait(false);
            var adminIdentity = previous is not null && string.IsNullOrWhiteSpace(request.AdminIdentityFile) ? profile.IdentityFile : request.AdminIdentityFile;
            var key = await _keys.PrepareAsync(profile.Id, adminIdentity, request.AdminKeyPassphrase, cancellationToken).ConfigureAwait(false);
            profile = profile with
            {
                IdentityFile = key.IdentityFile,
                PanelAccess = request.PanelAccess,
                PanelAllowedIpsText = request.PanelAccess == VpsPanelAccess.Both ? profile.PanelAllowedIpsText : "",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _profiles.SaveAsync(profile, cancellationToken).ConfigureAwait(false);

            // A resumed run normally has root login disabled already. Try the known admin key first.
            if (previous is not null)
            {
                try { admin = await ConnectAdmin().ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { /* Initial access is the fallback while prepare has not completed. */ }
            }
            if (admin is null)
            {
                ValidateInitialAccess(request.InitialAccess);
                await Report(VpsProvisionStage.Preflight, "Подключение к VPS: начальный SSH-вход и проверка SFTP.").ConfigureAwait(false);
                initial = await _sessions.ConnectAsync(profile, request.InitialAccess, request.HostKey, cancellationToken).ConfigureAwait(false);
                await MarkVpsContact().ConfigureAwait(false);
            }
            var active = admin ?? initial!;
            var prefix = admin is not null ? "sudo -n " : request.InitialAccess.UserName != "root"
                ? "sudo -n --preserve-env=SSH_CONNECTION " : "";
            var rootCheck = await active.ExecuteAsync(prefix + "id -u", cancellationToken).ConfigureAwait(false);
            if (rootCheck.ExitCode != 0 || rootCheck.Output.Trim() != "0")
                throw new SetupException("Для установки нужен root либо пользователь с sudo без запроса пароля.");
            await MarkVpsContact().ConfigureAwait(false);

            await Report(VpsProvisionStage.Preflight, "Передача установщика и проверка контрольной суммы на VPS.").ConfigureAwait(false);
            var staged = await StageBootstrap(active, prefix, request.RunId, key.PublicKey, cancellationToken,
                message => Report(VpsProvisionStage.Preflight, message)).ConfigureAwait(false);
            await MarkVpsContact().ConfigureAwait(false);
            var helper = staged.Helper;
            await Report(VpsProvisionStage.Preflight, "Проверка ОС, существующих служб и правил доступа VPS.").ConfigureAwait(false);
            var inspection = await InspectVps(active, prefix, helper, profile, cancellationToken).ConfigureAwait(false);
            if (!inspection.Supported)
                throw new SetupException("Поддерживаются Ubuntu 26.04/24.04 или Debian 12/13, amd64/arm64 и systemd.");
            if (inspection.Phase is "rolled-back" or "rolled_back")
                throw new SetupException("Сработало восстановление доступа. Автоматический prepare не повторяется: проверьте сервер через консоль провайдера.");

            if (IsFreshPhase(inspection.Phase))
            {
                var existingJob = await JobStatus(active, prefix, helper, request.RunId, "prepare", cancellationToken).ConfigureAwait(false);
                await MarkVpsContact().ConfigureAwait(false);
                if (existingJob.State == "running")
                {
                    remoteJobStarted = true;
                    await Report(VpsProvisionStage.PreparingAccess, "Продолжается уже запущенная подготовка доступа на VPS.").ConfigureAwait(false);
                    await WaitJob(active, prefix, helper, request.RunId, "prepare", _timing.PrepareTimeout, cancellationToken, observation => Report(VpsProvisionStage.PreparingAccess, observation.Message, observation.StepId, observation.State, observation.ContactAt, observation.NextPollAt)).ConfigureAwait(false);
                }
                else
                {
                    if (!inspection.Fresh)
                        throw new SetupException("Сервер не выглядит свежим: обнаружены существующая установка, администратор, службы или правила доступа. Автоматическая переустановка запрещена.");
                    if (existingJob.State != "missing")
                        throw new SetupException(JobFailureMessage(existingJob, "prepare"));
                    if (previous is { ActiveStage: VpsProvisionStage.PreparingAccess } priorPrepare && priorPrepare.RunId == request.RunId)
                        throw new SetupException("Сохранённый запуск уже доходил до подготовки доступа, но его состояние сейчас не подтверждено. Автоматический повтор запрещён; проверьте задание через SSH или консоль провайдера.");
                    if (!staged.SupportsSourceList && request.PanelCidr.Contains('\n'))
                        throw new SetupException("Сохранённый установщик не поддерживает список IP. Создайте новый запуск; настройки VPS не изменены.");
                    await Report(VpsProvisionStage.PreparingDns, "Подготовка поддоменов и ожидание публичного DNS до изменения доступа VPS.").ConfigureAwait(false);
                    await PrepareDns(profile, cancellationToken, async (items, nextPoll) =>
                    {
                        var waiting = items.Count(x => x.Status != ProbeStatus.Success);
                        var message = waiting == 0 ? "Публичный DNS подтвердил все ожидаемые записи." :
                            $"Проверка публичного DNS: подтверждено {items.Count - waiting} из {items.Count} имён.";
                        await Report(VpsProvisionStage.PreparingDns, message, "public-dns",
                            VpsProvisionRunState.Running, nextPollAt: nextPoll, dnsItems: items).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    await Report(VpsProvisionStage.PreparingAccess, "Установка обновлений, создание администратора и настройка доступа. SSH rollback остаётся на VPS.").ConfigureAwait(false);
                    var command = prefix + Quote(helper) + " desktop-prepare --run-id " + request.RunId.ToString("N") +
                        " --admin " + Quote(profile.UserName) + " --key-file " + Quote(HelperDirectory(request.RunId) + "/admin.pub") +
                        " --ssh-port " + profile.SshPort.ToString(CultureInfo.InvariantCulture) + " --ssh-cidr any" +
                        " --domain " + Quote(profile.PanelDomain) + " --acme-email " + Quote(request.AcmeEmail) +
                        " --panel-port " + profile.PanelPort.ToString(CultureInfo.InvariantCulture) +
                        " --panel-access " + (request.PanelAccess == VpsPanelAccess.Both ? "both" : "tunnel") +
                        " --panel-cidr " + Quote(request.PanelCidr) + " --vpn-tcp 443 --vpn-udp ''";
                    remoteJobStarted = true;
                    await Report(VpsProvisionStage.PreparingAccess, "Запуск удалённого задания подготовки для сохранённого Run ID.", "prepare-start").ConfigureAwait(false);
                    await StartJob(active, command, cancellationToken).ConfigureAwait(false);
                    await MarkVpsContact().ConfigureAwait(false);
                    await WaitJob(active, prefix, helper, request.RunId, "prepare", _timing.PrepareTimeout, cancellationToken, observation => Report(VpsProvisionStage.PreparingAccess, observation.Message, observation.StepId, observation.State, observation.ContactAt, observation.NextPollAt)).ConfigureAwait(false);
                }
                inspection = await InspectVps(active, prefix, helper, profile, cancellationToken).ConfigureAwait(false);
            }

            if (inspection.Phase == "unknown")
            {
                var unfinished = await JobStatus(active, prefix, helper, request.RunId, "prepare", cancellationToken).ConfigureAwait(false);
                await MarkVpsContact().ConfigureAwait(false);
                if (unfinished.State == "failed") throw new SetupException(JobFailureMessage(unfinished, "prepare"));
            }
            if (inspection.Phase is not ("pending" or "complete"))
                throw new SetupException("Подготовка не достигла ожидаемого состояния. Проверьте VPS; сохранённые данные не перезаписываются повторным prepare.");
            if (inspection.Phase == "pending")
                await WaitJob(active, prefix, helper, request.RunId, "prepare", _timing.PrepareTimeout, cancellationToken,
                    observation => Report(VpsProvisionStage.PreparingAccess, observation.Message, observation.StepId, observation.State, observation.ContactAt, observation.NextPollAt)).ConfigureAwait(false);
            await Report(VpsProvisionStage.VerifyingAdmin, "Проверка НОВОГО SSH-подключения по ключу администратора.").ConfigureAwait(false);
            // The initial connection remains open until the new account has successfully authenticated.
            admin ??= await ConnectAdmin().ConfigureAwait(false);
            var adminCheck = await admin.ExecuteAsync("sudo -n id -u", cancellationToken).ConfigureAwait(false);
            if (adminCheck.ExitCode != 0 || adminCheck.Output.Trim() != "0")
                throw new SetupException("Новый администратор не прошёл проверку sudo. Finalize не запущен; проверьте таймер восстановления доступа.");

            if (inspection.Phase == "pending")
            {
                if (!inspection.RollbackActive)
                    throw new SetupException("Таймер восстановления доступа уже истёк либо остановлен. Finalize не запущен; требуется проверка через консоль провайдера.");
                await Report(VpsProvisionStage.InstallingPanel, "Защита SSH, fail2ban, Let’s Encrypt и установка закреплённой версии 3x-ui.").ConfigureAwait(false);
                var job = await JobStatus(admin, "sudo -n ", helper, request.RunId, "finalize", cancellationToken).ConfigureAwait(false);
                await MarkVpsContact().ConfigureAwait(false);
                if (job.State == "missing")
                {
                    if (previous is { ActiveStage: VpsProvisionStage.InstallingPanel } priorFinalize && priorFinalize.RunId == request.RunId)
                        throw new SetupException("Сохранённый запуск уже доходил до установки панели, но состояние задания сейчас не подтверждено. Автоматический повтор запрещён; проверьте VPS перед продолжением.");
                    var command = "sudo -n --preserve-env=SSH_CONNECTION " + Quote(helper) +
                        " desktop-finalize --run-id " + request.RunId.ToString("N");
                    remoteJobStarted = true;
                    await Report(VpsProvisionStage.InstallingPanel, "Запуск удалённого задания установки для сохранённого Run ID.", "finalize-start").ConfigureAwait(false);
                    await StartJob(admin, command, cancellationToken).ConfigureAwait(false);
                    await MarkVpsContact().ConfigureAwait(false);
                }
                await WaitJob(admin, "sudo -n ", helper, request.RunId, "finalize", _timing.FinalizeTimeout, cancellationToken, observation => Report(VpsProvisionStage.InstallingPanel, observation.Message, observation.StepId, observation.State, observation.ContactAt, observation.NextPollAt)).ConfigureAwait(false);
            }

            await Report(VpsProvisionStage.RetrievingCredentials, "Получение данных панели по проверенному SSH-каналу и шифрование локального хранения.").ConfigureAwait(false);
            var finalState = await InspectVps(admin, "sudo -n ", helper, profile, cancellationToken).ConfigureAwait(false);
            if (finalState.Phase != "complete") throw new SetupException("Сервер не подтвердил завершение установки; данные панели не сохранены.");
            var response = await admin.ExecuteAsync("sudo -n " + Quote(helper) + " desktop-credentials", cancellationToken).ConfigureAwait(false);
            if (response.ExitCode != 0) throw new SetupException("Не удалось получить данные панели. Настройка может быть завершена: продолжите получение данных после проверки сервера.");
            var credentials = ParseCredentials(response.Output, profile);
            await MarkVpsContact().ConfigureAwait(false);
            await _store.SaveCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
            profile = profile with { PanelBasePath = credentials.PanelPath, UpdatedAt = DateTimeOffset.UtcNow };
            await _profiles.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            await Report(VpsProvisionStage.Complete, "Установка подтверждена. Данные панели сохранены зашифрованными; сервер готов к подключению через туннель.").ConfigureAwait(false);
            return new VpsProvisionResult(run, profile, true);
        }
        catch (Exception exception)
        {
            var canceled = exception is OperationCanceledException;
            var message = canceled
                ? remoteJobStarted ? "Наблюдение остановлено. Задание на VPS может продолжаться; таймер восстановления доступа работает независимо от приложения. Продолжите проверку этого запуска." : "Настройка остановлена. Проверьте сохранённое состояние перед повтором."
                : exception is VpsKeyPassphraseRequiredException keyPassphrase ? keyPassphrase.Message
                : exception is SetupException known ? known.Message : "Остановка на шаге: " + run.Message + " Проверьте доступ и консоль провайдера; сырые сообщения SSH не сохраняются.";
            run = run with
            {
                Stage = VpsProvisionStage.NeedsAttention,
                Message = message,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = canceled ? VpsProvisionRunState.Paused : exception is SetupException or VpsKeyPassphraseRequiredException
                    ? VpsProvisionRunState.NeedsOperator : VpsProvisionRunState.Failed,
                NextPollAt = null
            };
            try { await _store.SaveRunAsync(run, CancellationToken.None).ConfigureAwait(false); } catch { }
            progress?.Report(run);
            return new VpsProvisionResult(run, profile, false);
        }
        finally
        {
            try { if (admin is not null) await admin.DisposeAsync().ConfigureAwait(false); } catch { }
            try { if (initial is not null) await initial.DisposeAsync().ConfigureAwait(false); } catch { }
            _gate.Release();
        }

        async Task Report(VpsProvisionStage stage, string message, string? stepId = null,
            VpsProvisionRunState state = VpsProvisionRunState.Running, DateTimeOffset? contactAt = null,
            DateTimeOffset? nextPollAt = null, IReadOnlyList<VpsProvisionDnsItem>? dnsItems = null)
        {
            var now = DateTimeOffset.UtcNow;
            stepId ??= StageStepId(stage);
            if (stage == VpsProvisionStage.Complete) state = VpsProvisionRunState.Complete;
            else if (stage == VpsProvisionStage.Failed) state = VpsProvisionRunState.Failed;
            else if (stage == VpsProvisionStage.NeedsAttention && state == VpsProvisionRunState.Running) state = VpsProvisionRunState.NeedsOperator;
            var stepStartedAt = StringComparer.Ordinal.Equals(run.StepId, stepId) && run.StepStartedAt.HasValue
                ? run.StepStartedAt : now;
            run = run with
            {
                Stage = stage, ActiveStage = stage, Message = message, UpdatedAt = now, StepId = stepId,
                StepStartedAt = stepStartedAt,
                LastSuccessfulVpsContactAt = contactAt ?? run.LastSuccessfulVpsContactAt,
                NextPollAt = nextPollAt, State = state,
                DnsItems = dnsItems ?? run.DnsItems
            };
            await _store.SaveRunAsync(run, cancellationToken).ConfigureAwait(false);
            progress?.Report(run);
        }
        async Task MarkVpsContact()
        {
            var now = DateTimeOffset.UtcNow;
            run = run with { UpdatedAt = now, LastSuccessfulVpsContactAt = now };
            await _store.SaveRunAsync(run, cancellationToken).ConfigureAwait(false);
            progress?.Report(run);
        }
        async Task<VpsInspection> InspectVps(IVpsRemoteSession session, string prefix, string helper,
            ServerProfile inspectedProfile, CancellationToken ct)
        {
            var inspection = await Inspect(session, prefix, helper, inspectedProfile, ct).ConfigureAwait(false);
            await MarkVpsContact().ConfigureAwait(false);
            return inspection;
        }
        static string StageStepId(VpsProvisionStage stage) => stage switch
        {
            VpsProvisionStage.Preflight => "preflight",
            VpsProvisionStage.PreparingDns => "public-dns",
            VpsProvisionStage.PreparingAccess => "prepare-access",
            VpsProvisionStage.VerifyingAdmin => "verify-admin",
            VpsProvisionStage.InstallingPanel => "install-panel",
            VpsProvisionStage.RetrievingCredentials => "retrieve-credentials",
            VpsProvisionStage.Complete => "complete",
            VpsProvisionStage.NeedsAttention => "needs-attention",
            _ => "created"
        };
        async Task<IVpsRemoteSession> ConnectAdmin()
        {
            var connected = await _sessions.ConnectAsync(profile,
                new VpsSshAccess { UserName = profile.UserName, Method = VpsAuthMethod.PrivateKey,
                    IdentityFile = profile.IdentityFile, KeyPassphrase = request.AdminKeyPassphrase }, request.HostKey, cancellationToken).ConfigureAwait(false);
            await MarkVpsContact().ConfigureAwait(false);
            return connected;
        }
    }

    private async Task PrepareDns(ServerProfile profile, CancellationToken ct,
        Func<IReadOnlyList<VpsProvisionDnsItem>, DateTimeOffset?, Task>? report = null)
    {
        DnsPlan plan;
        if (profile.SubdomainMode == SubdomainMode.Automatic)
        {
            try { plan = await _dns.BuildPlanAsync(profile, cancellationToken: ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { throw new SetupException("Не удалось подготовить DNS. Настройте токен Timeweb в главном окне и проверьте доступ к зоне; доступ VPS ещё не изменён."); }
            if (!plan.CanApply)
            {
                var conflicts = plan.Items.Where(x => x.Action == DnsPlanAction.Conflict && x.BlockReason == DnsBlockReason.RecordConflict &&
                    ProfileValidator.IsValidDomain(x.Fqdn) && (x.Fqdn == profile.PanelDomain || x.Fqdn == profile.VpnDomain))
                    .Select(x => x.Fqdn).Distinct().Take(2).ToArray();
                if (conflicts.Length > 0)
                    throw new SetupException("DNS: имя уже занято другой записью: " + string.Join(", ", conflicts) +
                        ". Выберите свободный номер сервера или исправьте существующую запись у провайдера. Автоматическая замена не выполняется; доступ VPS ещё не изменён.");
                throw new SetupException("DNS не готов: проверьте токен, доступ к зоне, её NS и конфликтующие записи. Доступ VPS ещё не изменён.");
            }
            DnsApplyResult applied;
            try { applied = await _dns.ApplyAsync(plan, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { throw new SetupException("Ответ применения DNS не получен. Сверьте записи у провайдера перед продолжением: некоторые могли создаться. Установка VPS не началась."); }
            if (applied.Items.Count != plan.Items.Count || applied.Items.Any(x => x.Status != ProbeStatus.Success))
                throw new SetupException("Создание DNS не полностью подтверждено. Сверьте записи у провайдера; установка VPS не началась.");
        }
        else
        {
            var address = string.IsNullOrWhiteSpace(profile.DnsAddress) ? profile.Host : profile.DnsAddress;
            var type = IPAddress.Parse(address).AddressFamily == AddressFamily.InterNetwork ? "A" : "AAAA";
            var names = profile.Role == ServerRole.Control ? new[] { profile.PanelDomain } : new[] { profile.PanelDomain, profile.VpnDomain };
            plan = new DnsPlan(profile, profile.DnsZone, address, 600,
                names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(x => new DnsPlanItem(x, type, address, DnsPlanAction.AlreadyConfigured, "Проверка вручную созданной записи.")).ToArray(), [], DateTimeOffset.UtcNow);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timing.DnsTimeout);
        try
        {
            var orderedItems = plan.Items.OrderBy(x => x.Fqdn.TrimEnd('.'), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Type, StringComparer.OrdinalIgnoreCase).ToArray();
            var currentItems = orderedItems.Select(x => new VpsProvisionDnsItem(x.Fqdn, x.Type, x.Address, ProbeStatus.Unknown)).ToArray();
            if (report is not null) await report(currentItems, null).ConfigureAwait(false);
            while (true)
            {
                var results = await _publicDns.CheckAsync(plan, timeout.Token).ConfigureAwait(false);
                // PublicDnsService returns the delegation probe first, followed by one probe per item.
                var delegationOkay = results.Count == plan.Items.Count + 1 &&
                    (results[0].Status == ProbeStatus.Success || plan.NameServers.Count == 0 && results[0].Status == ProbeStatus.Warning);
                currentItems = orderedItems.Select((item, index) => new VpsProvisionDnsItem(
                    item.Fqdn, item.Type, item.Address,
                    results.Count == plan.Items.Count + 1 ? results[index + 1].Status : ProbeStatus.Unknown)).ToArray();
                if (report is not null) await report(currentItems, null).ConfigureAwait(false);
                if (delegationOkay && currentItems.All(x => x.Status == ProbeStatus.Success)) return;
                var nextPoll = DateTimeOffset.UtcNow + _timing.DnsPollInterval;
                if (report is not null) await report(currentItems, nextPoll).ConfigureAwait(false);
                await Task.Delay(_timing.DnsPollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new SetupException("Публичный DNS пока не подтвердил нужные IP и делегирование. Доступ VPS не изменён; проверьте A/AAAA/NS и повторите позже."); }
    }

    private static async Task<(string Helper, bool SupportsSourceList)> StageBootstrap(IVpsRemoteSession session, string prefix, Guid runId, string publicKey, CancellationToken ct, Func<string, Task>? report = null)
    {
        var staging = "/tmp/openviya-" + runId.ToString("N");
        var directory = HelperDirectory(runId);
        var helper = directory + "/bootstrap.sh";
        using var resource = typeof(VpsProvisioner).Assembly.GetManifestResourceStream("OpenViya.Ops.Provisioning.bootstrap-vps.sh")
            ?? throw new SetupException("В сборке отсутствует закреплённый bootstrap-скрипт.");
        using var reader = new StreamReader(resource, new UTF8Encoding(false, true));
        var script = Encoding.UTF8.GetBytes((await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal));
        var hash = Convert.ToHexStringLower(SHA256.HashData(script));
        // Existing root-owned assets are never overwritten, especially while a persistent job is running.
        var existing = await session.ExecuteAsync(prefix + "test -f " + Quote(helper), ct).ConfigureAwait(false);
        if (existing.ExitCode == 0)
        {
            var supportsSourceList = true;
            var verify = await session.ExecuteAsync("printf '%s  %s\\n' " + Quote(hash) + " " + Quote(helper) + " | " + prefix + "sha256sum --check --status", ct).ConfigureAwait(false);
            if (verify.ExitCode != 0)
            {
                supportsSourceList = false;
                // The previous released helper has no substeps; observe it without replacing a running script.
                var legacyVerified = false;
                foreach (var previousReleaseHash in new[] { "d1c7b83a3d1c941295513e8f8da0f4eb7421575d8c35f99692f571789a475bb3", "375b8d34ba658126d2592ef35c4539700305788516c0c273423fb8cbedd4b18b" })
                {
                    var legacy = await session.ExecuteAsync("printf '%s  %s\\n' " + Quote(previousReleaseHash) + " " + Quote(helper) + " | " + prefix + "sha256sum --check --status", ct).ConfigureAwait(false);
                    if (legacy.ExitCode == 0) { legacyVerified = true; break; }
                }
                if (!legacyVerified) throw new SetupException("Контрольная сумма установщика VPS не совпадает с поддерживаемой версией. Работающий скрипт не заменён; требуется проверка.");
                if (report is not null) await report("Сохранённый запуск использует предыдущий установщик: доступны общие этапы, подробные шаги на VPS не передаются.").ConfigureAwait(false);
            }
            var publicHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey.Trim() + "\n")));
            var verifyPublic = await session.ExecuteAsync("printf '%s  %s\\n' " + Quote(publicHash) + " " + Quote(directory + "/admin.pub") + " | " + prefix + "sha256sum --check --status", ct).ConfigureAwait(false);
            if (verifyPublic.ExitCode != 0) throw new SetupException("Ключ администратора отличается от подготовленного для этого запуска. Используйте исходный ключ; настройки доступа не изменены.");
            return (helper, supportsSourceList);
        }
        var safeStaging = "umask 077; if [ -e " + Quote(staging) + " ] || [ -L " + Quote(staging) +
            " ]; then [ -d " + Quote(staging) + " ] && [ ! -L " + Quote(staging) + " ] && [ \"$(stat -c %u -- " +
            Quote(staging) + ")\" = \"$(id -u)\" ] && [ \"$(stat -c %a -- " + Quote(staging) +
            ")\" = 700 ]; else mkdir -m 700 -- " + Quote(staging) + "; fi";
        await Require(session, safeStaging, ct).ConfigureAwait(false);
        await session.UploadAsync(staging + "/bootstrap-vps.sh", script, ct).ConfigureAwait(false);
        await session.UploadAsync(staging + "/admin.pub", Encoding.UTF8.GetBytes(publicKey.Trim() + "\n"), ct).ConfigureAwait(false);
        await Require(session, prefix + "install -d -o root -g root -m 700 -- /var/lib/openviya-desktop " + Quote(directory), ct).ConfigureAwait(false);
        // The script is installed last: its presence marks a complete, reusable staging transaction.
        await Require(session, prefix + "install -o root -g root -m 600 -- " + Quote(staging + "/admin.pub") + " " + Quote(directory + "/admin.pub"), ct).ConfigureAwait(false);
        await Require(session, prefix + "install -o root -g root -m 700 -- " + Quote(staging + "/bootstrap-vps.sh") + " " + Quote(helper), ct).ConfigureAwait(false);
        await Require(session, "printf '%s  %s\\n' " + Quote(hash) + " " + Quote(helper) + " | " + prefix + "sha256sum --check --status", ct).ConfigureAwait(false);
        return (helper, true);
    }

    private static async Task<VpsInspection> Inspect(IVpsRemoteSession session, string prefix, string helper, ServerProfile profile, CancellationToken ct)
    {
        var result = await session.ExecuteAsync(prefix + Quote(helper) + " desktop-inspect --admin " + Quote(profile.UserName) + " --ssh-port " + profile.SshPort.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new SetupException("Не удалось проверить состояние сервера без изменений его настроек.");
        using var doc = MachineDocument(result.Output);
        var r = doc.RootElement;
        var phase = RequiredText(r, "phase");
        if (phase is not ("fresh" or "none" or "pending" or "complete" or "rolled-back" or "unknown"))
            throw new SetupException("VPS вернул неизвестную фазу подготовки.");
        return new VpsInspection(RequiredText(r, "os"), RequiredText(r, "version"), RequiredText(r, "architecture"),
            RequiredBoolean(r, "supported"), RequiredBoolean(r, "fresh"), phase, RequiredBoolean(r, "rollbackActive"), "Состояние VPS прочитано.");
    }
    private async Task WaitJob(IVpsRemoteSession session, string prefix, string helper, Guid runId, string stage, TimeSpan duration, CancellationToken ct, Func<VpsProvisionJobProgress, Task>? report = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        try
        {
            var missingReads = 0;
            while (true)
            {
                var status = await JobStatus(session, prefix, helper, runId, stage, timeout.Token).ConfigureAwait(false);
                // Updating systemd can briefly hide a transient unit during reexec.
                // Observe the same job again; never relaunch it on missing status.
                if (status.State == "missing" && ++missingReads <= 20)
                {
                    if (report is not null)
                    await report(new VpsProvisionJobProgress("VPS: состояние задания временно недоступно; повторная проверка того же запуска.",
                            stage + "-status", VpsProvisionRunState.StatusUnknown, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + _timing.PollInterval)).ConfigureAwait(false);
                    await Task.Delay(_timing.PollInterval, timeout.Token).ConfigureAwait(false);
                    continue;
                }
                if (status.State != "missing") missingReads = 0;
                var label = RemoteStepLabel(status.Step);
                var message = status.State == "succeeded" ? "VPS: " + (stage == "prepare" ? "подготовка доступа завершена." : "установка панели завершена.")
                    : label is null ? "VPS: " + (stage == "prepare" ? "подготовка доступа" : "установка панели") + ". Подробный шаг ещё не передан." : "VPS: " + label + ".";
                if (report is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    await report(new VpsProvisionJobProgress(message, string.IsNullOrEmpty(status.Step) ? stage + "-job" : status.Step,
                        status.State == "missing" ? VpsProvisionRunState.StatusUnknown : VpsProvisionRunState.Running,
                        now, DateTimeOffset.UtcNow + _timing.PollInterval)).ConfigureAwait(false);
                }
                if (status.Phase is "rolled-back" or "rolled_back") throw new SetupException("На VPS сработал rollback доступа. Автоматическое продолжение остановлено; проверьте консоль провайдера.");
                if (status.State == "succeeded" && status.ExitCode == 0 &&
                    (stage == "prepare" && status.Phase is "pending" or "complete" || stage == "finalize" && status.Phase == "complete")) return;
                if (status.State == "succeeded") throw new SetupException("Задание завершилось без подтверждения ожидаемой фазы VPS.");
                if (status.State is "failed" or "missing") throw new SetupException(JobFailureMessage(status, stage));
                await Task.Delay(_timing.PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new SetupException("Истёк срок наблюдения за удалённым этапом. Задание может продолжаться; проверьте этот запуск и консоль провайдера."); }
    }
    private static async Task<Job> JobStatus(IVpsRemoteSession session, string prefix, string helper, Guid runId, string stage, CancellationToken ct)
    {
        var result = await session.ExecuteAsync(prefix + Quote(helper) + " desktop-job-status --run-id " + runId.ToString("N") + " --stage " + stage, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new SetupException("Не удалось прочитать состояние удалённого задания.");
        using var doc = MachineDocument(result.Output);
        var r = doc.RootElement;
        var state = Text(r, "state");
        if (state is not ("running" or "succeeded" or "failed" or "missing")) throw new SetupException("VPS вернул неизвестное состояние задания.");
        var code = Number(r, "exitCode");
        if (code < -1 || code > 255) throw new SetupException("VPS не подтвердил код завершения задания.");
        var step = Text(r, "step");
        return new Job(state, code, RequiredText(r, "phase"), RemoteStepLabel(step) is null ? "" : step);
    }
    private static async Task StartJob(IVpsRemoteSession session, string command, CancellationToken ct)
    {
        var result = await session.ExecuteAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new SetupException("VPS не подтвердил запуск этапа. Проверьте сохранённый запуск перед повтором: задание могло уже начаться.");
    }
    private static async Task Require(IVpsRemoteSession session, string command, CancellationToken ct)
    {
        if ((await session.ExecuteAsync(command, ct).ConfigureAwait(false)).ExitCode != 0)
            throw new SetupException("Не удалось подготовить защищённые файлы установки на VPS. Настройки доступа не изменялись этим этапом.");
    }
    private static VpsPanelCredentials ParseCredentials(string output, ServerProfile profile)
    {
        using var doc = MachineDocument(output);
        var r = doc.RootElement;
        var domain = Text(r, "domain"); var port = Number(r, "port");
        var path = Text(r, "path"); var username = Text(r, "username"); var password = Text(r, "password");
        if (Text(r, "phase") != "complete" || Text(r, "tlsMode") != "letsencrypt" ||
            !string.Equals(domain, profile.PanelDomain, StringComparison.OrdinalIgnoreCase) || port != profile.PanelPort ||
            !ProfileValidator.IsValidBasePath(path) || path.Length > 2048 || username.Length is < 1 or > 128 ||
            username.Any(char.IsControl) || password.Length is < 12 or > 4096 || password.Any(char.IsControl))
            throw new SetupException("Полученные данные панели не соответствуют профилю или завершённой TLS-установке. Секреты не сохранены.");
        return new VpsPanelCredentials { ServerId = profile.Id, UserName = username, Password = password,
            PanelPath = path, PanelDomain = domain, PanelPort = port, RetrievedAt = DateTimeOffset.UtcNow };
    }
    private static ServerProfile Validate(VpsProvisionRequest r)
    {
        if (r.Profile is null || r.InitialAccess is null || r.HostKey is null || r.RunId == Guid.Empty)
            throw new ArgumentException("Не заданы обязательные параметры запуска.");
        if (r.InitialAccess.Method == VpsAuthMethod.PrivateKey && VpsKeyFiles.IsPublicKeyPath(r.InitialAccess.IdentityFile) || VpsKeyFiles.IsPublicKeyPath(r.AdminIdentityFile))
            throw new ArgumentException(VpsKeyFiles.PublicKeySelectedMessage);
        var p = r.Profile;
        var errors = ProfileValidator.Validate(p);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        p = ProfileDefaults.Apply(p);
        var idn = new IdnMapping();
        p = p with { DnsZone = idn.GetAscii(p.DnsZone).ToLowerInvariant(), PanelDomain = idn.GetAscii(p.PanelDomain).ToLowerInvariant(),
            VpnDomain = string.IsNullOrEmpty(p.VpnDomain) ? "" : idn.GetAscii(p.VpnDomain).ToLowerInvariant() };
        if (p.Role == ServerRole.App) throw new ArgumentException("Этот мастер устанавливает 3x-ui только на VPN_NODE или CONTROL.");
        if (!r.FreshServerConfirmed || !r.LetsEncryptTermsAccepted || !r.ProviderFirewallConfirmed)
            throw new ArgumentException("Подтвердите свежий VPS, условия Let’s Encrypt и доступность TCP 80/SSH в firewall провайдера.");
        if (!ProfileValidator.IsPublicIpLiteral(p.Host)) throw new ArgumentException("Для первичной настройки укажите публичный IP VPS без hostname.");
        if (!string.IsNullOrWhiteSpace(p.DnsAddress) && (!IPAddress.TryParse(p.DnsAddress, out var dnsIp) || !dnsIp.Equals(IPAddress.Parse(p.Host))))
            throw new ArgumentException("В мастере DNS-адрес должен совпадать с публичным IP подключаемого VPS.");
        if (!Regex.IsMatch(p.UserName, "^[a-z][a-z0-9_-]{0,30}$", RegexOptions.CultureInvariant) || p.UserName == "root")
            throw new ArgumentException("Укажите отдельное имя администратора Linux, например vpsadmin.");
        if (!ProfileValidator.IsValidDomain(p.PanelDomain) || !p.PanelDomain.EndsWith("." + p.DnsZone, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Для панели нужен поддомен выбранной DNS-зоны.");
        if (p.PanelPort is 22 or 80 or 443 or 2096 || p.PanelPort == p.SshPort || p.SshPort is 80 or 443)
            throw new ArgumentException("Порты SSH, панели, HTTP-01 (80) и VPN (443) не должны совпадать; 2096 зарезервирован.");
        if (!Regex.IsMatch(r.AcmeEmail, @"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant) || r.AcmeEmail.Length > 254)
            throw new ArgumentException("Укажите email для аккаунта Let’s Encrypt.");
        if (!Enum.IsDefined(r.PanelAccess) || !PanelSourcesValid(r.PanelCidr))
            throw new ArgumentException("Укажите корректный режим панели и CIDR её публичного доступа.");
        p = p with
        {
            PanelAccess = r.PanelAccess,
            PanelAllowedIpsText = r.PanelAccess == VpsPanelAccess.Both ? PanelSourcesForProfile(r.PanelCidr) : ""
        };
        if (r.HostKey.KeyType != "ssh-ed25519") throw new ArgumentException("Для первичной настройки подтвердите Ed25519 host key сервера.");
        try
        {
            var key = Convert.FromBase64String(r.HostKey.PublicKey);
            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
            if (fingerprint != r.HostKey.Fingerprint) throw new ArgumentException("Fingerprint ключа сервера не совпадает.");
        }
        catch (FormatException) { throw new ArgumentException("Некорректный публичный ключ SSH-сервера."); }
        return p;
    }
    private static bool PanelSourcesValid(string sources)
    {
        if (sources == "any") return true;
        if (string.IsNullOrEmpty(sources) || sources.Length > 6400) return false;
        var lines = sources.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return lines.Length is >= 1 and <= 100 && lines.All(line =>
            line.Length > 0 && line == line.Trim() && !line.Contains('%') &&
            (line.Contains('/') ? IPNetwork.TryParse(line, out _) : IPAddress.TryParse(line, out _)));
    }
    private static string PanelSourcesForProfile(string sources)
    {
        if (sources == "any") return "";
        return string.Join('\n', sources.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => { var slash = line.IndexOf('/'); return slash < 0 ? line : line[..slash]; }));
    }
    private static void ValidateInitialAccess(VpsSshAccess access)
    {
        if (!Regex.IsMatch(access.UserName, "^[A-Za-z_][A-Za-z0-9_.-]{0,31}$", RegexOptions.CultureInvariant) || !Enum.IsDefined(access.Method))
            throw new SetupException("Некорректный способ первоначального SSH-входа.");
        if (access.Method == VpsAuthMethod.Password && string.IsNullOrEmpty(access.Password)) throw new SetupException("Введите первоначальный пароль провайдера либо выберите SSH-ключ.");
        if (access.Method == VpsAuthMethod.PrivateKey && (string.IsNullOrWhiteSpace(access.IdentityFile) || !Path.IsPathFullyQualified(access.IdentityFile)))
            throw new SetupException("Выберите существующий локальный SSH-ключ первоначального входа.");
    }
    private static string HelperDirectory(Guid id) => "/var/lib/openviya-desktop/" + id.ToString("N");
    private static bool IsFreshPhase(string phase) => phase is "" or "fresh" or "unprepared" or "none";
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static JsonDocument MachineDocument(string output)
    {
        if (output.Length > 32 * 1024) throw new SetupException("Машинный ответ VPS превышает допустимый размер.");
        try
        {
            var doc = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 8 });
            if (Number(doc.RootElement, "schemaVersion") != 1) { doc.Dispose(); throw new SetupException("Версия машинного протокола VPS не поддерживается."); }
            return doc;
        }
        catch (JsonException) { throw new SetupException("VPS вернул некорректный машинный ответ. Сырой вывод не сохраняется."); }
    }
    private static string Text(JsonElement r, string name) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static string RequiredText(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new SetupException("Машинный ответ VPS неполон.");
    private static bool RequiredBoolean(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : throw new SetupException("Машинный ответ VPS неполон.");
    private static int Number(JsonElement r, string name) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : int.MinValue;
    private static string? RemoteStepLabel(string step) => step switch
    {
        "startup" => "Запуск установки", "validate" => "Проверка параметров",
        "apt-index" => "Обновление списка пакетов", "apt-install" => "Установка системных пакетов",
        "validate-access" => "Проверка SSH и существующих правил firewall", "apt-upgrade" => "Обновление пакетов ОС",
        "admin-create" => "Создание администратора и установка публичного ключа", "rollback" => "Включение таймера восстановления доступа",
        "firewall" => "Настройка UFW и открытие портов", "prepare-done" => "Подготовка доступа завершена",
        "ssh-hardening" => "Защита SSH и отключение входа root", "fail2ban" => "Настройка fail2ban",
        "security-updates" => "Включение автоматических обновлений безопасности", "log-limits" => "Ограничение журналов и настройка сети",
        "tls-check" => "Проверка готовности HTTPS", "tls-issue" => "Выпуск сертификата Let’s Encrypt через TCP 80",
        "tls-deploy" => "Установка сертификата и настройка продления", "panel-download" => "Загрузка 3x-ui",
        "panel-verify" => "Проверка контрольной суммы и архива 3x-ui", "panel-configure" => "Настройка 3x-ui и учётной записи панели",
        "panel-start" => "Запуск 3x-ui", "backup" => "Настройка резервных копий", "health" => "Проверка панели, Xray и firewall",
        "finalize-done" => "Установка и проверка VPS завершены", _ => null
    };
    private static string RemoteRecovery(string step) => step switch
    {
        "tls-check" or "tls-issue" => "Проверьте A/AAAA домена панели и доступность TCP 80 в firewall провайдера. Затем проверьте журнал задания и таймер восстановления на VPS.",
        "apt-index" or "apt-install" or "apt-upgrade" or "security-updates" => "Проверьте доступность репозиториев, состояние APT и журнал задания на VPS. Не запускайте подготовку повторно без проверки сохранённого состояния.",
        _ => "Проверьте журнал задания и таймер восстановления через SSH или консоль провайдера. Повторная установка автоматически не запускается."
    };
    private static string JobFailureMessage(Job job, string stage) => "VPS остановился на шаге «" +
        (RemoteStepLabel(job.Step) ?? (stage == "prepare" ? "Подготовка доступа" : "Установка панели")) + "»." +
        (job.ExitCode >= 0 ? " Код завершения: " + job.ExitCode.ToString(CultureInfo.InvariantCulture) + "." : " Состояние задания не подтверждено.") + " " + RemoteRecovery(job.Step);
    private sealed record Job(string State, int ExitCode, string Phase, string Step);
    private sealed record VpsProvisionJobProgress(string Message, string StepId, VpsProvisionRunState State, DateTimeOffset ContactAt, DateTimeOffset NextPollAt);
    private sealed class SetupException(string message) : Exception(message);
}
