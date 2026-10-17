#!/usr/bin/env python3
"""Fixed-protocol maintenance helper for hosts provisioned by OpenViya VPS bootstrap."""
import datetime as dt
import contextlib
try:
    import fcntl
except ImportError:  # The helper is deployed only to Linux; keep pure tests runnable on Windows.
    fcntl = None
import ipaddress
import json
import os
import pathlib
import re
import shlex
import sqlite3
import subprocess
import sys
import urllib.request

STATE = pathlib.Path('/var/lib/openviya-vps-bootstrap')
CONFIG = STATE / 'config'
BACKUPS = pathlib.Path('/var/backups/3x-ui')
MAINT = pathlib.Path('/var/lib/openviya-desktop-maintenance')
LOCK = pathlib.Path('/run/lock/openviya-maintenance.lock')
DB = pathlib.Path('/etc/x-ui/x-ui.db')
TLS_CERT = pathlib.Path('/etc/x-ui/tls/panel.crt')
VERSION_FILE = STATE / 'version'
LATEST_URL = 'https://api.github.com/repos/MHSanaei/3x-ui/releases/latest'
MAX_REQUEST = 65536


def run(args, timeout=60, check=True, **kwargs):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout, **kwargs)
    if check and result.returncode:
        raise RuntimeError('command failed: ' + pathlib.Path(args[0]).name)
    return result


def safe_root_file(path):
    s = path.lstat()
    if not path.is_file() or path.is_symlink() or s.st_uid != 0 or s.st_mode & 0o022:
        raise RuntimeError('unsafe managed file')


def config_values():
    if STATE.is_symlink() or not STATE.is_dir() or STATE.stat().st_uid != 0 or STATE.stat().st_mode & 0o077:
        raise RuntimeError('managed bootstrap state directory is unsafe')
    safe_root_file(CONFIG)
    safe_root_file(STATE / 'phase')
    if (STATE / 'phase').read_text().strip() != 'complete':
        raise RuntimeError('server is not a completed OpenViya managed installation')
    safe_root_file(DB)
    script = "source \"$1\"; printf '%s\\0' \"$PANEL_PORT\" \"$PANEL_ACCESS\" \"$PANEL_CIDR\" \"$PANEL_DOMAIN\" \"$TLS_MODE\" \"$ACME_EMAIL\" \"$SSH_PORT\" \"$VPN_TCP\" \"$VPN_UDP\""
    raw = run(['/bin/bash', '-c', script, 'openviya-maintenance', str(CONFIG)]).stdout
    fields = raw.split('\0')
    if len(fields) != 10:
        raise RuntimeError('managed configuration is malformed')
    port, access, cidr, domain, tls, email, ssh_port, vpn_tcp, vpn_udp, _ = fields
    if not port.isdecimal() or not 1 <= int(port) <= 65535 or access not in ('tunnel', 'both'):
        raise RuntimeError('managed panel configuration is invalid')
    return {'panelPort': int(port), 'panelAccess': access, 'allowedIpsText': cidr,
            'domain': domain, 'tlsMode': tls, 'acmeEmail': email,
            'sshPort': int(ssh_port), 'vpnTcp': vpn_tcp, 'vpnUdp': vpn_udp}


def latest_version():
    req = urllib.request.Request(LATEST_URL, headers={'Accept': 'application/vnd.github+json', 'User-Agent': 'OpenViya-Ops'})
    with urllib.request.urlopen(req, timeout=12) as response:
        data = json.loads(response.read(131073))
    tag = data.get('tag_name')
    if not isinstance(tag, str) or not re.fullmatch(r'v?[0-9]+(?:\.[0-9]+){1,3}', tag):
        raise RuntimeError('official release response did not contain a valid version')
    return tag.lstrip('v')


def rollback_pending():
    result = run(['systemctl', 'list-units', '--type=timer', '--state=active', '--plain', '--no-legend',
                  'openviya-maintenance-*.timer'], check=False)
    return result.returncode == 0 and any(line.split() and line.split()[0].startswith('openviya-maintenance-') for line in result.stdout.splitlines())


