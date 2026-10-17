#!/usr/bin/env bash
set -euo pipefail
umask 077
cd /opt/openviya-control
docker compose up -d app
wait_backend() {
    for attempt in $(seq 1 90); do
        if curl --fail --silent --max-time 3 http://127.0.0.1:8080/actuator/health/readiness | grep -q '"UP"'; then
            echo 'Backend readiness UP'
            return 0
        fi
        sleep 2
    done
    echo 'Backend readiness failed; inspect application logs on server'
    return 1
}
wait_backend
python3 - <<'PY'
import pathlib
env = pathlib.Path('.env')
lines = [line for line in env.read_text().splitlines() if not line.startswith(('OPENVIYA_BOOTSTRAP_OWNER_ENABLED=', 'OPENVIYA_BOOTSTRAP_OWNER_LOGIN=', 'OPENVIYA_BOOTSTRAP_OWNER_PASSWORD='))]
env.write_text('\n'.join(lines) + '\nOPENVIYA_BOOTSTRAP_OWNER_ENABLED=false\n')
env.chmod(0o600)
PY
docker compose up -d app proxy
wait_backend
bash install-services.sh
touch private/deployment-complete
echo 'Deployment completed; public HTTPS waits for DNS; OIDC waits for BotFather credentials'
