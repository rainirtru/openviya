#!/usr/bin/env bash
# Restore one deploy-web.sh backup by its directory name.
set -euo pipefail
umask 077

root=/opt/openviya-control
if [[ $(id -u) -ne 0 || $# -ne 1 ]]; then
    echo "Usage: sudo $0 BACKUP-ID" >&2
    exit 2
fi
backup_id=$1
[[ $backup_id =~ ^[A-Za-z0-9._-]{1,80}$ ]] || { echo 'Invalid backup ID' >&2; exit 2; }
backup="$root/private/web-deploy-backups/$backup_id"
for file in "$backup/compose.yml" "$backup/Caddyfile.https" "$backup/Caddyfile" "$backup/web-current-target"; do
    [[ -f $file ]] || { echo "Incomplete deployment backup: $backup_id" >&2; exit 2; }
done

cd "$root"
target=$(cat "$backup/web-current-target")
if [[ -n $target ]]; then
    [[ $target == releases/* && -f "$root/web/$target/public/index.html" ]] || {
        echo 'Saved web release is missing; refusing rollback' >&2
        exit 1
    }
fi

docker compose --project-directory "$root" -f "$backup/compose.yml" config --quiet
docker compose --project-directory "$root" -f "$backup/compose.yml" run --rm -T --no-deps \
    -v "$backup/Caddyfile.https:/etc/caddy/Caddyfile:ro" proxy \
    caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile

work=$(mktemp -d "$root/private/web-rollback.XXXXXXXX")
old_target=''
if [[ -L $root/web/current ]]; then old_target=$(readlink "$root/web/current"); fi
cp -p "$root/compose.yml" "$work/compose.yml"
cp -p "$root/Caddyfile.https" "$work/Caddyfile.https"
cp -p "$root/proxy/Caddyfile" "$work/Caddyfile"
cleanup() {
    local result=$?
    if [[ $result -ne 0 ]]; then
        install -m 0644 "$work/compose.yml" "$root/compose.yml"
        install -m 0644 "$work/Caddyfile.https" "$root/Caddyfile.https"
        install -m 0644 "$work/Caddyfile" "$root/proxy/Caddyfile"
        if [[ -n $old_target ]]; then
            ln -sfn "$old_target" "$root/web/.current.rollback"
            mv -Tf "$root/web/.current.rollback" "$root/web/current"
        else
            rm -f "$root/web/current"
        fi
        docker compose up -d --force-recreate app proxy || echo 'Restore of pre-rollback services failed' >&2
    fi
    rm -rf -- "$work"
    exit "$result"
}
trap cleanup EXIT

install -m 0644 "$backup/compose.yml" "$root/.compose.yml.new"
install -m 0644 "$backup/Caddyfile.https" "$root/.Caddyfile.https.new"
install -m 0644 "$backup/Caddyfile.https" "$root/proxy/.Caddyfile.new"
mv -f "$root/.compose.yml.new" "$root/compose.yml"
mv -f "$root/.Caddyfile.https.new" "$root/Caddyfile.https"
mv -f "$root/proxy/.Caddyfile.new" "$root/proxy/Caddyfile"
if [[ -n $target ]]; then
    ln -s "$target" "$root/web/.current.rollback"
    mv -Tf "$root/web/.current.rollback" "$root/web/current"
else
    rm -f "$root/web/current"
fi
docker compose up -d --force-recreate app proxy
healthy=0
for attempt in $(seq 1 45); do
    if [[ -n $target ]]; then
        if curl --fail --silent --show-error --max-time 4 --resolve api.openviya.online:443:127.0.0.1 \
            https://api.openviya.online/account/ -o /dev/null \
            && curl --fail --silent --show-error --max-time 4 http://127.0.0.1:8080/actuator/health/readiness | grep -q '"UP"' \
            && [[ $(curl --silent --show-error --max-time 4 --resolve openviya.online:443:127.0.0.1 \
                -o /dev/null -w '%{http_code}' https://openviya.online/) == 200 ]] \
            && [[ $(curl --silent --show-error --max-time 4 --resolve www.openviya.online:443:127.0.0.1 \
                -o /dev/null -w '%{http_code}' https://www.openviya.online/) == 301 ]]; then
            healthy=1
            break
        fi
    elif curl --fail --silent --show-error --max-time 4 http://127.0.0.1:8080/actuator/health/readiness | grep -q '"UP"'; then
        healthy=1
        break
    fi
    sleep 2
done
[[ $healthy -eq 1 ]] || { echo 'Rollback health check failed' >&2; exit 1; }
echo "Deployment config and static release restored from $backup_id"