@contextlib.contextmanager
def maintenance_lock():
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_CREAT | os.O_RDWR | getattr(os, 'O_NOFOLLOW', 0)
    descriptor = os.open(LOCK, flags, 0o600)
    try:
        info = os.fstat(descriptor)
        if fcntl is not None and (info.st_uid != 0 or info.st_mode & 0o077):
            raise RuntimeError('maintenance lock file is unsafe')
        if fcntl is not None:
            try:
                fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise RuntimeError('another maintenance action is running')
        with os.fdopen(descriptor, 'r+') as stream:
            descriptor = -1
            yield
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def cert_expiry():
    if not TLS_CERT.exists() or TLS_CERT.is_symlink():
        return None
    result = run(['openssl', 'x509', '-enddate', '-noout', '-in', str(TLS_CERT)], check=False)
    if result.returncode:
        return None
    value = result.stdout.strip().partition('=')[2]
    try:
        return dt.datetime.strptime(value, '%b %d %H:%M:%S %Y %Z').replace(tzinfo=dt.timezone.utc).isoformat()
    except ValueError:
        return None


def backups():
    result = []
    if not BACKUPS.exists():
        return result
    if BACKUPS.is_symlink() or BACKUPS.stat().st_uid != 0:
        raise RuntimeError('backup directory is unsafe')
    for path in sorted(BACKUPS.glob('backup-*.tar.gz'), key=lambda p: p.name, reverse=True)[:50]:
        if path.is_symlink() or not path.is_file() or path.stat().st_uid != 0:
            continue
        st = path.stat()
        result.append({'name': path.name, 'sizeBytes': st.st_size,
                       'createdAt': dt.datetime.fromtimestamp(st.st_mtime, dt.timezone.utc).isoformat()})
    return result


def panel_healthy():
    cfg = config_values()
    settings = panel_db_settings()
    domain = cfg['domain']
    base = settings.get('webBasePath', '/')
    if not re.fullmatch(r'[A-Za-z0-9.-]{1,253}', domain) or not base.startswith('/') or any(c in base for c in '\r\n\x00'):
        return False
    resolve = f'{domain}:{cfg["panelPort"]}:127.0.0.1'
    url = f'https://{domain}:{cfg["panelPort"]}{base}'
    result = run(['curl', '--silent', '--show-error', '--noproxy', '*', '--max-time', '7', '--output', '/dev/null',
                  '--write-out', '%{http_code}', '--cacert', str(TLS_CERT), '--resolve', resolve, url], check=False)
    return result.returncode == 0 and result.stdout.isdecimal() and int(result.stdout) in (200, 301, 302, 303, 307, 308, 401, 403)


def panel_db_settings():
    connection = sqlite3.connect(f'file:{DB}?mode=ro', uri=True, timeout=5)
    try:
        columns = {row[1] for row in connection.execute('PRAGMA table_info(settings)')}
        if not {'key', 'value'} <= columns:
            raise RuntimeError('3x-ui settings schema is unsupported')
        settings = dict(connection.execute("SELECT key,value FROM settings WHERE key IN ('webPort','webListen','webBasePath')"))
        if not str(settings.get('webPort', '')).isdecimal():
            raise RuntimeError('3x-ui panel port is not readable')
        return {'webPort': int(settings['webPort']), 'webListen': settings.get('webListen', ''), 'webBasePath': settings.get('webBasePath', '/')}
    finally:
        connection.close()


def set_panel_db(port, access):
    listen = '0.0.0.0' if access == 'both' else '127.0.0.1'
    connection = sqlite3.connect(DB, timeout=10)
    try:
        columns = {row[1] for row in connection.execute('PRAGMA table_info(settings)')}
        if not {'key', 'value'} <= columns:
            raise RuntimeError('3x-ui settings schema is unsupported')
        for key, value in [('webPort', str(port)), ('webListen', listen)]:
            connection.execute('DELETE FROM settings WHERE key=?', (key,))
            connection.execute('INSERT INTO settings (key,value) VALUES (?,?)', (key, value))
        connection.commit()
        actual = dict(connection.execute("SELECT key,value FROM settings WHERE key IN ('webPort','webListen')"))
        if actual.get('webPort') != str(port) or actual.get('webListen') != listen:
            raise RuntimeError('3x-ui setting readback failed')
    finally:
        connection.close()


