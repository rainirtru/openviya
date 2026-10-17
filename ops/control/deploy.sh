#!/usr/bin/env bash
# Fresh-host deployment only. Existing environments must be upgraded explicitly.
set -euo pipefail
umask 077
cd /opt/openviya-control
test -s app.jar
test ! -e private/deployment-complete
bash prepare-host.sh
for image in postgres:18.6-alpine3.23 caddy:2-alpine eclipse-temurin:25-jre ghcr.io/mhsanaei/3x-ui:v3.10.2; do
    docker pull "$image"
done
python3 - <<'PY'
import pathlib, subprocess
env = pathlib.Path('.env')
images = {'POSTGRES_IMAGE': 'postgres:18.6-alpine3.23', 'CADDY_IMAGE': 'caddy:2-alpine',
          'JAVA_RUNTIME_IMAGE': 'eclipse-temurin:25-jre', 'PANEL_IMAGE': 'ghcr.io/mhsanaei/3x-ui:v3.10.2'}
with env.open('a') as file:
    for key, image in images.items():
        digest = subprocess.check_output(['docker', 'image', 'inspect', image, '--format', '{{index .RepoDigests 0}}'], text=True).strip()
        if '@sha256:' not in digest:
            raise SystemExit('Cannot pin container image')
        file.write(key + '=' + digest + '\n')
PY
docker compose config --quiet
python3 configure-panel.py
python3 configure-tls.py
docker compose up -d db panel
python3 - <<'PY'
import json, pathlib, time, urllib.request, ssl
access = json.loads(pathlib.Path('private/access.json').read_text())
url = 'https://127.0.0.1:38471' + access['panel_path']
tls = ssl.create_default_context(cafile='panel-ca.crt')
for _ in range(60):
    try:
        with urllib.request.urlopen(url, timeout=3, context=tls) as response:
            if response.status == 200:
                print('Panel listener ready')
                break
    except Exception:
        pass
    time.sleep(2)
else:
    raise SystemExit('Panel did not start; inspect private panel logs on server')
PY
python3 link-panel.py
echo 'Waiting for verified backend artifact upload'
for attempt in $(seq 1 450); do
    if test -s app.jar.sha256 && sha256sum --check --status app.jar.sha256; then
        break
    fi
    sleep 2
done
sha256sum --check --status app.jar.sha256
docker compose build app
bash finish-deployment.sh
