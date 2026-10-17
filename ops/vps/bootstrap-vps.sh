#!/usr/bin/env bash
# Fresh VPS only. See README.md before running. Never run this on your workstation.
set -Eeuo pipefail
umask 077

STATE=/var/lib/openviya-vps-bootstrap
DESKTOP_STATE=/var/lib/openviya-desktop
SELF=/usr/local/sbin/openviya-vps
SSH_DROP=/etc/ssh/sshd_config.d/00-openviya-vps.conf
VERSION=v3.10.2
ADMIN=vpsadmin
SSH_PORT=22
SSH_CIDR=any
PANEL_PORT=38471
PANEL_ACCESS=tunnel
PANEL_CIDR=any
TLS_MODE=letsencrypt
PANEL_DOMAIN=
ACME_EMAIL=
ACME_NAME=openviya-panel
TLS_DIR=/etc/x-ui/tls
VPN_TCP=443
VPN_UDP=
KEY_FILE=
DESKTOP_CURRENT_STEP=startup

desktop_step_valid() {
    case ${1:-} in
        startup|validate|apt-index|apt-install|validate-access|apt-upgrade|admin-create|rollback|firewall|prepare-done|ssh-hardening|fail2ban|security-updates|log-limits|tls-check|tls-issue|tls-deploy|panel-download|panel-verify|panel-configure|panel-start|backup|health|finalize-done) return 0 ;;
        *) return 1 ;;
    esac
}
desktop_step() {
    local step=${1:-startup} state=${2:-running} code=${3:-0}
    desktop_step_valid "$step" || return 0
    [[ $state == running || $state == succeeded || $state == failed ]] || return 0
    [[ $code =~ ^[0-9]{1,3}$ ]] && ((10#$code <= 255)) || return 0
    DESKTOP_CURRENT_STEP=$step
    # Fixed identifiers only: never send command output, arguments, domains or credentials.
    if [[ -n ${NOTIFY_SOCKET:-} ]] && command -v systemd-notify >/dev/null 2>&1; then
        timeout 3 systemd-notify --status="openviya-v1:$step:$state:$code" >/dev/null 2>&1 || true
    fi
    return 0
}
desktop_error() {
    local code=$1 line=$2
    desktop_step "$DESKTOP_CURRENT_STEP" failed "$code"
    printf 'Failed at line %s. Keep SSH open; inspect rollback timer and README.\n' "$line" >&2
}
die() { desktop_step "$DESKTOP_CURRENT_STEP" failed 1; printf 'ERROR: %s\n' "$*" >&2; exit 1; }
log() { printf '\n==> %s\n' "$*"; }
port_valid() { [[ $1 =~ ^[1-9][0-9]{0,4}$ ]] && ((10#$1 <= 65535)); }
ports_valid() {
    local p
    [[ -z $1 ]] && return 0
    [[ $1 =~ ^[0-9]+(,[0-9]+)*$ ]] || return 1
    local -a list
    IFS=, read -r -a list <<< "$1"
    for p in "${list[@]}"; do port_valid "$p" || return 1; done
}
panel_sources_valid() {
    python3 - "$PANEL_CIDR" <<'PY'
import ipaddress, sys
value = sys.argv[1]
if value == 'any':
    raise SystemExit(0)
lines = value.replace('\r\n', '\n').split('\n')
if not 1 <= len(lines) <= 100:
    raise SystemExit('Panel sources require 1 to 100 IP addresses or CIDRs')
for line in lines:
    if not line or line != line.strip() or '%' in line:
        raise SystemExit('Invalid panel source')
    ipaddress.ip_network(line, strict=True)
PY
}
configure_panel_firewall() {
    [[ $PANEL_ACCESS == both ]] || return 0
    panel_sources_valid || die 'Invalid panel source list'
    if [[ $PANEL_CIDR == any ]]; then
        ufw allow "$PANEL_PORT/tcp" comment '3x-ui public HTTPS'
    else
        local source
        while IFS= read -r source; do
            ufw allow from "$source" to any port "$PANEL_PORT" proto tcp comment '3x-ui HTTPS admin network'
        done <<< "${PANEL_CIDR//$'\r'/}"
    fi
}
usage() {
    cat <<'HELP'
Fresh Ubuntu 26.04/24.04 / Debian 12,13, amd64/arm64, systemd.
  bash bootstrap-vps.sh prepare --key-file /root/vpsadmin.pub [options]
  sudo --preserve-env=SSH_CONNECTION /usr/local/sbin/openviya-vps finalize
  sudo /usr/local/sbin/openviya-vps desktop-inspect --admin vpsadmin --ssh-port 22
  sudo /usr/local/sbin/openviya-vps desktop-prepare --run-id GUID --key-file /root/vpsadmin.pub [options]
  sudo /usr/local/sbin/openviya-vps desktop-job-status --run-id GUID --stage prepare|finalize
  sudo --preserve-env=SSH_CONNECTION /usr/local/sbin/openviya-vps desktop-finalize --run-id GUID
  sudo /usr/local/sbin/openviya-vps desktop-credentials
  sudo /usr/local/sbin/openviya-vps status
  sudo /usr/local/sbin/openviya-vps rollback
  sudo /usr/local/sbin/openviya-vps renew-cert
  sudo /usr/local/sbin/openviya-vps setup-letsencrypt --domain HOST --acme-email EMAIL
Options (prepare only):
  --admin vpsadmin       New sudo user; must not already exist
  --ssh-port 22          CURRENT SSH port; this script does not change it
  --ssh-cidr any         One IPv4/IPv6 CIDR, or 'any'; must include your IP
  --panel-port 38471     HTTPS panel port (default: 38471)
  --panel-access tunnel Local HTTPS only (default), or 'both' for public + tunnel
  --panel-cidr any      Newline-separated public IP/CIDR sources (max 100); 'any' permits all
  --domain HOST         Panel DNS name, e.g. panel-usa-1.openviya.online (not VPN address)
  --acme-email EMAIL    Required Let's Encrypt account email; accepts LE terms
  --vpn-tcp 443          Comma-separated public TCP ports; '' opens none
  --vpn-udp ''           Comma-separated public UDP ports; '' opens none
HTTP-01 requires public TCP 80, direct DNS A/AAAA to this VPS and no port-80 server.
TCP 80 stays allowed for renewal; Certbot listens only during validation.
All settings are saved during prepare. Finalize from a NEW key-authenticated
SSH connection as the created user. Automatic access rollback: 20 minutes.
HELP
}
parse_options() {
    while (($#)); do
        (($# >= 2)) || die "Missing value for $1"
        case $1 in
            --key-file) KEY_FILE=$2 ;; --admin) ADMIN=$2 ;;
            --ssh-port) SSH_PORT=$2 ;; --ssh-cidr) SSH_CIDR=$2 ;;
            --panel-port) PANEL_PORT=$2 ;; --vpn-tcp) VPN_TCP=$2 ;;
            --panel-access) PANEL_ACCESS=$2 ;; --panel-cidr) PANEL_CIDR=$2 ;;
            --domain) PANEL_DOMAIN=${2,,} ;; --acme-email) ACME_EMAIL=$2 ;;
            --vpn-udp) VPN_UDP=$2 ;; *) die "Unknown option: $1" ;;
        esac
        shift 2
    done
    [[ $ADMIN =~ ^[a-z][a-z0-9_-]{0,30}$ && $ADMIN != root ]] || die 'Invalid admin name'
    if ! port_valid "$SSH_PORT" || ! port_valid "$PANEL_PORT"; then die 'Invalid port'; fi
    if ! ports_valid "$VPN_TCP" || ! ports_valid "$VPN_UDP"; then die 'Invalid VPN port list'; fi
    [[ $SSH_PORT != "$PANEL_PORT" ]] || die 'SSH and panel ports must differ'
    [[ $PANEL_ACCESS == tunnel || $PANEL_ACCESS == both ]] || die '--panel-access must be tunnel or both'
    validate_acme_options
    [[ $SSH_PORT != 80 && $PANEL_PORT != 80 && $PANEL_PORT != 2096 ]] || die 'TCP 80 is reserved for ACME; 2096 is reserved for subscriptions'
    local p
    for p in ${VPN_TCP//,/ } ${VPN_UDP//,/ }; do
        [[ $p != "$SSH_PORT" && $p != "$PANEL_PORT" && $p != 2096 && $p != 80 ]] ||
            die 'VPN ports must not overlap SSH, panel, ACME 80 or subscription 2096'
    done
    [[ -f $KEY_FILE && ! -L $KEY_FILE ]] || die 'Provide --key-file with one PUBLIC Ed25519 key'
}
save_config() {
    local name
    for name in ADMIN SSH_PORT SSH_CIDR PANEL_PORT PANEL_ACCESS PANEL_CIDR TLS_MODE PANEL_DOMAIN ACME_EMAIL VPN_TCP VPN_UDP PREPARE_CONNECTION CLIENT_IP; do
        printf '%s=%q\n' "$name" "${!name}"
    done > "$STATE/config"
}
load_config() {
    [[ -f $STATE/config && $(stat -c '%u:%a' "$STATE/config") == 0:600 ]] || die 'Missing or unsafe saved configuration'
    # Root-owned file generated only with printf %q above.
    TLS_MODE=selfsigned  # Old installations must explicitly migrate; no silent rotation.
    # shellcheck source=/dev/null
    source "$STATE/config"
}
check_os() {
    [[ -f /etc/os-release && -d /run/systemd/system ]] || die 'Linux with systemd required'
    # shellcheck source=/dev/null
    source /etc/os-release
    case "$ID:$VERSION_ID" in ubuntu:26.04|ubuntu:24.04|debian:12|debian:13) ;; *) die "Unsupported OS: $ID $VERSION_ID" ;; esac
    case $(uname -m) in x86_64|aarch64|arm64) ;; *) die 'Only amd64 and arm64 supported' ;; esac
}
ssh_check() {
    # /run is temporary; the directory can be absent before ssh.service starts
    # or after a package restart. sshd -t requires it even for validation.
    install -d -o root -g root -m 755 /run/sshd
    /usr/sbin/sshd -t
}
ssh_reload() { systemctl reload ssh.service; }
effective_ssh() {
    /usr/sbin/sshd -T -C "user=$1,host=localhost,addr=$CLIENT_IP"
}
require_setting() {
    # Drain the whole output: grep -q can give sshd SIGPIPE under pipefail.
    effective_ssh "$1" | grep -Fx "$2" >/dev/null || die "Effective SSH setting mismatch for $1: $2"
}
write_ssh() {
    local mode=$1
    if [[ $mode == prepare ]]; then
        cat > "$SSH_DROP" <<EOF
PubkeyAuthentication yes
EOF
    else
        cat > "$SSH_DROP" <<EOF
PermitRootLogin no
PubkeyAuthentication yes
AuthenticationMethods publickey
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitEmptyPasswords no
X11Forwarding no
AllowAgentForwarding no
PermitTunnel no
AllowTcpForwarding local
AllowStreamLocalForwarding no
GatewayPorts no
PermitOpen 127.0.0.1:$PANEL_PORT
MaxAuthTries 3
LoginGraceTime 30
ClientAliveInterval 300
ClientAliveCountMax 2
EOF
    fi
    # A Match block in an included file would leak its context into the rest of
    # sshd_config. Put the temporary per-user block at the END of the main file.
    { printf 'Include %s\n' "$SSH_DROP"; cat "$STATE/sshd_config.original"; } > "$STATE/sshd_config.new"
    if [[ $mode == prepare ]]; then
        cat >> "$STATE/sshd_config.new" <<EOF

Match User $ADMIN
    AuthenticationMethods publickey
    PubkeyAuthentication yes
    PasswordAuthentication no
    KbdInteractiveAuthentication no
EOF
    fi
    install -m 600 "$STATE/sshd_config.new" /etc/ssh/sshd_config
    ssh_check
    require_setting "$ADMIN" 'authenticationmethods publickey'
    require_setting "$ADMIN" 'pubkeyauthentication yes'
    require_setting "$ADMIN" 'usepam yes'
    if [[ $mode == finalize ]]; then
        require_setting root 'permitrootlogin no'
        require_setting "$ADMIN" 'passwordauthentication no'
        require_setting "$ADMIN" "permitopen 127.0.0.1:$PANEL_PORT"
    fi
    ssh_reload
}
install_rollback() {
    # This restores only access controls. It never removes users, keys or data.
    cat > /etc/systemd/system/openviya-access-rollback.service <<EOF
[Unit]
Description=Restore pre-bootstrap SSH and disable fresh-host UFW
[Service]
Type=oneshot
ExecStart=$SELF rollback
EOF
    cat > /etc/systemd/system/openviya-access-rollback.timer <<'EOF'
[Unit]
Description=Recover VPS access unless provisioning completes
[Timer]
OnActiveSec=20min
AccuracySec=1s
Unit=openviya-access-rollback.service
[Install]
WantedBy=timers.target
EOF
    systemctl daemon-reload
    systemctl enable --now openviya-access-rollback.timer
}
rollback() {
    [[ -f $STATE/sshd_config.original ]] || die 'No original SSH configuration saved'
    log 'Restoring SSH and disabling UFW; use provider firewall to restrict access'
    # Stop the panel too: a later manual bind change must not expose it on rollback.
    systemctl stop x-ui.service 2>/dev/null || true
    if command -v fail2ban-client >/dev/null; then fail2ban-client stop sshd 2>/dev/null || true; fi
    rm -f -- "$SSH_DROP"
    cp -p -- "$STATE/sshd_config.original" /etc/ssh/sshd_config
    ssh_check
    ufw --force disable
    ssh_reload
    printf 'rolled-back\n' > "$STATE/phase"
    systemctl disable --now openviya-access-rollback.timer
    log 'Access rollback complete. OS updates, admin account and panel data retained.'
}
configure_firewall() {
    local p proto ports
    # Refuse pre-existing UFW rules before prepare; reset only our fresh-host rules.
    ufw --force reset
    sed -i 's/^IPV6=.*/IPV6=yes/' /etc/default/ufw
    grep -qx 'IPV6=yes' /etc/default/ufw || die 'Cannot enable IPv6 firewall support'
    ufw default deny incoming
    ufw default allow outgoing
    ufw default deny routed
    ufw allow 80/tcp comment 'Lets Encrypt HTTP-01 renewal'
    if [[ $SSH_CIDR == any ]]; then
        ufw allow "$SSH_PORT/tcp" comment 'SSH key access'
    else
        ufw allow from "$SSH_CIDR" to any port "$SSH_PORT" proto tcp comment 'SSH admin network'
    fi
    for proto in tcp udp; do
        if [[ $proto == tcp ]]; then ports=$VPN_TCP; else ports=$VPN_UDP; fi
        for p in ${ports//,/ }; do ufw allow "$p/$proto" comment 'Explicit VPN inbound'; done
    done
    configure_panel_firewall
    ufw logging low
    ufw --force enable
}
check_ufw_rules() {
    local added rules
    if ! added=$(ufw show added); then die 'Cannot inspect existing UFW rules'; fi
    # UFW 0.36 prints a literal '(None)' for a clean installation.
    # Keep unknown output fail-closed and never reset genuine rules here.
    rules=$(printf '%s\n' "$added" | sed '/^Added user rules/d; /^(None)$/d; /^[[:space:]]*$/d')
    if [[ -n $rules ]]; then
        printf '%s\n' "$added" >&2
        die 'Existing UFW rules refused; review the rules above before provisioning'
    fi
}
prepare() {
    desktop_step validate
    parse_options "$@"
    check_os
    [[ ! -e $STATE/phase ]] || die "Already prepared: use $SELF status; do not overwrite state"
    ! id "$ADMIN" &>/dev/null || die 'Admin user already exists; fresh host required'
    ! getent group "$ADMIN" >/dev/null || die 'Admin name conflicts with an existing group; choose another --admin'
    [[ ! -e /etc/x-ui && ! -e /usr/local/x-ui && ! -e $SSH_DROP ]] || die 'Existing x-ui/bootstrap installation found'
    ! systemctl is-active --quiet docker.service || die 'Docker is already running; fresh host required'
    ! systemctl is-active --quiet firewalld.service || die 'firewalld already running'
    ! systemctl is-active --quiet nftables.service || die 'nftables service already running'
    # Ensure our Include starts in global context and has precedence over cloud-init.
    ! grep -Eisq '^[[:space:]]*Match[[:space:]]' /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*.conf 2>/dev/null ||
        die 'Existing SSH Match blocks require manual review'
    export DEBIAN_FRONTEND=noninteractive
    log 'Installing OS tools and security updates (no automatic reboot)'
    desktop_step apt-index
    apt-get update
    desktop_step apt-install
    apt-get -o Dpkg::Options::=--force-confold install -y openssh-server sudo ufw fail2ban python3 python3-systemd \
        unattended-upgrades ca-certificates curl openssl sqlite3 nftables iproute2 certbot
    desktop_step validate-access
    if ufw status | grep -q '^Status: active'; then die 'UFW already active; existing host refused'; fi
    check_ufw_rules
    ssh_check
    # Refuse an incorrect port rather than replacing socket-activated SSH settings.
    ss -H -ltn "sport = :$SSH_PORT" | grep -q . || die 'No TCP listener on --ssh-port'
    PREPARE_CONNECTION=${SSH_CONNECTION:-console}
    CLIENT_IP=${SSH_CONNECTION:-}
    CLIENT_IP=${CLIENT_IP%% *}
    CLIENT_IP=${CLIENT_IP:-127.0.0.1}
    python3 - "$SSH_CIDR" "$CLIENT_IP" <<'PY'
import ipaddress, sys
cidr, client = sys.argv[1:]
ip = ipaddress.ip_address(client)
if cidr != 'any' and ip not in ipaddress.ip_network(cidr, strict=True):
    raise SystemExit('SSH CIDR must be a canonical network including your current IP')
PY
    panel_sources_valid || die 'Invalid panel source list'
    install -d -m 700 "$STATE"
    tr -d '\r' < "$KEY_FILE" > "$STATE/admin.pub"
    [[ $(grep -c . "$STATE/admin.pub") == 1 ]] || die 'Exactly one public key required'
    grep -Eq '^(ssh-ed25519|sk-ssh-ed25519@openssh.com) [A-Za-z0-9+/=]+( .*)?$' "$STATE/admin.pub" || die 'Only a plain Ed25519 public key is accepted'
    ssh-keygen -lf "$STATE/admin.pub" >/dev/null || die 'Invalid public key'
    save_config
    # Preserve provider SSH configuration during package upgrades.
    desktop_step apt-upgrade
    apt-get -o Dpkg::Options::=--force-confold upgrade -y
    # Fail2ban needs the nft CLI, not nftables.service (which could flush UFW on boot).
    systemctl disable --now nftables.service
    cp -p /etc/ssh/sshd_config "$STATE/sshd_config.original"
    install -m 700 -- "${BASH_SOURCE[0]}" "$SELF"
    desktop_step admin-create
    useradd --create-home --shell /bin/bash --groups sudo "$ADMIN"
    # Permit only key login; sudo deliberately uses NOPASSWD for this locked account.
    passwd -l "$ADMIN" >/dev/null
    install -d -m 700 -o "$ADMIN" -g "$ADMIN" "/home/$ADMIN/.ssh"
    install -m 600 -o "$ADMIN" -g "$ADMIN" "$STATE/admin.pub" "/home/$ADMIN/.ssh/authorized_keys"
    printf '%s ALL=(ALL:ALL) NOPASSWD: ALL\n' "$ADMIN" > "/etc/sudoers.d/90-openviya-$ADMIN"
    chmod 440 "/etc/sudoers.d/90-openviya-$ADMIN"
    visudo -cf "/etc/sudoers.d/90-openviya-$ADMIN"
    install -d -m 755 /etc/ssh/sshd_config.d
    printf 'pending\n' > "$STATE/phase"
    desktop_step rollback
    install_rollback
    write_ssh prepare
    desktop_step firewall
    configure_firewall
    desktop_step prepare-done succeeded
    cat <<EOF

Prepared. KEEP THIS SSH SESSION OPEN. Within 20 minutes, from your computer:
  ssh -p $SSH_PORT $ADMIN@SERVER_IP
Then in that NEW session:
  sudo --preserve-env=SSH_CONNECTION $SELF finalize
No new login / finalize means automatic access rollback (UFW disabled).
If cloud firewall is present, allow SSH there before trying the new connection.
Panel HTTPS port: $PANEL_PORT (access mode: $PANEL_ACCESS).
EOF
}
validate_acme_options() {
    # Runs before installing Python on minimal VPS images.
    local label email_pattern='^[A-Za-z0-9.!#$%&*+/=?^_{|}~-]+@[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$'
    local -a labels
    [[ ${#PANEL_DOMAIN} -le 253 && $PANEL_DOMAIN == *.* && $PANEL_DOMAIN != *. &&
        ! $PANEL_DOMAIN =~ ^[0-9.]+$ ]] || die 'Provide --domain with one DNS name, not an IP or wildcard'
    IFS=. read -r -a labels <<< "$PANEL_DOMAIN"
    for label in "${labels[@]}"; do
        [[ $label =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] || die 'Invalid domain; use punycode for IDN'
    done
    [[ ! ${labels[-1]} =~ ^[0-9]+$ ]] || die 'Invalid domain suffix'
    [[ $ACME_EMAIL =~ $email_pattern && $ACME_EMAIL == *@*.* ]] || die 'Provide --acme-email with a valid account email'
}
require_acme() {
    [[ $TLS_MODE == letsencrypt ]] || die 'Legacy self-signed installation: run setup-letsencrypt --domain HOST --acme-email EMAIL'
    validate_acme_options
}
acme_preflight() {
    # Never stop an existing web server or VPN listener to get a certificate.
    [[ -z $(ss -H -ltn 'sport = :80') ]] || die 'TCP 80 is occupied; stop/reconfigure its owner before HTTP-01'
    python3 - "$PANEL_DOMAIN" <<'PY'
import ipaddress, socket, sys
addresses = {a[4][0] for a in socket.getaddrinfo(sys.argv[1], 80, type=socket.SOCK_STREAM)}
if not addresses or any(not ipaddress.ip_address(a).is_global for a in addresses):
    raise SystemExit('Domain must resolve to public addresses of this VPS; check A and AAAA')
print('HTTP-01 DNS addresses:', ', '.join(sorted(addresses)))
PY
}
request_certificate() {
    desktop_step tls-issue
    require_acme
    acme_preflight || die 'ACME preflight failed; check DNS and TCP 80'
    log "Requesting Let's Encrypt certificate for $PANEL_DOMAIN (HTTP-01 / TCP 80)"
    certbot certonly --standalone --preferred-challenges http --http-01-port 80 \
        --non-interactive --agree-tos --email "$ACME_EMAIL" \
        --server https://acme-v02.api.letsencrypt.org/directory \
        --cert-name "$ACME_NAME" --domain "$PANEL_DOMAIN" --key-type ecdsa \
        --keep-until-expiring --no-directory-hooks
}
validate_certificate() {
    local directory=$1 cert_public key_public
    [[ -s $directory/fullchain.pem && -s $directory/privkey.pem ]] || die 'Missing Certbot fullchain/key'
    openssl x509 -checkend 86400 -noout -in "$directory/fullchain.pem" || die 'Certificate validity is less than one day'
    openssl verify -purpose sslserver -verify_hostname "$PANEL_DOMAIN" \
        -untrusted "$directory/fullchain.pem" "$directory/fullchain.pem" || die 'Certificate chain or hostname verification failed'
    cert_public=$(openssl x509 -in "$directory/fullchain.pem" -pubkey -noout | openssl pkey -pubin -outform DER | sha256sum) || die 'Cannot read certificate public key'
    key_public=$(openssl pkey -in "$directory/privkey.pem" -pubout -outform DER | sha256sum) || die 'Cannot read private key'
    [[ $cert_public == "$key_public" ]] || die 'Certificate and private key do not match'
}
https_probe() {
    # Verify CA and hostname while connecting locally, including tunnel mode.
    local path=/
    if [[ -f $STATE/credentials ]]; then
        # shellcheck source=/dev/null
        source "$STATE/credentials"
        path=$PANEL_PATH
    fi
    curl --fail --silent --show-error --noproxy '*' --retry 10 --retry-connrefused --retry-delay 1 \
        --resolve "$PANEL_DOMAIN:$PANEL_PORT:127.0.0.1" --max-time 5 \
        "https://$PANEL_DOMAIN:$PANEL_PORT$path" -o /dev/null
}
deploy_tls() {
    desktop_step tls-deploy
    local lineage="/etc/letsencrypt/live/$ACME_NAME" work had_old=false running=false
    validate_certificate "$lineage"
    install -d -m 700 "$TLS_DIR"
    if cmp -s "$lineage/fullchain.pem" "$TLS_DIR/panel.crt" &&
        cmp -s "$lineage/privkey.pem" "$TLS_DIR/panel.key"; then return; fi
    work=$(mktemp -d "$STATE/tls-deploy.XXXXXXXX")
    if [[ -f $TLS_DIR/panel.crt && -f $TLS_DIR/panel.key ]]; then
        cp -p "$TLS_DIR/panel.crt" "$TLS_DIR/panel.key" "$work/"
        had_old=true
    else
        [[ ! -e $TLS_DIR/panel.crt && ! -e $TLS_DIR/panel.key ]] || die 'Incomplete active TLS pair; inspect TLS directory'
    fi
    if systemctl is-active --quiet x-ui.service; then running=true; fi
    install -m 600 "$lineage/privkey.pem" "$work/new.key"
    install -m 644 "$lineage/fullchain.pem" "$work/new.crt"
    # In-memory TLS stays valid until restart; restore both files on failure.
    if ! (mv -f "$work/new.key" "$TLS_DIR/panel.key" &&
        mv -f "$work/new.crt" "$TLS_DIR/panel.crt" &&
        { [[ $running == false ]] || { systemctl restart x-ui.service && https_probe; }; }); then
        if [[ $had_old == true ]]; then
            cp -p "$work/panel.key" "$work/panel.crt" "$TLS_DIR/"
            if [[ $running == true ]]; then systemctl restart x-ui.service || true; fi
        else
            rm -f "$TLS_DIR/panel.key" "$TLS_DIR/panel.crt"
        fi
        die "TLS deployment failed; previous pair restored if present. Inspect $work"
    fi
    find "$STATE" -maxdepth 1 -type d -name 'tls-deploy.*' ! -path "$work" -exec rm -rf -- {} +
    log "Deployed trusted TLS certificate for $PANEL_DOMAIN"
}
install_acme_renewal() {
    install -d -m 755 /etc/letsencrypt/renewal-hooks/deploy
    cat > /etc/letsencrypt/renewal-hooks/deploy/50-openviya-panel <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
[[ ${RENEWED_LINEAGE:-} == /etc/letsencrypt/live/openviya-panel ]] || exit 0
exec /usr/local/sbin/openviya-vps deploy-cert
EOF
    chmod 700 /etc/letsencrypt/renewal-hooks/deploy/50-openviya-panel
    # Retry a failed deployment on the next timer run, even when renewal is not due.
    install -d -m 755 /etc/systemd/system/certbot.service.d
    cat > /etc/systemd/system/certbot.service.d/90-openviya-deploy.conf <<EOF
[Service]
ExecStartPost=$SELF deploy-cert
EOF
    systemctl daemon-reload
    systemctl enable --now certbot.timer
}
create_tls() {
    require_acme
    request_certificate || die 'Certificate issuance failed; inspect DNS, TCP 80 and the Certbot log'
    deploy_tls
    install_acme_renewal
}
deploy_cert() {
    load_config
    require_acme
    [[ $(cat "$STATE/phase") == complete ]] || die 'Certificate deployment requires completed provisioning'
    deploy_tls
}
renew_cert() {
    load_config
    require_acme
    [[ $(cat "$STATE/phase") == complete ]] || die 'Renewal requires completed provisioning'
    acme_preflight
    # Certbot's hook runs as a separate process and takes the bootstrap lock.
    certbot renew --cert-name "$ACME_NAME" --non-interactive
    # Retry a failed deploy even if the CA certificate is already renewed.
    "$SELF" deploy-cert
}
setup_letsencrypt() {
    load_config
    local phase
    phase=$(cat "$STATE/phase")
    [[ $phase == complete || $phase == pending ]] || die 'Migration requires complete or pending state'
    while (($#)); do
        (($# >= 2)) || die "Missing value for $1"
        case $1 in
            --domain) PANEL_DOMAIN=${2,,} ;; --acme-email) ACME_EMAIL=$2 ;;
            *) die "Unknown TLS option: $1" ;;
        esac
        shift 2
    done
    TLS_MODE=letsencrypt
    validate_acme_options
    [[ $SSH_PORT != 80 && $PANEL_PORT != 80 ]] || die 'Existing SSH/panel uses ACME port 80'
    local p
    for p in ${VPN_TCP//,/ }; do [[ $p != 80 ]] || die 'Existing VPN uses ACME port 80'; done
    DEBIAN_FRONTEND=noninteractive apt-get install -y certbot
    acme_preflight
    ufw status | grep -q '^Status: active' || die 'UFW must be active before migration'
    ufw allow 80/tcp comment 'Lets Encrypt HTTP-01 renewal'
    # ACME failure leaves the working panel and saved settings intact.
    request_certificate || die 'Certificate issuance failed; active TLS and saved configuration retained'
    deploy_tls
    save_config
    install_acme_renewal
    if [[ -f $STATE/panel-configured ]]; then install_backup; fi
    log "Let's Encrypt configured. Domain: $PANEL_DOMAIN; panel port: $PANEL_PORT; access: $PANEL_ACCESS"
    printf 'For an SSH tunnel, map %s to 127.0.0.1 in your computer hosts file.\n' "$PANEL_DOMAIN"
    printf 'Run: sudo certbot renew --cert-name %s --dry-run\n' "$ACME_NAME"
}
install_panel() {
    local arch expected work password username base listen=127.0.0.1
    if [[ $PANEL_ACCESS == both ]]; then listen=0.0.0.0; fi
    case $(uname -m) in
        x86_64) arch=amd64; expected=bfda7921984e7a72555c38d4e861c7a8b67b3686b658608937de99682f1452d0 ;;
        aarch64|arm64) arch=arm64; expected=a867c65ce52ad49f6504dd4c9c395337dc92ba57ae478da4f1b7ddb624014ed5 ;;
        *) die 'Unsupported architecture' ;;
    esac
    work=$STATE/download
    install -d -m 700 "$work" /etc/x-ui /var/log/x-ui
    desktop_step tls-check
    create_tls
    if [[ ! -e $STATE/panel-configured ]]; then
        desktop_step panel-download
        log "Downloading pinned 3x-ui $VERSION ($arch)"
        curl --fail --location --proto '=https' --proto-redir '=https' --tlsv1.2 \
            --connect-timeout 15 --max-time 240 --retry 3 \
            "https://github.com/MHSanaei/3x-ui/releases/download/$VERSION/x-ui-linux-$arch.tar.gz" \
            -o "$work/release.tar.gz"
        desktop_step panel-verify
        printf '%s  %s\n' "$expected" "$work/release.tar.gz" | sha256sum --check --status || die 'Release SHA-256 mismatch'
        # Validate members and links BEFORE extraction, even for the pinned archive.
        python3 - "$work/release.tar.gz" <<'PY'
import pathlib, sys, tarfile
with tarfile.open(sys.argv[1]) as tar:
    for m in tar.getmembers():
        p = pathlib.PurePosixPath(m.name)
        if p.is_absolute() or '..' in p.parts or not p.parts or p.parts[0] != 'x-ui':
            raise SystemExit('Unsafe archive member: ' + m.name)
        if not (m.isfile() or m.isdir()):
            raise SystemExit('Unexpected archive link or device: ' + m.name)
PY
        tar -xzf "$work/release.tar.gz" --no-same-owner --no-same-permissions -C "$work"
        [[ -s $work/x-ui/x-ui && -s $work/x-ui/bin/xray-linux-$arch ]] || die 'Release layout mismatch'
        install -d -m 700 /usr/local/x-ui
        cp -a "$work/x-ui/." /usr/local/x-ui/
        chmod 700 /usr/local/x-ui /usr/local/x-ui/x-ui /usr/local/x-ui/bin
        find /usr/local/x-ui/bin -type f -name '*linux*' -exec chmod 700 {} +
        desktop_step panel-configure
        if [[ ! -f $STATE/credentials ]]; then
            username="ov_$(openssl rand -hex 6)"
            password=$(openssl rand -hex 24)
            base="/$(openssl rand -hex 18)/"
            { printf 'PANEL_USERNAME=%q\n' "$username"; printf 'PANEL_PASSWORD=%q\n' "$password"; printf 'PANEL_PATH=%q\n' "$base"; printf 'PANEL_PORT=%q\n' "$PANEL_PORT"; } > "$STATE/credentials"
        fi
        # shellcheck source=/dev/null
        source "$STATE/credentials"
        export XUI_DB_FOLDER=/etc/x-ui XUI_LOG_FOLDER=/var/log/x-ui XUI_BIN_FOLDER=/usr/local/x-ui/bin
        (cd /usr/local/x-ui && ./x-ui setting -port "$PANEL_PORT" -listenIP "$listen" \
            -username "$PANEL_USERNAME" -password "$PANEL_PASSWORD" -webBasePath "$PANEL_PATH") > "$STATE/panel-setup.log" 2>&1
        (cd /usr/local/x-ui && ./x-ui cert -webCert "$TLS_DIR/panel.crt" -webCertKey "$TLS_DIR/panel.key") >> "$STATE/panel-setup.log" 2>&1
        # There is no CLI flag for subscriptions. Modify only this pinned, stopped DB.
        # Verify the schema and read back: the upstream CLI can return 0 on errors.
        python3 - "$PANEL_PORT" "$PANEL_PATH" "$listen" <<'PY'
import sqlite3, sys
db = sqlite3.connect('/etc/x-ui/x-ui.db')
cols = {r[1] for r in db.execute('PRAGMA table_info(settings)')}
if not {'key', 'value'} <= cols:
    raise SystemExit('Unexpected settings schema; service has NOT been started')
for key, value in [('subEnable', 'false'), ('subJsonEnable', 'false'),
                   ('subClashEnable', 'false'), ('subListen', '127.0.0.1'),
                   ('sessionMaxAge', '60')]:
    db.execute('DELETE FROM settings WHERE key=?', (key,))
    db.execute('INSERT INTO settings (key,value) VALUES (?,?)', (key, value))
db.commit()
actual = dict(db.execute('SELECT key,value FROM settings'))
for key, value in [('webListen', sys.argv[3]), ('webPort', sys.argv[1]),
                   ('webBasePath', sys.argv[2]), ('subEnable', 'false'),
                   ('webCertFile', '/etc/x-ui/tls/panel.crt'),
                   ('webKeyFile', '/etc/x-ui/tls/panel.key')]:
    if actual.get(key) != value:
        raise SystemExit('Panel setting readback failed: ' + key)
if db.execute('SELECT COUNT(*) FROM users').fetchone()[0] != 1:
    raise SystemExit('Unexpected administrator count')
if db.execute('PRAGMA quick_check').fetchone()[0] != 'ok':
    raise SystemExit('Database integrity check failed')
db.close()
PY
        find /etc/x-ui -maxdepth 1 -type f -exec chmod 600 {} +
        touch "$STATE/panel-configured"
        printf '%s\n' "$VERSION" > "$STATE/version"
    fi
    cat > /etc/systemd/system/x-ui.service <<'EOF'
[Unit]
Description=3x-ui panel and Xray (OpenViya bootstrap)
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
User=root
WorkingDirectory=/usr/local/x-ui
Environment=XUI_DB_FOLDER=/etc/x-ui
Environment=XUI_LOG_FOLDER=/var/log/x-ui
Environment=XUI_BIN_FOLDER=/usr/local/x-ui/bin
Environment=XUI_ENABLE_FAIL2BAN=false
ExecStart=/usr/local/x-ui/x-ui
Restart=on-failure
RestartSec=5
UMask=0077
LimitNOFILE=65536
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=full
# The panel atomically writes Xray config and manages core/geo assets here.
# Keep the panel executable and the rest of /usr read-only.
ReadWritePaths=/etc/x-ui /usr/local/x-ui/bin
[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    desktop_step panel-start
    systemctl enable --now x-ui.service
}
enable_apt_timers() {
    local unit load_state repair_needed=false
    # Packages may have updated unit files since PID 1 last loaded them.
    systemctl daemon-reload
    for unit in apt-daily.service apt-daily-upgrade.service; do
        load_state=$(systemctl show "$unit" -p LoadState --value) || die "Cannot inspect $unit"
        if [[ $load_state == masked ]]; then
            # This bootstrap explicitly enables OS security updates. Some VPS
            # images mask these two stock services; never unmask unrelated units.
            log "Unmasking $unit for scheduled security updates"
            systemctl unmask "$unit"
            systemctl unmask --runtime "$unit"
            systemctl daemon-reload
            load_state=$(systemctl show "$unit" -p LoadState --value) || die "Cannot inspect $unit"
        fi
        case $load_state in
            loaded) ;;
            not-found) repair_needed=true ;;
            *)
                systemctl show "$unit" -p Id -p LoadState -p LoadError -p FragmentPath >&2 || true
                die "APT service $unit cannot load ($load_state); inspect its configuration"
                ;;
        esac
    done
    if [[ $repair_needed == true ]]; then
        log 'Restoring missing APT service files from the distribution package'
        DEBIAN_FRONTEND=noninteractive apt-get -o Dpkg::Options::=--force-confold install --reinstall -y apt
        systemctl daemon-reload
        for unit in apt-daily.service apt-daily-upgrade.service; do
            load_state=$(systemctl show "$unit" -p LoadState --value) || die "Cannot inspect $unit"
            [[ $load_state == loaded ]] || die "$unit still cannot load ($load_state); check dpkg path exclusions and local unit overrides"
        done
    fi
    if ! systemctl enable --now apt-daily.timer apt-daily-upgrade.timer; then
        systemctl show apt-daily.service apt-daily-upgrade.service -p Id -p LoadState -p LoadError -p FragmentPath >&2 || true
        systemctl --no-pager --full status apt-daily.timer apt-daily-upgrade.timer >&2 || true
        journalctl -b -u apt-daily.timer -u apt-daily-upgrade.timer -n 60 --no-pager >&2 || true
        die 'APT timers failed; inspect the diagnostics above. Keep SSH open and check the access rollback timer.'
    fi
}
configure_os() {
    desktop_step fail2ban
    log 'Configuring SSH jail, security updates and bounded logs'
    cat > /etc/fail2ban/jail.d/90-openviya.local <<EOF
[sshd]
enabled = true
backend = systemd
banaction = nftables-multiport
port = $SSH_PORT
maxretry = 5
findtime = 10m
bantime = 1h
ignoreip = 127.0.0.1/8 ::1 $CLIENT_IP
EOF
    fail2ban-client -t
    systemctl enable fail2ban.service
    systemctl restart fail2ban.service
    cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
    cat > /etc/apt/apt.conf.d/52openviya-security <<'EOF'
#clear Unattended-Upgrade::Allowed-Origins;
#clear Unattended-Upgrade::Origins-Pattern;
Unattended-Upgrade::Origins-Pattern {
    "origin=Ubuntu,archive=${distro_codename}-security";
    "origin=Debian,codename=${distro_codename}-security,label=Debian-Security";
};
Unattended-Upgrade::Automatic-Reboot "false";
EOF
    desktop_step security-updates
    enable_apt_timers
    desktop_step log-limits
    install -d -m 755 /etc/systemd/journald.conf.d
    cat > /etc/systemd/journald.conf.d/90-openviya.conf <<'EOF'
[Journal]
Storage=persistent
SystemMaxUse=200M
RuntimeMaxUse=50M
MaxRetentionSec=14day
EOF
    systemctl restart systemd-journald
    cat > /etc/sysctl.d/90-openviya.conf <<'EOF'
net.ipv4.conf.all.accept_redirects=0
net.ipv4.conf.default.accept_redirects=0
net.ipv4.conf.all.send_redirects=0
net.ipv4.conf.default.send_redirects=0
net.ipv4.conf.all.accept_source_route=0
net.ipv4.conf.default.accept_source_route=0
net.ipv6.conf.all.accept_redirects=0
net.ipv6.conf.default.accept_redirects=0
net.ipv6.conf.all.accept_source_route=0
net.ipv6.conf.default.accept_source_route=0
net.ipv4.conf.all.rp_filter=2
net.ipv4.conf.default.rp_filter=2
EOF
    sysctl -p /etc/sysctl.d/90-openviya.conf
}
install_backup() {
    install -d -m 700 /var/backups/3x-ui
    cat > /usr/local/sbin/openviya-xui-backup <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
exec 8>/run/lock/openviya-xui-backup.lock
flock -n 8 || exit 0
dir=$(mktemp -d /var/backups/3x-ui/.partial.XXXXXXXX)
trap 'rm -rf -- "$dir"' EXIT
sqlite3 /etc/x-ui/x-ui.db ".timeout 10000" ".backup '$dir/x-ui.db'"
[[ $(sqlite3 "$dir/x-ui.db" 'PRAGMA quick_check;') == ok ]]
cp /var/lib/openviya-vps-bootstrap/version "$dir/version"
openssl x509 -checkend 604800 -noout -in /etc/x-ui/tls/panel.crt >&2 || echo 'WARNING: panel TLS certificate expires in <7 days or is invalid' >&2
tar -czf "$dir/backup.tar.gz" -C "$dir" x-ui.db version \
    -C / etc/x-ui/tls etc/systemd/system/x-ui.service etc/ssh/sshd_config \
    etc/ssh/sshd_config.d etc/ufw etc/default/ufw etc/sysctl.d/90-openviya.conf \
    etc/letsencrypt
mv -- "$dir/backup.tar.gz" "/var/backups/3x-ui/backup-$(date -u +%Y%m%dT%H%M%S)-$$.tar.gz"
find /var/backups/3x-ui -maxdepth 1 -type f -name 'backup-*.tar.gz' -mtime +14 -delete
EOF
    chmod 700 /usr/local/sbin/openviya-xui-backup
    cat > /etc/systemd/system/openviya-xui-backup.service <<'EOF'
[Unit]
Description=Consistent local SQLite backup of 3x-ui (contains secrets)
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/openviya-xui-backup
UMask=0077
EOF
    cat > /etc/systemd/system/openviya-xui-backup.timer <<'EOF'
[Unit]
Description=Daily 3x-ui database backup
[Timer]
OnCalendar=*-*-* 03:30:00 UTC
RandomizedDelaySec=30m
Persistent=true
[Install]
WantedBy=timers.target
EOF
    systemctl daemon-reload
    systemctl enable --now openviya-xui-backup.timer
    systemctl start openviya-xui-backup.service
}
xray_health() {
    # Only used for initial provisioning, before the administrator enables 2FA.
    # Keep login secrets out of argv; verify the public CA and domain locally.
    PANEL_USERNAME="$PANEL_USERNAME" PANEL_PASSWORD="$PANEL_PASSWORD" \
        python3 - "$PANEL_PORT" "$PANEL_PATH" "$PANEL_DOMAIN" <<'PY'
import http.client, http.cookiejar, json, os, socket, ssl, sys, time, urllib.parse, urllib.request
port, path, domain = sys.argv[1:]
base = 'https://' + domain + ':' + port + path
class LocalHTTPSConnection(http.client.HTTPSConnection):
    def connect(self):
        self.sock = socket.create_connection(('127.0.0.1', self.port), timeout=self.timeout)
        self.sock = self._context.wrap_socket(self.sock, server_hostname=self.host)
class LocalHTTPSHandler(urllib.request.HTTPSHandler):
    def https_open(self, req):
        return self.do_open(LocalHTTPSConnection, req, context=ssl.create_default_context())
opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({}),
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
    LocalHTTPSHandler(),
)
with opener.open(base + 'csrf-token', timeout=5) as response:
    token = json.load(response)['obj']