def inspect():
    cfg = config_values()
    db_settings = panel_db_settings()
    if db_settings['webPort'] != cfg['panelPort']:
        raise RuntimeError('saved panel port disagrees with the live 3x-ui database; manual repair is required')
    installed = VERSION_FILE.read_text().strip() if VERSION_FILE.exists() and not VERSION_FILE.is_symlink() else 'unknown'
    latest = 'unknown'
    ufw = run(['ufw', 'status'], check=False)
    active = ufw.returncode == 0 and ufw.stdout.startswith('Status: active')
    cert = cert_expiry()
    rollback = rollback_pending()
    return {'installedVersion': installed, 'latestVersion': latest,
            'updateAvailable': installed != 'unknown' and installed.lstrip('v') != latest,
            **cfg, 'certificateExpiresAt': cert, 'certificateManagedByLetsEncrypt': cfg['tlsMode'] == 'letsencrypt',
            'panelHealthy': panel_healthy(), 'firewallActive': active,
            'panelFirewallRulesMatch': panel_rules_match(cfg['panelPort'], cfg['panelAccess'], cfg['allowedIpsText']),
            'rollbackPending': rollback,
            'backups': backups()}


def validate_cidr(text):
    if text == 'any':
        return 'any'
    lines = text.split('\n')
    if not 1 <= len(lines) <= 100:
        raise ValueError('allowed IP list must contain between 1 and 100 entries')
    normalized = []
    for line in lines:
        if not line or line != line.strip() or '%' in line:
            raise ValueError('allowed IP list contains an invalid line')
        normalized.append(str(ipaddress.ip_network(line, strict=True)))
    return '\n'.join(dict.fromkeys(normalized))


def validate_request(req):
    if not isinstance(req, dict):
        raise ValueError('request must be an object')
    action = req.get('action')
    if action not in ('inspect', 'access', 'cert-check', 'cert-renew', 'backup-create', 'backup-list', 'backup-read', 'update-check'):
        raise ValueError('unsupported action')
    if action == 'access':
        cfg = config_values()
        port = req.get('panelPort')
        if type(port) is not int or not 1 <= port <= 65535 or port in (80, 2096) or port == cfg['sshPort']:
            raise ValueError('panel port is invalid or reserved')
        reserved = set()
        for value in (cfg['vpnTcp'], cfg['vpnUdp']):
            for item in value.split(',') if value else []:
                if item.isdecimal(): reserved.add(int(item))
        if port in reserved:
            raise ValueError('panel port conflicts with VPN')
        if req.get('panelAccess') not in ('tunnel', 'both'):
            raise ValueError('panel access mode is invalid')
        req['allowedIpsText'] = validate_cidr(req.get('allowedIpsText', ''))
    if action == 'backup-read':
        if not isinstance(req.get('name'), str) or not re.fullmatch(r'backup-[0-9]{8}T[0-9]{6}-[0-9]+\.tar\.gz', req['name']):
            raise ValueError('backup name is invalid')
        if type(req.get('offset')) is not int or not 0 <= req['offset'] <= 2**31:
            raise ValueError('backup offset is invalid')
    return action


def write_config(new_port, new_access, new_cidr):
    # The original config is generated by the installed bootstrap using printf %q and is root-owned mode 0600.
    args = [
        '/bin/bash', '-c',
        'source "$1"; PANEL_PORT=$2; PANEL_ACCESS=$3; PANEL_CIDR=$4; '
        'for n in ADMIN SSH_PORT SSH_CIDR PANEL_PORT PANEL_ACCESS PANEL_CIDR TLS_MODE PANEL_DOMAIN ACME_EMAIL VPN_TCP VPN_UDP PREPARE_CONNECTION CLIENT_IP; do printf "%s=%q\\n" "$n" "${!n}"; done > "$1.new"; '
        'chmod 600 "$1.new"; mv -f -- "$1.new" "$1"',
        'openviya-maintenance', str(CONFIG), str(new_port), new_access, new_cidr]
    run(args)


def remove_panel_rules(port, access, cidr):
    if access != 'both':
        return
    commands = []
    added = run(['ufw', 'show', 'added'])
    added_rules = actual_panel_rules(added.stdout)
    if cidr == 'any':
        if ('public', int(port), 'tcp', '3x-ui public HTTPS') in added_rules:
            commands.append(['ufw', '--force', 'delete', 'allow', f'{port}/tcp'])
    else:
        for source in cidr.split('\n'):
            network = str(ipaddress.ip_network(source, strict=True))
            if ('source', network, int(port), 'tcp', '3x-ui HTTPS admin network') in added_rules:
                commands.append(['ufw', '--force', 'delete', 'allow', 'from', source, 'to', 'any', 'port', str(port), 'proto', 'tcp'])
    for cmd in commands:
        run(cmd)


