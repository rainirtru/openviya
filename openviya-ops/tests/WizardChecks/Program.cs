using OpenViya.Ops.Core;
using OpenViya.Ops.Desktop;

var checks = new (string Name, Func<Task> Run)[]
{
    ("root and supported OS preflight succeeds read-only", RootSuccess),
    ("passwordless sudo supports resumed admin identity", SudoSuccess),
    ("non-root without sudo fails", NoRoot),
    ("unsupported OS fails", UnsupportedOs),
    ("timeout is bounded", TimeoutIsBounded),
    ("pre-cancelled check performs no connect", PreCancelled),
    ("step model requires trust and check", StepModel),
    ("progress projection shows honest stage and DNS states", ProgressProjection)
};
foreach (var check in checks)
{
    await check.Run();
    Console.WriteLine($"PASS {check.Name}");
}

static async Task RootSuccess()
{
    var factory = new FakeFactory(Output("0", "sudo=no", "ubuntu", "24.04", "yes"));
    var result = await Service(factory).CheckAsync(Profile(), Access(), Key());
    Require(result.Success && result.IsRoot && result.SftpReady && result.Supported && result.Systemd, result.Message);
    var session = factory.Session!;
    Require(factory.ConnectCount == 1 && session.Command.Contains("cat /etc/os-release", StringComparison.Ordinal), "preflight did not run its fixed OS read");
    Require(!session.Command.Contains("apt", StringComparison.OrdinalIgnoreCase) && !session.Command.Contains("systemctl enable", StringComparison.OrdinalIgnoreCase), "preflight command appears mutating");
    Require(session.UploadCount == 0, "preflight must not upload files");
}

static async Task SudoSuccess()
{
    var result = await Service(new FakeFactory(Output("1000", "0", "debian", "12", "yes"))).CheckAsync(Profile(), Access(), Key());
    Require(result.Success && !result.IsRoot && result.HasRootAccess && result.Message.Contains("sudo без пароля", StringComparison.Ordinal), result.Message);
}

static async Task NoRoot()
{
    var result = await Service(new FakeFactory(Output("1000", "sudo=no", "debian", "13", "yes"))).CheckAsync(Profile(), Access(), Key());
    Require(!result.Success && !result.HasRootAccess, "non-root account without sudo was accepted");
}

static async Task UnsupportedOs()
{
    var result = await Service(new FakeFactory(Output("0", "sudo=no", "ubuntu", "20.04", "yes"))).CheckAsync(Profile(), Access(), Key());
    Require(!result.Success && !result.Supported && result.Message.Contains("не поддерживаем", StringComparison.Ordinal), result.Message);
}

static async Task TimeoutIsBounded()
{
    var factory = new FakeFactory(Output("0", "sudo=no", "ubuntu", "24.04", "yes"), delayConnect: true);
    var service = new VpsConnectionPreflightService(factory, TimeSpan.FromMilliseconds(40));
    var before = DateTime.UtcNow;
    var result = await service.CheckAsync(Profile(), Access(), Key());
    Require(!result.Success && result.Message.Contains("лимит", StringComparison.Ordinal), result.Message);
    Require(DateTime.UtcNow - before < TimeSpan.FromSeconds(2), "timeout did not bound a stalled connect");
}

static async Task PreCancelled()
{
    var factory = new FakeFactory(Output("0", "sudo=no", "ubuntu", "24.04", "yes"));
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await Service(factory).CheckAsync(Profile(), Access(), Key(), cts.Token); }
    catch (OperationCanceledException) { Require(factory.ConnectCount == 0, "pre-cancelled check connected"); return; }
    throw new InvalidOperationException("expected cancellation");
}

static Task StepModel()
{
    var vm = new VpsProvisionWizardViewModel();
    Require(!vm.CanAdvance(0, hostKeyTrusted: true), "step 1 advanced before check");
    vm.SetConnectionResult(true, "ready");
    Require(vm.CanAdvance(0, hostKeyTrusted: true), "step 1 did not advance after pinned preflight");
    vm.InvalidateConnection("changed");
    Require(!vm.CanAdvance(0, hostKeyTrusted: true), "connection change did not invalidate step 1");
    vm.MoveTo(1);
    Require(vm.StepTitle == "2 · Параметры сервера", "step title not updated");
    return Task.CompletedTask;
}

