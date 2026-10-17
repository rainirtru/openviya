#!/usr/bin/env bash
# Integration checks in a DISPOSABLE container only. Installs into its /etc.
# Test invalid values in subshells deliberately, preserving outer configuration.
# shellcheck disable=SC2030,SC2031
set -Eeuo pipefail
[[ -f /.dockerenv && ${OPENVIYA_DISPOSABLE_TEST:-} == yes ]] || {
    echo 'Run only in a disposable Docker container with OPENVIYA_DISPOSABLE_TEST=yes' >&2
    exit 1
}
cd /work/ops/vps
bash -n bootstrap-vps.sh
shellcheck bootstrap-vps.sh tests/*.sh
# shellcheck source=/dev/null
source ./bootstrap-vps.sh
bash tests/verify-ufw.sh
bash tests/verify-apt-timers.sh
bash tests/verify-sshd-runtime.sh

for p in 1 22 2053 65535; do port_valid "$p"; done
for p in 0 65536 -1 022 999999999999999999999 ';id' ''; do
    if port_valid "$p"; then die "Unsafe port accepted: $p"; fi
done
ports_valid ''
ports_valid '443,8443'
PANEL_DOMAIN=panel.example.test
export ACME_EMAIL=admin@example.test
validate_acme_options
for host in '127.0.0.1' 'panel' '*.example.com' '-evil.example' 'a..b' 'a/b.example'; do
    if (PANEL_DOMAIN=$host; validate_acme_options) >/dev/null 2>&1; then die "Unsafe domain accepted: $host"; fi
done
for p in '443,' ',443' '443 8443' '443,0' '443;id'; do
    if ports_valid "$p"; then die "Unsafe list accepted: $p"; fi
done
install -d -m 700 "$STATE"
ssh-keygen -t ed25519 -N '' -f "$STATE/testkey" >/dev/null
parse_options --key-file "$STATE/testkey.pub"
(parse_options --key-file "$STATE/testkey.pub" --panel-port 38472; [[ $PANEL_PORT == 38472 ]])
for bad in '--ssh-port 0' '--vpn-tcp 22' '--vpn-udp 38471' '--panel-port 22' '--admin root' '--vpn-tcp 2096' '--vpn-tcp 80' '--panel-port 80' '--ssh-port 80' '--panel-port 2096'; do
    # Intentional splitting of fixed test cases, never user data.
    # shellcheck disable=SC2086
    if (parse_options --key-file "$STATE/testkey.pub" $bad) 2>/dev/null; then die "Accepted $bad"; fi
done

export CLIENT_IP=198.51.100.10
PREPARE_CONNECTION='198.51.100.10 12345 203.0.113.10 22'
save_config
load_config
[[ $PREPARE_CONNECTION == '198.51.100.10 12345 203.0.113.10 22' ]]
mkdir -p /run/sshd /etc/ssh/sshd_config.d
ssh-keygen -A >/dev/null
cp /etc/ssh/sshd_config "$STATE/sshd_config.original"
useradd --create-home --shell /bin/bash vpsadmin
ssh_reload() { :; }
original_root=$(/usr/sbin/sshd -T -f "$STATE/sshd_config.original" | sed -n 's/^permitrootlogin //p')
write_ssh prepare
# Root keeps the original login policy, while the NEW account requires a key.
require_setting root "permitrootlogin $original_root"
write_ssh finalize
require_setting root 'permitrootlogin no'
require_setting vpsadmin 'passwordauthentication no'
require_setting vpsadmin 'kbdinteractiveauthentication no'
require_setting vpsadmin 'allowtcpforwarding local'
install -d -m 700 -o vpsadmin -g vpsadmin /home/vpsadmin/.ssh
install -m 600 -o vpsadmin -g vpsadmin "$STATE/testkey.pub" /home/vpsadmin/.ssh/authorized_keys
install -d -m 700 /root/.ssh
install -m 600 "$STATE/testkey.pub" /root/.ssh/authorized_keys
/usr/sbin/sshd -D -e -p 22222 -o ListenAddress=127.0.0.1 > "$STATE/sshd.log" 2>&1 &
sshd_pid=$!
trap 'kill "$sshd_pid" 2>/dev/null || true' EXIT
for _ in {1..10}; do
    if ssh-keyscan -T 1 -p 22222 127.0.0.1 > "$STATE/known_hosts" 2>/dev/null; then break; fi
    sleep 1
done
ssh_opts=(-p 22222 -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityAgent=none -o "UserKnownHostsFile=$STATE/known_hosts" -i "$STATE/testkey")
[[ $(ssh "${ssh_opts[@]}" vpsadmin@127.0.0.1 id -un) == vpsadmin ]]
if ssh "${ssh_opts[@]}" root@127.0.0.1 true 2>/dev/null; then die 'Root SSH login was allowed'; fi
kill "$sshd_pid"
wait "$sshd_pid" || true
trap - EXIT

# Exercise the real pinned release, real DB migrations and actual HTTP login.
# systemd is absent inside this test container; only its invocation is stubbed.
systemctl() {
    if [[ $1 == show ]]; then printf 'loaded\n'; fi
    if [[ $1 == is-active && $* == *x-ui.service* ]]; then return 1; fi
    return 0
}
# A disposable CA simulates issuance; tests never contact ACME or modify real DNS.
# shellcheck source=/dev/null
source tests/test-certificates.sh
init_test_ca
acme_preflight() { :; }
certbot() {
    printf '%s\n' "$*" >> "$STATE/certbot-test.log"
    if [[ $1 == certonly && ! -f /etc/letsencrypt/live/openviya-panel/fullchain.pem ]]; then
        issue_test_certificate "$PANEL_DOMAIN" /etc/letsencrypt/live/openviya-panel
    fi
}

# Do not change the host kernel's shared sysctls from a container.
sysctl() { :; }
configure_os
# shellcheck disable=SC2016
apt-config dump | grep -F 'codename=${distro_codename}-security' >/dev/null
install_panel
install_panel  # Retry must preserve the generated credentials and database.
openssl verify -verify_hostname "$PANEL_DOMAIN" "$TLS_DIR/panel.crt"
[[ $(stat -c %a "$TLS_DIR/panel.key") == 600 ]]
# shellcheck source=/dev/null
source "$STATE/credentials"
/usr/local/x-ui/x-ui > "$STATE/server.log" 2>&1 &
panel_pid=$!
trap 'kill "$panel_pid" 2>/dev/null || true' EXIT
for _ in {1..30}; do
    if curl -fsS --noproxy '*' --resolve "$PANEL_DOMAIN:$PANEL_PORT:127.0.0.1" "https://$PANEL_DOMAIN:$PANEL_PORT$PANEL_PATH" -o /dev/null 2>/dev/null; then break; fi
    sleep 1
done
python3 - "$PANEL_PORT" "$PANEL_PATH" "$PANEL_USERNAME" "$PANEL_PASSWORD" "$PANEL_DOMAIN" <<'PY'
import http.cookiejar, json, sqlite3, ssl, subprocess, sys, urllib.parse, urllib.request
port, path, user, password, domain = sys.argv[1:]
base = 'https://' + domain + ':' + port + path
tls = ssl.create_default_context()
# Test DNS is confined to this disposable container.
with open('/etc/hosts', 'a') as hosts:
    hosts.write('\n127.0.0.1 ' + domain + '\n')
for secret, expected in [(password, True), ('incorrect-password', False)]:
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
                                        urllib.request.HTTPSHandler(context=tls))
    token = json.load(opener.open(base + 'csrf-token', timeout=5))['obj']
    data = urllib.parse.urlencode({'username': user, 'password': secret}).encode()
    request = urllib.request.Request(base + 'login', data=data, headers={'X-CSRF-Token': token})
    response = json.load(opener.open(request, timeout=5))
    assert response['success'] is expected, 'Login policy failure'
db = sqlite3.connect('/etc/x-ui/x-ui.db')
settings = dict(db.execute('SELECT key,value FROM settings'))
assert settings['subEnable'] == 'false'
assert settings['subListen'] == '127.0.0.1'
assert db.execute('SELECT COUNT(*) FROM inbounds').fetchone()[0] == 0
assert db.execute('SELECT COUNT(*) FROM users WHERE username=?', (user,)).fetchone()[0] == 1
listeners = subprocess.check_output(['ss', '-H', '-ltn'], text=True)
assert '127.0.0.1:' + port in listeners
assert not any(r.split()[3].endswith(':2096') for r in listeners.splitlines())
print('Pinned release: login, credentials, DB, disabled subscriptions and loopback bind OK')
PY
xray_health
install_backup
install -m 700 bootstrap-vps.sh "$SELF"
install_rollback
systemd-analyze verify /etc/systemd/system/x-ui.service \
    /etc/systemd/system/openviya-xui-backup.service /etc/systemd/system/openviya-xui-backup.timer \
    /etc/systemd/system/openviya-access-rollback.service /etc/systemd/system/openviya-access-rollback.timer \
    /usr/lib/systemd/system/certbot.service /usr/lib/systemd/system/certbot.timer
/usr/local/sbin/openviya-xui-backup
backup=$(find /var/backups/3x-ui -name 'backup-*.tar.gz' -type f | head -n1)
[[ -n $backup ]]
mkdir "$STATE/restored"
tar -xzf "$backup" -C "$STATE/restored"
[[ $(sqlite3 "$STATE/restored/x-ui.db" 'PRAGMA quick_check;') == ok ]]
[[ $(stat -c %a "$backup") == 600 ]]
cmp "$TLS_DIR/panel.key" "$STATE/restored/etc/x-ui/tls/panel.key"
kill "$panel_pid"
wait "$panel_pid" || true
trap - EXIT
# Public + SSH access: same TLS identity, working wildcard listener, no HTTP.
export PANEL_ACCESS=both
save_config
printf 'complete\n' > "$STATE/phase"
old_fingerprint=$(openssl x509 -noout -sha256 -fingerprint -in "$TLS_DIR/panel.crt")
issue_test_certificate "$PANEL_DOMAIN" /etc/letsencrypt/live/openviya-panel
deploy_cert
[[ $(openssl x509 -noout -sha256 -fingerprint -in "$TLS_DIR/panel.crt") != "$old_fingerprint" ]]
openssl verify -verify_hostname "$PANEL_DOMAIN" "$TLS_DIR/panel.crt"
# A trusted certificate for the domain must NOT validate as 127.0.0.1.
if openssl verify -verify_ip 127.0.0.1 "$TLS_DIR/panel.crt" 2>/dev/null; then die 'Domain certificate accepted for loopback IP'; fi
bash tests/verify-letsencrypt.sh
/usr/local/x-ui/x-ui setting -listenIP 0.0.0.0 >/dev/null
/usr/local/x-ui/x-ui > "$STATE/server-public.log" 2>&1 &
panel_pid=$!
trap 'kill "$panel_pid" 2>/dev/null || true' EXIT
for _ in {1..30}; do
    if curl -fsS --noproxy '*' --resolve "$PANEL_DOMAIN:$PANEL_PORT:127.0.0.1" "https://$PANEL_DOMAIN:$PANEL_PORT$PANEL_PATH" -o /dev/null 2>/dev/null; then break; fi
    sleep 1
done
curl -fsS --noproxy '*' --resolve "$PANEL_DOMAIN:$PANEL_PORT:127.0.0.1" \
    "https://$PANEL_DOMAIN:$PANEL_PORT$PANEL_PATH" -o /dev/null
ss -H -ltn "sport = :$PANEL_PORT" | grep -E '(0\.0\.0\.0|\*|\[::\]):' >/dev/null
kill "$panel_pid"
wait "$panel_pid" || true
trap - EXIT
# Exercise file restoration without touching the container host's netfilter.
ufw() { printf '%s\n' "$*" > "$STATE/firewall-rollback-call"; }
rollback
cmp /etc/ssh/sshd_config "$STATE/sshd_config.original"
[[ ! -e $SSH_DROP && $(cat "$STATE/phase") == rolled-back ]]
[[ $(cat "$STATE/firewall-rollback-call") == '--force disable' ]]
[[ -f /etc/x-ui/x-ui.db && -f $backup && -f /home/vpsadmin/.ssh/authorized_keys ]]
printf 'All bootstrap checks passed. No VPS was modified.\n'