def add_panel_rules(port, access, cidr):
    if access != 'both':
        return
    if cidr == 'any':
        run(['ufw', 'allow', f'{port}/tcp', 'comment', '3x-ui public HTTPS'])
    else:
        for source in cidr.split('\n'):
            run(['ufw', 'allow', 'from', source, 'to', 'any', 'port', str(port), 'proto', 'tcp', 'comment', '3x-ui HTTPS admin network'])


def panel_rules_match(port, access, cidr):
    added = run(['ufw', 'show', 'added'], check=False)
    if added.returncode:
        return False
    return panel_rules_match_from_output(added.stdout, port, access, cidr)


def panel_rules_match_from_output(text, port, access, cidr):
    expected = set()
    if access == 'both':
        if cidr == 'any':
            expected.add(('public', int(port), 'tcp', '3x-ui public HTTPS'))
        else:
            for source in cidr.split('\n'):
                expected.add(('source', str(ipaddress.ip_network(source, strict=True)), int(port), 'tcp', '3x-ui HTTPS admin network'))
    actual = actual_panel_rules(text)
    return actual == expected


def actual_panel_rules(text):
    result = set()
    for line in text.splitlines():
        try:
            parts = shlex.split(line)
            if len(parts) < 5 or parts[0:2] != ['ufw', 'allow'] or 'comment' not in parts:
                continue
            comment_at = parts.index('comment')
            comment = ' '.join(parts[comment_at + 1:])
            if comment == '3x-ui public HTTPS' and len(parts) > 2 and re.fullmatch(r'[0-9]{1,5}/tcp', parts[2]):
                result.add(('public', int(parts[2].split('/')[0]), 'tcp', comment))
            elif comment == '3x-ui HTTPS admin network' and len(parts) > 9 and parts[2] == 'from':
                source = str(ipaddress.ip_network(parts[3], strict=False))
                if parts[4:7] != ['to', 'any', 'port'] or parts[8] != 'proto':
                    continue
                port = parts[7]
                proto = parts[9]
                if port.isdecimal() and proto == 'tcp':
                    result.add(('source', source, int(port), proto, comment))
        except (ValueError, IndexError):
            continue
    return result


def apply_access(req):
    with maintenance_lock():
        return _apply_access_locked(req, config_values())


def _apply_access_locked(req, cfg):
    if 'expectedPanelPort' in req and (req['expectedPanelPort'] != cfg['panelPort'] or
            req.get('expectedPanelAccess') != cfg['panelAccess'] or
            req.get('expectedAllowedIpsText') != validate_cidr(cfg['allowedIpsText'])):
        raise RuntimeError('panel settings changed after plan verification')
    if rollback_pending():
        raise RuntimeError('another panel access change still has an armed rollback timer')
    old_db = panel_db_settings()
    rollback_id = req['operationId']
    if not re.fullmatch(r'[0-9a-f]{32}', rollback_id):
        raise ValueError('operation ID is invalid')
    snapshot = {'old': cfg, 'oldDb': old_db, 'new': {'panelPort': req['panelPort'], 'panelAccess': req['panelAccess'], 'allowedIpsText': req['allowedIpsText']}}
    if MAINT.exists():
        if MAINT.is_symlink() or MAINT.stat().st_uid != 0 or MAINT.stat().st_mode & 0o077:
            raise RuntimeError('maintenance state directory is unsafe')
    else:
        MAINT.mkdir(mode=0o700, parents=True)
    state_path = MAINT / f'{rollback_id}.json'
    state_path.write_text(json.dumps(snapshot, separators=(',', ':')))
    os.chmod(state_path, 0o600)
    run(['/usr/local/sbin/openviya-xui-backup'], timeout=300)
    timer = f'openviya-maintenance-{rollback_id}'
    script = pathlib.Path(__file__).resolve()
    run(['/usr/bin/systemd-run', '--quiet', '--unit=' + timer, '--on-active=5m', '--property=Type=oneshot',
         '/usr/bin/python3', str(script), 'rollback', rollback_id])
    try:
        remove_panel_rules(cfg['panelPort'], cfg['panelAccess'], cfg['allowedIpsText'])
        write_config(req['panelPort'], req['panelAccess'], req['allowedIpsText'])
        set_panel_db(req['panelPort'], req['panelAccess'])
        add_panel_rules(req['panelPort'], req['panelAccess'], req['allowedIpsText'])
        run(['systemctl', 'restart', 'x-ui'], timeout=60)
    except Exception:
        rollback(rollback_id)
        raise
    if not panel_healthy():
        rollback(rollback_id)
        raise RuntimeError('panel health check failed; old settings restored')
    if not panel_rules_match(req['panelPort'], req['panelAccess'], req['allowedIpsText']):
        rollback(rollback_id)
        raise RuntimeError('panel firewall rules did not match the requested access; old settings restored')
    return {'operationId': rollback_id, 'rollbackTimer': timer + '.timer', 'health': 'passed'}


