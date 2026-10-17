#!/usr/bin/env bash
set -euo pipefail
umask 077
cd /opt/openviya-control
cat >/etc/systemd/system/openviya-control.service <<'EOF'
[Unit]
Description=OpenViya backend and control panel
Requires=docker.service
After=docker.service network-online.target
Wants=network-online.target
[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=/opt/openviya-control
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose stop
TimeoutStartSec=180
TimeoutStopSec=90
[Install]
WantedBy=multi-user.target
EOF
cat >/etc/systemd/system/openviya-backup.service <<'EOF'
[Unit]
Description=OpenViya local database backup
After=docker.service
[Service]
Type=oneshot
ExecStart=/bin/bash /opt/openviya-control/backup.sh
EOF
cat >/etc/systemd/system/openviya-backup.timer <<'EOF'
[Unit]
Description=Daily OpenViya backup
[Timer]
OnCalendar=*-*-* 03:30:00 UTC
RandomizedDelaySec=15m
Persistent=true
[Install]
WantedBy=timers.target
EOF
cat >/etc/systemd/system/openviya-https.service <<'EOF'
[Unit]
Description=Enable OpenViya HTTPS after DNS is ready
After=network-online.target docker.service
[Service]
Type=oneshot
ExecStart=/usr/bin/python3 /opt/openviya-control/activate-https.py
EOF
cat >/etc/systemd/system/openviya-https.timer <<'EOF'
[Unit]
Description=Check OpenViya public DNS every five minutes
[Timer]
OnBootSec=2m
OnUnitActiveSec=5m
[Install]
WantedBy=timers.target
EOF
cat >/etc/systemd/system/openviya-panel-tls.service <<'EOF'
[Unit]
Description=Renew internal OpenViya panel certificate
After=docker.service
[Service]
Type=oneshot
ExecStart=/usr/bin/python3 /opt/openviya-control/configure-tls.py --renew
EOF
cat >/etc/systemd/system/openviya-panel-tls.timer <<'EOF'
[Unit]
Description=Daily panel certificate expiry check
[Timer]
OnCalendar=daily
Persistent=true
[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now openviya-control.service openviya-backup.timer openviya-https.timer openviya-panel-tls.timer
systemctl start openviya-backup.service openviya-https.service
echo 'Autostart, daily local backups and DNS activation timer installed'
