#!/usr/bin/env bash
# Atomically install a static web release and control-plane config on the VPS.
set -euo pipefail
umask 077

root=/opt/openviya-control
if [[ $(id -u) -ne 0 ]]; then
    echo 'Run this deployment helper as root on the VPS' >&2
    exit 1
fi
if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "Usage: $0 WEB-RELEASE.zip CONTROL-CONFIG-DIR [RELEASE-ID]" >&2
    exit 2
fi
archive=$(realpath -- "$1")
config=$(realpath -- "$2")
release_id=${3:-$(date -u +%Y%m%dT%H%M%SZ)}
[[ -f $archive ]] || { echo 'Web archive not found' >&2; exit 2; }
[[ $release_id =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] || { echo 'Invalid release ID' >&2; exit 2; }
for candidate in "$config/compose.yml" "$config/Caddyfile.https"; do
    [[ -f $candidate ]] || { echo "Required config file missing: $candidate" >&2; exit 2; }
done

cd "$root"
install -d -m 755 web web/releases
install -d -m 700 private/web-deploy-backups
chmod 755 web web/releases
stage=$(mktemp -d "$root/web/.stage.XXXXXXXX")
backup=''
release="$root/web/releases/$release_id"
previous_link=''
committed=0
release_created=0
cleanup() {
    local result=$?
    if [[ $committed -ne 1 && -n $backup ]]; then
        echo 'Deployment did not complete; restoring previous config and static release' >&2
        install -m 0644 "$backup/compose.yml" "$root/compose.yml"
        install -m 0644 "$backup/Caddyfile.https" "$root/Caddyfile.https"
        install -m 0644 "$backup/Caddyfile" "$root/proxy/Caddyfile"
        if [[ -n $previous_link ]]; then
            ln -sfn "$previous_link" "$root/web/.current.rollback"
            mv -Tf "$root/web/.current.rollback" "$root/web/current"
        else
            rm -f "$root/web/current"
        fi
        docker compose up -d --force-recreate app proxy || echo 'Automatic rollback service restart failed; inspect Docker Compose' >&2
    fi
    if [[ $committed -ne 1 && $release_created -eq 1 ]]; then
        rm -rf -- "$release"
    fi
    rm -rf -- "$stage"
    exit "$result"
}
trap cleanup EXIT

if [[ -e $root/web/current && ! -L $root/web/current ]]; then
    echo 'web/current exists but is not a symlink; refusing deployment' >&2
    exit 1
fi

# Extract only regular files beneath the expected public/ root. Reject traversal,
# symlinks, oversized archives, and unexpected top-level entries.
python3 - "$archive" "$stage" <<'PY'
import pathlib, stat, sys, zipfile

archive = pathlib.Path(sys.argv[1])
stage = pathlib.Path(sys.argv[2])
max_total = 100 * 1024 * 1024
max_files = 2000
with zipfile.ZipFile(archive) as zf:
    items = zf.infolist()
    if not items or len(items) > max_files:
        raise SystemExit('Web archive is empty or has too many entries')
    total = 0
    for item in items:
        name = item.filename
        path = pathlib.PurePosixPath(name)
        mode = item.external_attr >> 16
        if path.is_absolute() or '..' in path.parts or '\\' in name or not path.parts or path.parts[0] != 'public':
            raise SystemExit('Web archive contains a path outside public/')
        if stat.S_ISLNK(mode):
            raise SystemExit('Web archive may not contain symlinks')
        if item.is_dir():
            continue
        file_type = stat.S_IFMT(mode)
        if file_type not in (0, stat.S_IFREG):
            raise SystemExit('Web archive may contain regular files only')
        total += item.file_size
        if total > max_total:
            raise SystemExit('Expanded web archive exceeds 100 MiB')
        destination = stage.joinpath(*path.parts)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with zf.open(item) as source, destination.open('wb') as target:
            target.write(source.read())
        destination.chmod(0o644)
public = stage / 'public'
if not (public / 'index.html').is_file() or not (public / 'account' / 'index.html').is_file():
    raise SystemExit('Archive must include public/index.html and public/account/index.html')
PY

[[ ! -e $release ]] || { echo "Release already exists: $release_id" >&2; exit 2; }
mv "$stage/public" "$stage/release-public"
install -d -m 755 "$release"
release_created=1
mv "$stage/release-public" "$release/public"
chmod -R a+rX,go-w "$release"
chmod 755 "$root/web" "$root/web/releases" "$release" "$release/public"

docker compose --project-directory "$root" -f "$config/compose.yml" config --quiet
docker compose --project-directory "$root" -f "$config/compose.yml" run --rm -T --no-deps \
    -v "$config/Caddyfile.https:/etc/caddy/Caddyfile:ro" proxy \
    caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile

stamp=$(date -u +%Y%m%dT%H%M%SZ)
backup="$root/private/web-deploy-backups/$stamp"
backup=$(mktemp -d "$root/private/web-deploy-backups/$stamp.XXXXXXXX")
chmod 700 "$backup"
cp -p "$root/compose.yml" "$backup/compose.yml"
cp -p "$root/Caddyfile.https" "$backup/Caddyfile.https"
cp -p "$root/proxy/Caddyfile" "$backup/Caddyfile"
if [[ -L $root/web/current ]]; then
    previous_link=$(readlink "$root/web/current")
fi
printf '%s\n' "$previous_link" >"$backup/web-current-target"
chmod 600 "$backup/web-current-target"

install -m 0644 "$config/compose.yml" "$root/.compose.yml.new"
install -m 0644 "$config/Caddyfile.https" "$root/.Caddyfile.https.new"
install -m 0644 "$config/Caddyfile.https" "$root/proxy/.Caddyfile.new"
mv -f "$root/.compose.yml.new" "$root/compose.yml"
mv -f "$root/.Caddyfile.https.new" "$root/Caddyfile.https"
mv -f "$root/proxy/.Caddyfile.new" "$root/proxy/Caddyfile"
ln -s "releases/$release_id" "$root/web/.current.new"
mv -Tf "$root/web/.current.new" "$root/web/current"
docker compose up -d --force-recreate app proxy

for attempt in $(seq 1 45); do
    if curl --fail --silent --show-error --max-time 4 --resolve api.openviya.online:443:127.0.0.1 \
        https://api.openviya.online/account/ -o /dev/null \
        && curl --fail --silent --show-error --max-time 4 http://127.0.0.1:8080/actuator/health/readiness | grep -q '"UP"' \
        && [[ $(curl --silent --show-error --max-time 4 --resolve openviya.online:443:127.0.0.1 \
            -o /dev/null -w '%{http_code}' https://openviya.online/) == 200 ]] \
        && [[ $(curl --silent --show-error --max-time 4 --resolve www.openviya.online:443:127.0.0.1 \
            -o /dev/null -w '%{http_code}' https://www.openviya.online/) == 301 ]]; then
        committed=1
        echo "Web release $release_id deployed; backup: $backup"
        exit 0
    fi
    sleep 2
done
echo 'Cabinet HTTPS health check failed' >&2
exit 1