def rollback(operation_id):
    if not re.fullmatch(r'[0-9a-f]{32}', operation_id):
        raise ValueError('operation ID is invalid')
    state_path = MAINT / f'{operation_id}.json'
    safe_root_file(state_path)
    state = json.loads(state_path.read_text())
    old, new = state['old'], state['new']
    remove_panel_rules(new['panelPort'], new['panelAccess'], new['allowedIpsText'])
    write_config(old['panelPort'], old['panelAccess'], old['allowedIpsText'])
    set_panel_db(state['oldDb']['webPort'], old['panelAccess'])
    add_panel_rules(old['panelPort'], old['panelAccess'], old['allowedIpsText'])
    run(['systemctl', 'restart', 'x-ui'], timeout=60)
    run(['systemctl', 'disable', '--now', f'openviya-maintenance-{operation_id}.timer'], check=False)
    return {'restored': True}


def confirm(operation_id):
    if not re.fullmatch(r'[0-9a-f]{32}', operation_id) or not panel_healthy():
        raise RuntimeError('confirmation requires a healthy panel')
    run(['systemctl', 'disable', '--now', f'openviya-maintenance-{operation_id}.timer'], check=False)
    (MAINT / f'{operation_id}.json').unlink(missing_ok=True)
    return {'confirmed': True}


def dispatch(req):
    action = validate_request(req)
    if action == 'inspect':
        return inspect()
    if action == 'update-check':
        result = inspect()
        latest = latest_version()
        result['latestVersion'] = latest
        result['updateAvailable'] = result['installedVersion'] != 'unknown' and result['installedVersion'].lstrip('v') != latest
        return result
    if action == 'access':
        return apply_access(req)
    if action == 'cert-check':
        return {'certificateExpiresAt': cert_expiry(), 'managed': config_values()['tlsMode'] == 'letsencrypt'}
    if action == 'cert-renew':
        cfg = config_values()
        if cfg['tlsMode'] != 'letsencrypt':
            raise RuntimeError('certificate is not managed by Let’s Encrypt')
        run(['/usr/local/sbin/openviya-vps', 'renew-cert'], timeout=300)
        return {'certificateExpiresAt': cert_expiry()}
    if action == 'backup-create':
        run(['/usr/local/sbin/openviya-xui-backup'], timeout=300)
        return {'backups': backups()}
    if action == 'backup-list':
        return {'backups': backups()}
    if action == 'backup-read':
        path = BACKUPS / req['name']
        safe_root_file(path)
        size = path.stat().st_size
        if size > 128 * 1024 * 1024:
            raise RuntimeError('backup exceeds the 128 MiB download limit')
        with path.open('rb') as stream:
            stream.seek(req['offset'])
            chunk = stream.read(60 * 1024)
        return {'offset': req['offset'], 'totalBytes': size, 'dataBase64': __import__('base64').b64encode(chunk).decode('ascii')}
    raise ValueError('unsupported action')


def main():
    if len(sys.argv) == 3 and sys.argv[1] in ('rollback', 'confirm'):
        result = rollback(sys.argv[2]) if sys.argv[1] == 'rollback' else confirm(sys.argv[2])
    elif len(sys.argv) == 2 and sys.argv[1] == 'run':
        raw = sys.stdin.buffer.read(MAX_REQUEST + 1)
        if len(raw) > MAX_REQUEST:
            raise ValueError('request is too large')
        result = dispatch(json.loads(raw))
    else:
        raise ValueError('invalid helper invocation')
    print(json.dumps({'ok': True, 'result': result}, separators=(',', ':')))


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(json.dumps({'ok': False, 'error': str(exc)[:300]}, separators=(',', ':')))
        raise SystemExit(1)
