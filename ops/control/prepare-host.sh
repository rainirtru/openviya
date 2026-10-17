#!/usr/bin/env bash
set -euo pipefail
umask 077
test "$(id -u)" = 0
cd /opt/openviya-control
install -d -m 700 private
if ! swapon --show=NAME --noheadings | grep -q .; then
    test ! -e /swapfile || { echo 'Existing inactive swap file needs review'; exit 1; }
    fallocate -l 2G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile >/dev/null
    swapon /swapfile
    grep -q '^/swapfile ' /etc/fstab || printf '/swapfile none swap sw 0 0\n' >> /etc/fstab
fi
printf 'vm.swappiness=20\n' >/etc/sysctl.d/90-openviya.conf
sysctl -p /etc/sysctl.d/90-openviya.conf >/dev/null
install -d /etc/systemd/journald.conf.d
printf '[Journal]\nSystemMaxUse=128M\n' >/etc/systemd/journald.conf.d/openviya.conf
systemctl restart systemd-journald
cat >/etc/ssh/sshd_config.d/00-openviya-control.conf <<'EOF'
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitEmptyPasswords no
PermitRootLogin prohibit-password
X11Forwarding no
MaxAuthTries 3
EOF
sshd -t
systemctl reload ssh
cat >/etc/fail2ban/jail.d/openviya.local <<'EOF'
[sshd]
enabled = true
backend = systemd
maxretry = 5
findtime = 10m
bantime = 1h
EOF
systemctl enable --now docker fail2ban
systemctl restart fail2ban
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
python3 - <<'PY'
import json, os, pathlib, secrets, base64, sys
root = pathlib.Path('/opt/openviya-control')
env = root / '.env'
if env.exists():
    print('Existing environment preserved')
    sys.exit(0)
access = {
    'panel_username': 'ov_' + secrets.token_hex(6),
    'panel_password': secrets.token_hex(24),
    'panel_path': '/' + secrets.token_hex(18) + '/',
    'backend_owner_login': 'owner',
    'backend_owner_password': secrets.token_hex(24),
}
credentials = root / 'private/access.json'
credentials.write_text(json.dumps(access, indent=2) + '\n')
credentials.chmod(0o600)
values = {
    'OPENVIYA_DB_PASSWORD': secrets.token_hex(32),
    'OPENVIYA_TOTP_ENCRYPTION_KEY': base64.b64encode(secrets.token_bytes(32)).decode(),
    'OPENVIYA_TELEGRAM_BOT_SERVICE_TOKEN': secrets.token_hex(32),
    'OPENVIYA_BOOTSTRAP_OWNER_ENABLED': 'true',
    'OPENVIYA_BOOTSTRAP_OWNER_LOGIN': access['backend_owner_login'],
    'OPENVIYA_BOOTSTRAP_OWNER_PASSWORD': access['backend_owner_password'],
    'OPENVIYA_TELEGRAM_OIDC_ENABLED': 'false',
    'OPENVIYA_3XUI_ENABLED': 'false',
    'OPENVIYA_3XUI_ADMIN_BASE_URI': 'https://panel:2053' + access['panel_path'],
}
env.write_text(''.join(k+'='+v+'\n' for k,v in values.items()))
env.chmod(0o600)
print('Fresh server credentials created; values not printed')
PY
echo 'Host preparation complete'