data = urllib.parse.urlencode({'username': os.environ['PANEL_USERNAME'],
                              'password': os.environ['PANEL_PASSWORD']}).encode()
request = urllib.request.Request(base + 'login', data=data, headers={'X-CSRF-Token': token})
with opener.open(request, timeout=5) as response:
    if json.load(response).get('success') is not True:
        raise SystemExit('Panel login verification failed')
error = 'unknown'
for _ in range(15):
    with opener.open(base + 'panel/api/server/status', timeout=5) as response:
        status = json.load(response)
    core = (status.get('obj') or {}).get('xray') or {}
    if status.get('success') is True and core.get('state') == 'running':
        print('Authenticated panel API confirms Xray is running')
        break
    error = core.get('errorMsg') or core.get('state') or 'status response unavailable'
    time.sleep(2)
else:
    raise SystemExit('Xray health check failed: ' + str(error)[:500])
PY
}
panel_health() {
    # shellcheck source=/dev/null
    source "$STATE/credentials"
    https_probe
    python3 - "$PANEL_PORT" "$PANEL_ACCESS" <<'PY'
import subprocess, sys
port = sys.argv[1]
rows = subprocess.check_output(['ss', '-H', '-ltn', 'sport = :' + port], text=True).splitlines()
if not rows:
    raise SystemExit('Panel TCP listener is missing')
if sys.argv[2] == 'tunnel' and any(r.split()[3] != '127.0.0.1:' + port for r in rows):
    raise SystemExit('Panel is not exclusively bound to IPv4 loopback')
if sys.argv[2] == 'both' and not any(r.split()[3] in {'0.0.0.0:' + port, '[::]:' + port, '*:' + port} for r in rows):
    raise SystemExit('Public panel wildcard listener is missing')
PY
    ufw status | grep -q '^Status: active' || die 'Firewall is not active'
    systemctl is-active --quiet x-ui.service fail2ban.service
    xray_health
}
finalize() {
    desktop_step validate
    load_config
    require_acme
    [[ $(cat "$STATE/phase") == pending ]] || die 'Finalize requires pending state; see status / README'
    [[ ${SUDO_USER:-} == "$ADMIN" && -n ${SSH_CONNECTION:-} && $SSH_CONNECTION != "$PREPARE_CONNECTION" ]] ||
        die "Use a NEW SSH session as $ADMIN, then sudo --preserve-env=SSH_CONNECTION $SELF finalize"
    local remote_ip remote_port local_ip local_port
    read -r remote_ip remote_port local_ip local_port <<< "$SSH_CONNECTION"
    [[ $local_port == "$SSH_PORT" && -n $local_ip && -n $remote_port ]] || die 'Unexpected SSH connection/port'
    CLIENT_IP=$remote_ip
    systemctl is-active --quiet openviya-access-rollback.timer || die 'Rollback timer expired; inspect status'
    systemctl restart openviya-access-rollback.timer
    desktop_step ssh-hardening
    write_ssh finalize
    configure_os
    install_panel
    desktop_step backup
    install_backup
    desktop_step health
    panel_health
    [[ $(cat "$STATE/phase") == pending ]] || die 'Access rollback already ran; refusing completion'
    systemctl disable --now openviya-access-rollback.timer
    printf 'complete\n' > "$STATE/phase"
    desktop_step finalize-done succeeded
    cat <<EOF

Complete. Credentials: sudo cat $STATE/credentials
From your computer (keep this command running):
  ssh -p $SSH_PORT -N -o ExitOnForwardFailure=yes -L 127.0.0.1:20530:127.0.0.1:$PANEL_PORT $ADMIN@SERVER_IP
On your computer, add to hosts: 127.0.0.1 $PANEL_DOMAIN
Open https://$PANEL_DOMAIN:20530$PANEL_PATH (keep the domain for TLS verification).
TLS certificate: $TLS_DIR/panel.crt (Let's Encrypt full chain; automatic Certbot renewal).
Panel HTTPS port: $PANEL_PORT.
Access mode: $PANEL_ACCESS. Public panel source: $PANEL_CIDR (both mode only).
Public TCP: SSH $SSH_PORT (source: $SSH_CIDR), HTTP-01 80, VPN ${VPN_TCP:-none}
Public UDP: ${VPN_UDP:-none}. No VPN inbound/client has been created yet.
Backups: /var/backups/3x-ui (local only; copy encrypted backups off the VPS).
EOF
    if [[ $PANEL_ACCESS == both ]]; then log "Public HTTPS: https://$PANEL_DOMAIN:$PANEL_PORT$PANEL_PATH"; fi
    openssl x509 -noout -sha256 -fingerprint -dates -in "$TLS_DIR/panel.crt"
    if [[ -f /var/run/reboot-required ]]; then log 'OS requests a reboot. Reboot manually after checking access.'; fi
}
status() {
    [[ -f $STATE/phase ]] || die 'Not prepared'
    load_config
    printf 'Phase: '; cat "$STATE/phase"
    printf 'Panel HTTPS port: %s (access mode: %s)\n' "$PANEL_PORT" "$PANEL_ACCESS"
    printf 'TLS: %s; domain: %s\n' "$TLS_MODE" "${PANEL_DOMAIN:-legacy}"
    systemctl --no-pager list-timers openviya-access-rollback.timer openviya-xui-backup.timer certbot.timer
    ufw status verbose
    ss -lntup
    if [[ -f $TLS_DIR/panel.crt ]]; then
        openssl x509 -noout -sha256 -fingerprint -dates -ext subjectAltName -in "$TLS_DIR/panel.crt"
        openssl x509 -checkend 604800 -noout -in "$TLS_DIR/panel.crt"
    fi
}
json_escape() {
    local value=$1
    value=${value//\\/\\\\}
    value=${value//\"/\\\"}
    value=${value//$'\r'/\\r}
    value=${value//$'\n'/\\n}
    printf '%s' "$value"
}
read_phase() {
    local value=none
    if [[ -f $STATE/phase && ! -L $STATE/phase ]]; then
        IFS= read -r value < "$STATE/phase" || true
        case $value in pending|complete|rolled-back) ;; *) value=unknown ;; esac
    elif [[ -e $STATE ]]; then
        value=unknown
    fi
    printf '%s' "$value"
}
phase_is_fresh() { [[ ${1:-} == none ]]; }
inspection_phase_label() {
    if phase_is_fresh "${1:-}"; then printf 'fresh'; else printf '%s' "${1:-unknown}"; fi
}
inspection_fresh_value() {
    [[ ${1:-} == none && ${2:-false} == true && ${3:-false} == true ]]
}
unit_job_queued() {
    local job=${1:-}
    [[ $job =~ ^[1-9][0-9]*$ || $job =~ ^/org/freedesktop/systemd1/job/[1-9][0-9]*$ ]]
}
rollback_active() {
    command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet openviya-access-rollback.timer
}
machine_error() {
    local message=$1
    printf '{"schemaVersion":1,"supported":false,"fresh":false,"phase":"%s","rollbackActive":false,"message":"%s"}\n' \
        "$(json_escape "$(read_phase)")" "$(json_escape "$message")"
}
desktop_inspect() {
    local admin= ssh_port= os= version= arch= supported=false fresh=true phase=none rollback=false message='Fresh server checks passed.'
    while (($#)); do
        (($# >= 2)) || { machine_error 'Expected --admin USER --ssh-port PORT.'; return 2; }
        case $1 in
            --admin) admin=$2 ;;
            --ssh-port) ssh_port=$2 ;;
            *) machine_error 'Unknown inspection option.'; return 2 ;;
        esac
        shift 2
    done
    if [[ ! $admin =~ ^[a-z][a-z0-9_-]{0,30}$ || $admin == root ]] || ! port_valid "$ssh_port"; then
        machine_error 'Admin name or SSH port is invalid.'; return 2
    fi
    phase=$(read_phase)
    if [[ -r /etc/os-release ]]; then
        while IFS='=' read -r key value; do
            case $key in ID) os=${value#\"}; os=${os%\"} ;; VERSION_ID) version=${value#\"}; version=${version%\"} ;; esac
        done < /etc/os-release
    fi
    case $(uname -m 2>/dev/null || true) in x86_64) arch=amd64 ;; aarch64|arm64) arch=arm64 ;; *) arch=unknown ;; esac
    case "$os:$version:$arch" in ubuntu:26.04:amd64|ubuntu:26.04:arm64|ubuntu:24.04:amd64|ubuntu:24.04:arm64|debian:12:amd64|debian:12:arm64|debian:13:amd64|debian:13:arm64) supported=true ;; *) supported=false; fresh=false; message='Operating system, version, or architecture is unsupported.' ;; esac
    [[ -d /run/systemd/system ]] || { supported=false; fresh=false; message='A running systemd host is required.'; }
    if ! phase_is_fresh "$phase"; then fresh=false; message='Bootstrap state already exists; inspect the current phase before continuing.'; fi
    [[ ! -e $STATE && ! -e /etc/x-ui && ! -e /usr/local/x-ui && ! -e /etc/systemd/system/x-ui.service && ! -e $SSH_DROP ]] || { fresh=false; message='Existing bootstrap or panel files were detected.'; }
    ! id "$admin" &>/dev/null || { fresh=false; message='The requested admin user already exists.'; }
    ! getent group "$admin" >/dev/null 2>&1 || { fresh=false; message='The requested admin name conflicts with an existing group.'; }
    if command -v systemctl >/dev/null 2>&1; then
        for service in docker.service firewalld.service nftables.service; do
            if systemctl is-active --quiet "$service"; then fresh=false; message='Docker or an existing firewall service is active.'; break; fi
        done
    else
        supported=false; fresh=false; message='systemctl is unavailable.'
    fi
    if command -v ufw >/dev/null 2>&1; then
        local ufw_status ufw_rules
        ufw_status=$(ufw status 2>/dev/null) || { fresh=false; message='Could not inspect UFW safely.'; ufw_status=''; }
        if grep -q '^Status: active' <<< "$ufw_status"; then fresh=false; message='UFW is already active.'; fi
        ufw_rules=$(ufw show added 2>/dev/null) || { fresh=false; message='Could not inspect UFW rules safely.'; ufw_rules='unreadable'; }
        ufw_rules=$(printf '%s\n' "$ufw_rules" | sed '/^Added user rules/d; /^(None)$/d; /^[[:space:]]*$/d')
        [[ -z $ufw_rules ]] || { fresh=false; message='Existing UFW rules were detected.'; }
    fi
    if command -v nft >/dev/null 2>&1; then
        local nft_rules
        nft_rules=$(nft list ruleset 2>/dev/null) || { fresh=false; message='Could not inspect nftables safely.'; nft_rules='unreadable'; }
        [[ -z ${nft_rules//[$' \t\r\n']/} ]] || { fresh=false; message='Existing nftables rules were detected.'; }
    fi
    for save_cmd in iptables-save ip6tables-save; do
        if command -v "$save_cmd" >/dev/null 2>&1; then
            local saved
            saved=$("$save_cmd" 2>/dev/null) || { fresh=false; message='Could not inspect packet-filter rules safely.'; saved='unreadable'; }
            if grep -Eq '^-A |^:[A-Z0-9_-]+ (DROP|REJECT) ' <<< "$saved"; then fresh=false; message='Existing packet-filter rules were detected.'; fi
        fi
    done
    local match_file
    for match_file in /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*.conf; do
        [[ -f $match_file ]] || continue
        if grep -Eisq '^[[:space:]]*Match[[:space:]]' "$match_file" 2>/dev/null; then fresh=false; message='Existing SSH Match blocks require manual review.'; fi
    done
    if command -v ss >/dev/null 2>&1; then
        ss -H -ltn "sport = :$ssh_port" 2>/dev/null | grep -q . || { fresh=false; message='No SSH TCP listener was found on the requested port.'; }
    else
        fresh=false; message='Cannot verify the SSH listener because ss is unavailable.'
    fi
    if rollback_active; then rollback=true; fi
    if ! inspection_fresh_value "$phase" "$supported" "$fresh"; then fresh=false; fi
    printf '{"schemaVersion":1,"os":"%s","version":"%s","architecture":"%s","supported":%s,"fresh":%s,"phase":"%s","rollbackActive":%s,"message":"%s"}\n' \
        "$(json_escape "$os")" "$(json_escape "$version")" "$(json_escape "$arch")" "$supported" "$fresh" \
        "$(json_escape "$(inspection_phase_label "$phase")")" "$rollback" "$(json_escape "$message")"
}
parse_machine_args() {
    local raw=${1:-}
    if [[ $raw =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
        MACHINE_RUN_N=${raw//-/}
    elif [[ $raw =~ ^[0-9a-fA-F]{32}$ ]]; then
        MACHINE_RUN_N=$raw
    else
        return 1
    fi
    MACHINE_RUN_N=${MACHINE_RUN_N,,}
    MACHINE_RUN_ID=$MACHINE_RUN_N
}
unit_for() { printf 'openviya-desktop-%s-%s' "$MACHINE_RUN_N" "$1"; }
secure_job_script() {
    local job_dir="$DESKTOP_STATE/$MACHINE_RUN_N" script="$DESKTOP_STATE/$MACHINE_RUN_N/bootstrap.sh"
    [[ -d $DESKTOP_STATE && ! -L $DESKTOP_STATE && $(stat -c '%u:%g:%a' "$DESKTOP_STATE" 2>/dev/null) == 0:0:700 &&
        -d $job_dir && ! -L $job_dir && $(stat -c '%u:%g:%a' "$job_dir" 2>/dev/null) == 0:0:700 &&
        -f $script && ! -L $script && $(stat -c '%u:%g:%a' "$script" 2>/dev/null) == 0:0:700 ]] || return 1
    printf '%s' "$script"
}
job_status() {
    local stage=${1:-} unit load active substate queued_job result exit_code=-1 phase rollback=false state=missing message='No desktop provisioning job was found.'
    local status_text= step= step_state= signature= status_code= extra=
    if [[ $stage != prepare && $stage != finalize ]]; then machine_error 'Stage must be prepare or finalize.'; return 2; fi
    unit=$(unit_for "$stage")
    phase=$(read_phase)
    if command -v systemctl >/dev/null 2>&1; then
        load=$(systemctl show "$unit.service" -p LoadState --value 2>/dev/null || true)
        if [[ $load == loaded ]]; then
            status_text=$(systemctl show "$unit.service" -p StatusText --value 2>/dev/null || true)
            IFS=: read -r signature step step_state status_code extra <<< "$status_text"
            if [[ $signature != openviya-v1 || -n $extra ]] || ! desktop_step_valid "$step" ||
                [[ $step_state != running && $step_state != succeeded && $step_state != failed ]] ||
                [[ ! $status_code =~ ^[0-9]{1,3}$ ]] || ((10#${status_code:-0} > 255)); then
                step=; step_state=
            fi
            active=$(systemctl show "$unit.service" -p ActiveState --value 2>/dev/null || true)
            substate=$(systemctl show "$unit.service" -p SubState --value 2>/dev/null || true)
            queued_job=$(systemctl show "$unit.service" -p Job --value 2>/dev/null || true)
            result=$(systemctl show "$unit.service" -p Result --value 2>/dev/null || true)
            exit_code=$(systemctl show "$unit.service" -p ExecMainStatus --value 2>/dev/null || true)
            [[ $exit_code =~ ^[0-9]+$ ]] || exit_code=-1
            if unit_job_queued "$queued_job" || [[ $active == activating || $active == reloading || $active == deactivating || ( $active == active && $substate != exited ) ]]; then
                state=running; exit_code=-1; message='Provisioning job is running.'
            elif [[ $result == success && $exit_code == 0 && ( $stage == prepare && ( $phase == pending || $phase == complete ) || $stage == finalize && $phase == complete ) ]]; then
                state=succeeded; message='Provisioning job completed.'
            else
                state=failed; message='Provisioning job did not complete successfully; review the server through its recovery channel.'
            fi
        fi
    fi
    if rollback_active; then rollback=true; fi
    printf '{"schemaVersion":1,"state":"%s","exitCode":%s,"phase":"%s","rollbackActive":%s,"message":"%s","step":"%s","stepState":"%s"}\n' \
        "$state" "$exit_code" "$(json_escape "$phase")" "$rollback" "$(json_escape "$message")" "$step" "$step_state"
}
launch_machine_job() {
    local stage=$1 capture=${2:-} sudo_user=${3:-} script unit load launch_lock
    shift 3
    script=$(secure_job_script) || { machine_error 'Secure root-owned desktop job script is missing.'; return 1; }
    unit=$(unit_for "$stage")
    launch_lock="$DESKTOP_STATE/$MACHINE_RUN_N/launcher.lock"
    exec 8>"$launch_lock"
    flock -n 8 || { job_status "$stage"; return 0; }
    load=$(systemctl show "$unit.service" -p LoadState --value 2>/dev/null || true)
    if [[ $load == loaded ]]; then job_status "$stage"; return 0; fi
    if [[ $stage == prepare ]]; then
        systemd-run --no-block --quiet --unit="$unit" --property=Type=oneshot --property=RemainAfterExit=yes --property=UMask=0077 --property=NotifyAccess=all \
            --setenv="SSH_CONNECTION=$capture" "$script" prepare "$@" >/dev/null 2>&1 || {
                load=$(systemctl show "$unit.service" -p LoadState --value 2>/dev/null || true)
                [[ $load == loaded ]] || { printf '{"schemaVersion":1,"state":"failed","exitCode":-1,"phase":"%s","rollbackActive":false,"message":"Could not launch the provisioning job."}\n' "$(json_escape "$(read_phase)")"; return 1; }
            }
    else
        systemd-run --no-block --quiet --unit="$unit" --property=Type=oneshot --property=RemainAfterExit=yes --property=UMask=0077 --property=NotifyAccess=all \
            --setenv="SSH_CONNECTION=$capture" --setenv="SUDO_USER=$sudo_user" "$script" finalize >/dev/null 2>&1 || {
                load=$(systemctl show "$unit.service" -p LoadState --value 2>/dev/null || true)
                [[ $load == loaded ]] || { printf '{"schemaVersion":1,"state":"failed","exitCode":-1,"phase":"%s","rollbackActive":true,"message":"Could not launch the finalize job."}\n' "$(json_escape "$(read_phase)")"; return 1; }
            }
    fi
    job_status "$stage"
}
desktop_prepare() {
    (($# >= 2)) && [[ $1 == --run-id ]] && parse_machine_args "$2" || { machine_error 'Expected --run-id GUID followed by prepare options.'; return 2; }
    local run_id=$MACHINE_RUN_ID; shift 2
    [[ -n ${SSH_CONNECTION:-} ]] || { machine_error 'desktop-prepare must be started from the initial SSH session.'; return 2; }
    [[ $(read_phase) == none ]] || { machine_error 'Existing bootstrap state cannot be overwritten; use the matching current phase.'; return 2; }
    launch_machine_job prepare "$SSH_CONNECTION" '' "$@"
}
desktop_finalize() {
    (($# == 2)) && [[ $1 == --run-id ]] && parse_machine_args "$2" || { machine_error 'Expected --run-id GUID.'; return 2; }
    load_config
    [[ $(read_phase) == pending && ${SUDO_USER:-} == "$ADMIN" && -n ${SSH_CONNECTION:-} && $SSH_CONNECTION != "$PREPARE_CONNECTION" ]] || {
        machine_error 'Finalize requires the created admin, a new SSH session, pending state, and the active rollback timer.'; return 2;
    }
    local remote_ip remote_port local_ip local_port
    read -r remote_ip remote_port local_ip local_port <<< "$SSH_CONNECTION"
    [[ -n $remote_ip && -n $remote_port && $local_port == "$SSH_PORT" ]] || { machine_error 'SSH connection did not match the configured port.'; return 2; }
    systemctl is-active --quiet openviya-access-rollback.timer || { machine_error 'The access rollback timer is not active.'; return 2; }
    local run_id=$MACHINE_RUN_ID
    shift 2
    (($# == 0)) || { machine_error 'desktop-finalize accepts no extra options.'; return 2; }
    launch_machine_job finalize "$SSH_CONNECTION" "$SUDO_USER"
}
desktop_job_status() {
    (($# == 4)) && [[ $1 == --run-id && $3 == --stage ]] && parse_machine_args "$2" || { machine_error 'Expected --run-id GUID --stage prepare|finalize.'; return 2; }
    local run_id=$MACHINE_RUN_ID
    job_status "$4"
}
desktop_credentials() {
    local phase
    phase=$(read_phase)
    [[ $phase == complete && -f $STATE/config && ! -L $STATE/config && $(stat -c '%u:%a' "$STATE/config" 2>/dev/null) == 0:600 &&
        -f $STATE/credentials && ! -L $STATE/credentials && $(stat -c '%u:%a' "$STATE/credentials" 2>/dev/null) == 0:600 ]] || {
        printf '{"schemaVersion":1,"phase":"%s","message":"Credentials are available only after verified completion."}\n' "$(json_escape "$phase")"; return 1;
    }
    load_config
    # This root-only file is generated by printf %q; source it only after ownership/mode checks.
    # shellcheck source=/dev/null
    source "$STATE/credentials"
    [[ -n ${PANEL_USERNAME:-} && -n ${PANEL_PASSWORD:-} && -n ${PANEL_PATH:-} && ${PANEL_PORT:-} =~ ^[0-9]+$ && -n ${PANEL_DOMAIN:-} ]] || {
        printf '{"schemaVersion":1,"phase":"complete","message":"Saved credentials are incomplete."}\n'; return 1;
    }
    PANEL_USERNAME="$PANEL_USERNAME" PANEL_PASSWORD="$PANEL_PASSWORD" PANEL_PATH="$PANEL_PATH" PANEL_DOMAIN="$PANEL_DOMAIN" \
        PANEL_PORT="$PANEL_PORT" PANEL_ACCESS="$PANEL_ACCESS" TLS_MODE="$TLS_MODE" python3 - <<'PY'
import json, os
print(json.dumps({
    'schemaVersion': 1,
    'username': os.environ['PANEL_USERNAME'],
    'password': os.environ['PANEL_PASSWORD'],
    'path': os.environ['PANEL_PATH'],
    'domain': os.environ['PANEL_DOMAIN'],
    'port': int(os.environ['PANEL_PORT']),
    'access': os.environ['PANEL_ACCESS'],
    'tlsMode': os.environ['TLS_MODE'],
    'phase': 'complete',
}, separators=(',', ':')))
PY
}
main() {
    local command=${1:-help}
    if (($#)); then shift; fi
    case $command in help|--help|-h) usage; return ;; esac
    [[ $EUID == 0 ]] || die 'Run as root (or sudo)'
    export LC_ALL=C
    # Rollback must remain callable if a provisioning process hangs with the lock.
    if [[ $command != rollback && $command != renew-cert && $command != status && $command != desktop-inspect &&
        $command != desktop-prepare && $command != desktop-finalize && $command != desktop-job-status && $command != desktop-credentials ]]; then
        exec 9>/run/lock/openviya-vps.lock
        flock -n 9 || die 'Another bootstrap process is running'
    fi
    trap 'desktop_error "$?" "$LINENO"' ERR
    case $command in desktop-inspect|desktop-prepare|desktop-finalize|desktop-job-status|desktop-credentials) trap - ERR ;; esac
    case $command in
        prepare) prepare "$@" ;; finalize) (($# == 0)) || die 'No finalize options'; finalize ;;
        rollback) rollback ;; status) status ;; renew-cert) renew_cert ;;
        desktop-inspect) desktop_inspect "$@" ;; desktop-prepare) desktop_prepare "$@" ;;
        desktop-finalize) desktop_finalize "$@" ;; desktop-job-status) desktop_job_status "$@" ;;
        desktop-credentials) (($# == 0)) || { machine_error 'No credential options are accepted.'; return 2; }; desktop_credentials ;;
        setup-letsencrypt) setup_letsencrypt "$@" ;; deploy-cert) deploy_cert ;;
        *) die "Unknown command: $command" ;;
    esac
}
if [[ ${BASH_SOURCE[0]} == "$0" ]]; then main "$@"; fi
