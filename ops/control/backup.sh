#!/usr/bin/env bash
set -euo pipefail
umask 077
cd /opt/openviya-control
exec 9>/run/openviya-control-backup.lock
flock -n 9 || exit 0
destination="/var/backups/openviya-control/$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 700 "$destination"
docker compose exec -T db sh -c 'export PGPASSWORD="$POSTGRES_PASSWORD"; exec pg_dump -U openviya -d openviya' | gzip >"$destination/postgres.sql.gz"
volume=$(docker volume inspect openviya-control_panel-data --format '{{.Mountpoint}}')
python3 - "$volume/x-ui.db" "$destination/panel.sqlite" <<'PY'
import sqlite3, sys
source = sqlite3.connect('file:' + sys.argv[1] + '?mode=ro', uri=True)
target = sqlite3.connect(sys.argv[2])
source.backup(target)
assert target.execute('pragma quick_check').fetchone()[0] == 'ok'
target.close()
source.close()
PY
cp .env "$destination/backend.env"
cp private/access.json "$destination/access.json"
tar -czf "$destination/panel-ca.tar.gz" panel-ca.crt private/panel-ca.key
tar -czf "$destination/panel-tls.tar.gz" -C "$volume" tls
gzip -t "$destination/postgres.sql.gz"
touch "$destination/COMPLETE"
find /var/backups/openviya-control -mindepth 1 -maxdepth 1 -type d -mtime +7 -exec rm -rf -- {} +
echo 'OpenViya local backup completed'
