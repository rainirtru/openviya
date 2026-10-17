using OpenViya.Ops.Core;
using System.IO;
using System.Text.RegularExpressions;

namespace OpenViya.Ops.Desktop;

internal sealed record VpsConnectionPreflightResult(bool Success, string Message, string? Os = null, string? Version = null, string? Architecture = null, bool IsRoot = false, bool SftpReady = false, bool Supported = false, bool Systemd = false, bool HasRootAccess = false);

internal interface IVpsConnectionPreflightService
{
    Task<VpsConnectionPreflightResult> CheckAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default);
}

/// <summary>Performs bounded, read-only SSH/SFTP and OS checks using the already pinned host key.</summary>
internal sealed class VpsConnectionPreflightService : IVpsConnectionPreflightService
{
    private readonly IVpsRemoteSessionFactory _sessions;
    private readonly TimeSpan _timeout;

    public VpsConnectionPreflightService(IVpsRemoteSessionFactory sessions, TimeSpan? timeout = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<VpsConnectionPreflightResult> CheckAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(hostKey);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(profile, access, hostKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await using var session = await _sessions.ConnectAsync(profile, access, hostKey, timeout.Token).ConfigureAwait(false);
            // Fixed command only: no user-provided text is interpolated, and every operation is read-only.
            var result = await session.ExecuteAsync("printf 'OVPREFLIGHT\\n'; id -u; sudo -n id -u 2>/dev/null || printf 'sudo=no\\n'; uname -srm; cat /etc/os-release; test -d /run/systemd/system && printf 'systemd=yes\\n' || printf 'systemd=no\\n'", timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) return new(false, "SSH и SFTP установлены, но чтение ОС завершилось ошибкой.", SftpReady: true);
            var parsed = ParseOutput(result.Output);
            if (!parsed.HasRootAccess) return parsed with { Success = false, Message = "SSH и SFTP работают, но пользователь не имеет root-доступа и sudo без пароля." };
            if (!parsed.Supported) return parsed with { Success = false, Message = $"SSH и SFTP работают; обнаружена ОС {parsed.Os} {parsed.Version}, не поддерживаемая установщиком." };
            if (!parsed.Systemd) return parsed with { Success = false, Message = "SSH и SFTP работают, но каталог runtime systemd не обнаружен." };
            return parsed with { Success = true, Message = $"SSH и SFTP проверены. {(parsed.IsRoot ? "root-вход" : "sudo без пароля")} подтверждён; {parsed.Os} {parsed.Version}, {parsed.Architecture}. Проверка не оценивает DNS, firewall или работу VPN." };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, $"Проверка SSH/SFTP превысила лимит {_timeout.TotalSeconds:0} секунд."); }
        catch (VpsKeyPassphraseRequiredException ex) { return new(false, ex.Message); }
        catch (Exception) { return new(false, "Не удалось пройти SSH/SFTP-проверку с принятым fingerprint. Проверьте адрес, порт и способ входа."); }
    }

    private static void Validate(ServerProfile profile, VpsSshAccess access, HostKeyCandidate key)
    {
        if (!ProfileValidator.IsPublicIpLiteral(profile.Host) || profile.SshPort is < 1 or > 65535)
            throw new ArgumentException("Укажите публичный IP-адрес и корректный SSH-порт.");
        if (!Regex.IsMatch(access.UserName, @"^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Имя начального SSH-пользователя указано неверно.");
        if (key.KeyType != "ssh-ed25519" || string.IsNullOrWhiteSpace(key.PublicKey) || string.IsNullOrWhiteSpace(key.Fingerprint))
            throw new ArgumentException("Сначала независимо подтвердите fingerprint ssh-ed25519.");
        if (access.Method == VpsAuthMethod.Password && string.IsNullOrEmpty(access.Password))
            throw new ArgumentException("Введите начальный SSH-пароль.");
        if (access.Method == VpsAuthMethod.PrivateKey && (string.IsNullOrWhiteSpace(access.IdentityFile) || !File.Exists(access.IdentityFile)))
            throw new ArgumentException("Выберите существующий начальный приватный ключ.");
    }

    private static VpsConnectionPreflightResult ParseOutput(string output)
    {
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 5 || lines[0] != "OVPREFLIGHT") return new(false, "Ответ сервера не соответствует формату проверки.", SftpReady: true);
        var root = lines[1].Trim() == "0";
        var sudo = lines[2].Trim() == "0";
        var uname = lines[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var osId = lines.FirstOrDefault(x => x.StartsWith("ID=", StringComparison.Ordinal))?[3..].Trim('"');
        var version = lines.FirstOrDefault(x => x.StartsWith("VERSION_ID=", StringComparison.Ordinal))?[11..].Trim('"');
        var arch = uname.Length >= 3 ? uname[^1] : "unknown";
        var supported = ((osId == "ubuntu" && (version == "24.04" || version == "26.04")) || (osId == "debian" && (version == "12" || version == "13")))
            && arch is "x86_64" or "aarch64" or "arm64";
        var systemd = lines.Any(x => x == "systemd=yes");
        return new VpsConnectionPreflightResult(false, "", osId ?? "unknown", version ?? "unknown", arch, root, true, supported, systemd, root || sudo) with
        {
            Success = root && supported && systemd,
            Message = systemd ? "" : "SSH и SFTP работают, но systemd не обнаружен.",
            Os = osId ?? "unknown",
            Version = version ?? "unknown"
        };
    }
}
