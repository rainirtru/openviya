#!/usr/bin/env python3
"""Create an ordinary SSH account with the existing administrator's verified public key."""
import os
import pathlib
import pwd
import re
import subprocess

os.umask(0o077)
username = 'openviya'
fingerprint = 'SHA256:iDNFcjlay1zUien+K3QljtqRdqILl0DLwarpi+Jlr6c'
marker = pathlib.Path('/var/lib/openviya-limited-user.created')
public_key = None
for line in pathlib.Path('/root/.ssh/authorized_keys').read_text().splitlines():
    check = subprocess.run(['ssh-keygen', '-lf', '/dev/stdin', '-E', 'sha256'], input=line + '\n', text=True, capture_output=True)
    if check.returncode == 0 and fingerprint in check.stdout.split():
        match = re.search(r'(ssh-ed25519\s+[A-Za-z0-9+/=]+)(?:\s|$)', line)
        if match:
            public_key = match.group(1) + ' openviya-vps'
            break
if public_key is None:
    raise SystemExit('The verified administrator public key is missing; no account changed')
try:
    account = pwd.getpwnam(username)
    if not marker.exists():
        raise SystemExit('Account already exists and is not owned by this provisioning script')
except KeyError:
    subprocess.run(['useradd', '--create-home', '--shell', '/bin/bash', username], check=True)
    account = pwd.getpwnam(username)
    marker.touch(mode=0o600)
groups = subprocess.check_output(['id', '-nG', username], text=True).split()
if any(group in groups for group in ['root', 'sudo', 'admin', 'docker', 'lxd', 'incus-admin', 'adm', 'systemd-journal']):
    raise SystemExit('Unexpected privileged group membership; no SSH configuration changed')
subprocess.run(['usermod', '--lock', username], check=True)
home = pathlib.Path(account.pw_dir)
home.chmod(0o750)
ssh = home / '.ssh'
ssh.mkdir(mode=0o700, exist_ok=True)
ssh.chmod(0o700)
os.chown(ssh, account.pw_uid, account.pw_gid)
authorized = ssh / 'authorized_keys'
authorized.write_text(public_key + '\n')
authorized.chmod(0o600)
os.chown(authorized, account.pw_uid, account.pw_gid)
config = pathlib.Path('/etc/ssh/sshd_config.d/30-openviya-limited-user.conf')
config.write_text('''Match User openviya
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    AllowAgentForwarding no
    X11Forwarding no
    AllowTcpForwarding local
    AllowStreamLocalForwarding no
    PermitOpen 127.0.0.1:38471 127.0.0.1:8080
    PermitListen none
    PermitTunnel no
    PermitUserRC no
Match all
''')
config.chmod(0o644)
subprocess.run(['sshd', '-t'], check=True)
subprocess.run(['systemctl', 'reload', 'ssh'], check=True)
print('Created limited SSH account openviya; key login, no sudo or Docker privileges')
