using System.Collections.Concurrent;
using System.Diagnostics;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Ssh;

/// <summary>Owns only SSH tunnel processes started by this instance.</summary>
public sealed class TunnelManager : ITunnelManager
{
    private readonly SshService _ssh;
    private readonly ConcurrentDictionary<Guid, Run> _runs = new();
    private readonly ConcurrentDictionary<Guid, TunnelSnapshot> _snapshots = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _eventGate = new();
    private bool _disposed;

    public TunnelManager(SshService ssh) => _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));

    public event EventHandler<TunnelSnapshot>? Changed;
    public IReadOnlyList<TunnelSnapshot> Snapshots => _snapshots.Values.OrderBy(x => x.ServerId).ToArray();

    public async Task<TunnelSnapshot> StartAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SshService.ValidateEndpoint(server);
        SshService.ValidatePort(server.PanelPort, nameof(server.PanelPort));
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.TryGetValue(server.Id, out var existing))
            {
                if (!existing.Process.HasExited) return existing.Snapshot;
                Publish(existing, TunnelState.Failed, "SSH процесс завершился. Проверьте host key, SSH доступ и remote panel target.");
                _runs.TryRemove(server.Id, out _); existing.Dispose();
            }

            var runId = Guid.NewGuid();
            Publish(server.Id, runId, null, TunnelState.Starting, "Запускаем SSH forwarding.");
            int? requested = server.LocalPort;
            if (requested is not null) SshService.ValidatePort(requested.Value, nameof(server.LocalPort));
            var ports = CandidatePorts(requested).ToArray();
            Exception? lastError = null;
            foreach (var port in ports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (WindowsJob.IsLoopbackPortInUse(port))
                    {
                        lastError = new IOException("Loopback port already has a listener.");
                        if (requested is null) continue;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break; // Fail closed if Windows cannot report listener ownership.
                }
                Process process;
                try { process = await _ssh.StartTunnelProcessAsync(server, port, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { Publish(server.Id, runId, port, TunnelState.Stopped, "Запуск отменён."); throw; }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ArgumentException)
                {
                    lastError = ex;
                    break;
                }
                var run = new Run(server.Id, runId, port, process, this);
                if (!_runs.TryAdd(server.Id, run)) { run.Dispose(); throw new InvalidOperationException("A tunnel run for this server already exists."); }
                try
                {
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                    var listenerOwned = false;
                    var foreignCollision = false;
                    while (DateTime.UtcNow < deadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (run.ExitObserved || process.HasExited) break;
                        if (WindowsJob.OwnsLoopbackListener(port, process.Id)) { listenerOwned = true; break; }
                        if (WindowsJob.HasForeignLoopbackListener(port, process.Id)) { foreignCollision = true; break; }
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                    if (!listenerOwned)
                    {
                        var exited = run.ExitObserved || process.HasExited;
                        if (exited) process.WaitForExit(); // Drain bounded stderr before classifying an address collision.
                        var collision = foreignCollision || exited && SshService.IsAddressCollision(process);
                        if (foreignCollision) lastError = new IOException("Another process owns the requested local listener.");
                        else if (exited) lastError = new IOException($"OpenSSH exited with code {process.ExitCode}.");
                        else lastError = new TimeoutException("The SSH process did not acquire its own local listener.");
                        _runs.TryRemove(server.Id, out _); run.Dispose();
                        if (requested is null && collision) continue;
                        break;
                    }
                    // Confirm the loopback listener belongs to this process before reporting Active.
                    var active = new TunnelSnapshot(server.Id, runId, port, TunnelState.Active,
                        $"Локальный forwarding 127.0.0.1:{port} запущен к 127.0.0.1:{server.PanelPort} на сервере. Это подтверждает bind, но не TLS и не доступность панели; проверьте целевой адрес отдельно.", DateTimeOffset.UtcNow, process.Id);
                    if (!run.TryMarkActive(active))
                    {
                        _runs.TryRemove(server.Id, out _); run.Dispose();
                        lastError = new IOException("SSH process ended during tunnel startup.");
                        break;
                    }
                    _snapshots[server.Id] = active; RaiseChanged(active);
                    return active;
                }
                catch (OperationCanceledException)
                {
                    _runs.TryRemove(server.Id, out _); run.Dispose();
                    Publish(server.Id, runId, port, TunnelState.Stopped, "Запуск отменён.");
                    throw;
                }
                catch (Exception)
                {
                    _runs.TryRemove(server.Id, out _); run.Dispose();
                    return Publish(server.Id, runId, port, TunnelState.Failed, "Не удалось подтвердить локальный listener, принадлежащий SSH процессу. Туннель остановлен.");
                }
            }
            var message = requested is not null
                ? "Не удалось открыть выбранный локальный порт. Проверьте занятый порт, host key и доступ к SSH."
                : "Не удалось открыть локальный порт в диапазоне 20530–20629. Проверьте занятость портов и SSH доступ.";
            var failed = Publish(server.Id, runId, requested, TunnelState.Failed, message);
            _ = lastError; // Deliberately do not expose paths, command line, or raw process diagnostics.
            return failed;
        }
        finally { _startGate.Release(); }
    }

    public async Task StopAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_runs.TryRemove(serverId, out var run)) return;
            Publish(run.ServerId, run.RunId, run.Port, TunnelState.Stopping, "Останавливаем принадлежащий приложению SSH процесс.", run.Process.Id);
            run.Dispose();
            Publish(run.ServerId, run.RunId, run.Port, TunnelState.Stopped, "Туннель остановлен.");
        }
        finally { _startGate.Release(); }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in _runs.Keys.ToArray()) await StopAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }

    internal static IEnumerable<int> CandidatePorts(int? requested)
    {
        var emitted = new HashSet<int>();
        if (requested is >= 1 and <= 65535 && emitted.Add(requested.Value)) yield return requested.Value;
        for (var p = 20530; p <= 20629; p++) if (emitted.Add(p)) yield return p;
    }

    private TunnelSnapshot Publish(Guid id, Guid runId, int? port, TunnelState state, string message, int? pid = null)
    {
        var snapshot = new TunnelSnapshot(id, runId, port ?? 0, state, message, DateTimeOffset.UtcNow, pid);
        _snapshots[id] = snapshot;
        RaiseChanged(snapshot);
        return snapshot;
    }
    private TunnelSnapshot Publish(Run run, TunnelState state, string message)
    {
        var snapshot = run.Snapshot with { State = state, Message = message, UpdatedAt = DateTimeOffset.UtcNow };
        run.Snapshot = snapshot; _snapshots[run.ServerId] = snapshot; RaiseChanged(snapshot); return snapshot;
    }
    private void RaiseChanged(TunnelSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<TunnelSnapshot> handler in handlers.GetInvocationList())
            try { handler(this, snapshot); } catch { /* UI subscribers cannot break process ownership. */ }
    }

    private void ProcessExited(Run run)
    {
        if (_runs.TryRemove(run.ServerId, out var removed) && ReferenceEquals(removed, run))
        {
            Publish(run, TunnelState.Failed, "SSH туннельный процесс завершился. Проверьте состояние соединения и запустите его снова.");
            run.Dispose();
        }
    }

    private sealed class Run : IDisposable
    {
        private readonly TunnelManager _owner;
        private readonly object _stateGate = new();
        private bool _exitObserved;
        private bool _active;
        internal Guid ServerId { get; }
        internal Guid RunId { get; }
        internal int Port { get; }
        internal Process Process { get; }
        internal TunnelSnapshot Snapshot { get; set; }
        internal bool ExitObserved { get { lock (_stateGate) return _exitObserved; } }
        internal Run(Guid serverId, Guid runId, int port, Process process, TunnelManager owner)
        {
            ServerId = serverId; RunId = runId; Port = port; Process = process; _owner = owner;
            Snapshot = new TunnelSnapshot(serverId, runId, port, TunnelState.Starting, "Ожидаем подтверждение локального bind.", DateTimeOffset.UtcNow, process.Id);
            process.Exited += Exited;
        }
        private void Exited(object? sender, EventArgs e)
        {
            bool active;
            lock (_stateGate) { _exitObserved = true; active = _active; }
            if (active) _owner.ProcessExited(this);
        }
        internal bool TryMarkActive(TunnelSnapshot snapshot)
        {
            lock (_stateGate)
            {
                if (_exitObserved) return false;
                Snapshot = snapshot; _active = true; return true;
            }
        }
        public void Dispose() { try { Process.Kill(true); } catch { } try { Process.Dispose(); } catch { } }
    }
}
