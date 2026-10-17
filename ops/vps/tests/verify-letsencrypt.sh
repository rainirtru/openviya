#!/usr/bin/env bash
# Failure/recovery paths with real X.509 verification and simulated Certbot/systemd.
set -Eeuo pipefail
[[ -f /.dockerenv && ${OPENVIYA_DISPOSABLE_TEST:-} == yes ]] || exit 1
cd /work/ops/vps
# shellcheck source=/dev/null
source ./bootstrap-vps.sh
load_config
# shellcheck source=/dev/null
source tests/test-certificates.sh
lineage=/etc/letsencrypt/live/openviya-panel
cp "$TLS_DIR/panel.crt" "$STATE/last-valid.crt"
cp "$TLS_DIR/panel.key" "$STATE/last-valid.key"
systemctl() {
    printf '%s\n' "$*" >> "$STATE/tls-systemctl.log"
    case $1 in
        is-active) [[ -f $STATE/test-panel-active ]] ;;
        restart) [[ ! -f $STATE/test-restart-fails ]] ;;
        *) return 0 ;;
    esac
}
# shellcheck disable=SC2329
https_probe() { [[ ! -f $STATE/test-probe-fails ]]; }
acme_preflight() { :; }
# shellcheck disable=SC2329
apt-get() { :; }
# shellcheck disable=SC2329
ufw() { printf 'Status: active\n'; }
certbot() { printf '%s\n' "$*" >> "$STATE/certbot-test.log"; [[ ! -f $STATE/test-acme-fails ]]; }

# Hostname mismatch and key mismatch must leave the active pair untouched.
issue_test_certificate wrong.example.test "$lineage"
if (deploy_cert) >/dev/null 2>&1; then die 'Wrong certificate hostname accepted'; fi
cmp "$TLS_DIR/panel.crt" "$STATE/last-valid.crt"
issue_test_certificate "$PANEL_DOMAIN" "$lineage"
cp "$STATE/last-valid.key" "$lineage/privkey.pem"
if (deploy_cert) >/dev/null 2>&1; then die 'Mismatched certificate key accepted'; fi
cmp "$TLS_DIR/panel.key" "$STATE/last-valid.key"

issue_test_certificate "$PANEL_DOMAIN" "$lineage"
touch "$STATE/test-panel-active" "$STATE/test-restart-fails"
if (deploy_cert) >/dev/null 2>&1; then die 'Failed restart accepted'; fi
cmp "$TLS_DIR/panel.crt" "$STATE/last-valid.crt"
cmp "$TLS_DIR/panel.key" "$STATE/last-valid.key"
rm "$STATE/test-restart-fails"
touch "$STATE/test-probe-fails"
if (deploy_cert) >/dev/null 2>&1; then die 'Failed HTTPS verification accepted'; fi
cmp "$TLS_DIR/panel.crt" "$STATE/last-valid.crt"
rm "$STATE/test-probe-fails"
deploy_cert
cmp "$lineage/fullchain.pem" "$TLS_DIR/panel.crt"
: > "$STATE/tls-systemctl.log"
deploy_cert
if grep -q restart "$STATE/tls-systemctl.log"; then die 'Unchanged certificate caused restart'; fi

# A failed CA request must preserve config, active TLS, credentials and panel port.
cp "$STATE/config" "$STATE/config.before-acme"
cp "$STATE/credentials" "$STATE/credentials.before-acme"
cp "$TLS_DIR/panel.crt" "$STATE/current.crt"
touch "$STATE/test-acme-fails"
if (setup_letsencrypt --domain other.example.test --acme-email admin@example.test) >/dev/null 2>&1; then die 'Failed issuance accepted'; fi
cmp "$STATE/config" "$STATE/config.before-acme"
cmp "$TLS_DIR/panel.crt" "$STATE/current.crt"
rm "$STATE/test-acme-fails"
setup_letsencrypt --domain "$PANEL_DOMAIN" --acme-email admin@example.test
cmp "$STATE/credentials" "$STATE/credentials.before-acme"
[[ $PANEL_PORT == 38471 ]]

# Legacy configs remain identifiable, with their original panel port preserved.
sed '/^TLS_MODE=/d; /^PANEL_DOMAIN=/d; /^ACME_EMAIL=/d; s/^PANEL_PORT=.*/PANEL_PORT=2053/' "$STATE/config" > "$STATE/legacy-config"
cp "$STATE/legacy-config" "$STATE/config"
(load_config; [[ $TLS_MODE == selfsigned && $PANEL_PORT == 2053 ]])
if (load_config; require_acme) >/dev/null 2>&1; then die 'Legacy config silently treated as LE'; fi
cp "$STATE/config.before-acme" "$STATE/config"
printf 'rolled-back\n' > "$STATE/phase"
if (deploy_cert) >/dev/null 2>&1; then die 'Renewal restarted a rolled-back panel'; fi
printf 'complete\n' > "$STATE/phase"
rm "$STATE/test-panel-active"
printf 'Lets Encrypt: trust, hostname, key matching, rollback and migration checks passed\n'