static Task ProgressProjection()
{
    var now = DateTimeOffset.UtcNow;
    var vm = new VpsProgressViewModel();
    var run = new VpsProvisionRun(Guid.NewGuid(), Guid.NewGuid(), VpsProvisionStage.PreparingDns,
        "Проверка публичного DNS.", now)
    {
        ActiveStage = VpsProvisionStage.PreparingDns,
        StepId = "public-dns",
        StepStartedAt = now.AddMinutes(-1).AddSeconds(-5),
        LastSuccessfulVpsContactAt = now.AddSeconds(-12),
        NextPollAt = now.AddSeconds(8),
        State = VpsProvisionRunState.StatusUnknown,
        DnsItems = [
            new VpsProvisionDnsItem("panel.example.org", "A", "203.0.113.9", ProbeStatus.Success),
            new VpsProvisionDnsItem("vpn.example.org", "A", "203.0.113.9", ProbeStatus.Warning)
        ]
    };
    vm.Update(run);
    vm.RefreshClock(now);
    Require(vm.Stages.Count == 6 && vm.Stages[0].State == VpsProvisionStepState.Done &&
        vm.Stages[1].State == VpsProvisionStepState.Running && vm.Stages[2].State == VpsProvisionStepState.Pending,
        "stage checklist must distinguish done, running, and pending");
    Require(vm.CurrentElapsed.Contains("1 мин", StringComparison.Ordinal) && vm.LastContact.Contains("Последний успешный ответ VPS", StringComparison.Ordinal),
        "progress must show elapsed time and label the actual last VPS response");
    Require(vm.NextPoll.Contains("Следующая проверка через", StringComparison.Ordinal) && vm.DnsItems.Count == 2 &&
        vm.DnsItems.Single(x => x.Name == "vpn.example.org").StateText == "Ожидает распространения" &&
        vm.DnsSummary == "Подтверждено 1 из 2 имён", "DNS progress must retain a per-name outcome and scheduled next poll");
    Require(vm.StateText.Contains("временно неизвестно", StringComparison.Ordinal) && !vm.StateText.Contains('%'),
        "unknown remote status must remain explicitly uncertain without an invented percentage");

    vm.Update(run with { Stage = VpsProvisionStage.NeedsAttention, State = VpsProvisionRunState.NeedsOperator, UpdatedAt = now.AddSeconds(1) });
    Require(vm.Stages[1].State == VpsProvisionStepState.NeedsAttention && vm.Stages[2].State == VpsProvisionStepState.Pending,
        "operator recovery state must be attached to the active stage");
    vm.Update(run with { Stage = VpsProvisionStage.Complete, ActiveStage = VpsProvisionStage.Complete, State = VpsProvisionRunState.Complete });
    Require(vm.Stages.All(x => x.State == VpsProvisionStepState.Done), "completed run must mark every stage done");
    return Task.CompletedTask;
}

static VpsConnectionPreflightService Service(FakeFactory factory) => new(factory);
static ServerProfile Profile() => new() { Host = "1.1.1.1", SshPort = 22 };
static VpsSshAccess Access() => new() { UserName = "root", Method = VpsAuthMethod.Password, Password = "fake-only" };
static HostKeyCandidate Key() => new("ssh-ed25519", "fake-public-key", "SHA256:fake");
static string Output(string uid, string sudo, string id, string version, string systemd) =>
    $"OVPREFLIGHT\n{uid}\n{sudo}\nLinux 6.8.0 x86_64\nNAME={id}\nVERSION_ID=\"{version}\"\nID={id}\nID_LIKE=debian\nsystemd={systemd}\n";
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class FakeFactory(string output, bool delayConnect = false) : IVpsRemoteSessionFactory
{
    public int ConnectCount { get; private set; }
    public FakeSession? Session { get; private set; }
    public async Task<IVpsRemoteSession> ConnectAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        if (delayConnect) await Task.Delay(Timeout.Infinite, cancellationToken);
        Session = new FakeSession(output);
        return Session;
    }
}

sealed class FakeSession(string output) : IVpsRemoteSession
{
    public string Command { get; private set; } = "";
    public int UploadCount { get; private set; }
    public Task<VpsRemoteResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        Command = command;
        return Task.FromResult(new VpsRemoteResult(0, output));
    }
    public Task UploadAsync(string remotePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        UploadCount++;
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
