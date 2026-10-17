#!/usr/bin/env bash
# Exercises launch/status protocol with mock systemctl/systemd-run in a private temp directory.
# No real system manager, host paths, network, or provisioning command is called.
set -Eeuo pipefail
script=${1:?Pass the bootstrap script path}
source "$script"

fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/desktop/0123456789abcdef0123456789abcdef"
export MOCK_SYSTEMD_STATE="$fixture/systemd-state"
export MOCK_SYSTEMD_LOG="$fixture/systemd-run-args"
export MOCK_ROLLBACK=inactive
export MOCK_PHASE=fresh
export DESKTOP_STATE="$fixture/desktop"
export PATH="$fixture/bin:$PATH"
MACHINE_RUN_ID=0123456789abcdef0123456789abcdef
MACHINE_RUN_N=$MACHINE_RUN_ID

cat > "$fixture/bin/systemctl" <<'MOCK'
#!/usr/bin/env bash
set -Eeuo pipefail
if [[ ${1:-} == is-active ]]; then
    [[ ${MOCK_ROLLBACK:-inactive} == active ]] && exit 0 || exit 3
fi
[[ ${1:-} == show ]] || exit 2
property=${4#-p}
property=${property# }
if [[ ! -f $MOCK_SYSTEMD_STATE ]]; then
    [[ $property == LoadState ]] && printf 'not-found\n'
    exit 0
fi
awk -F= -v key="$property" '$1 == key { sub(/^[^=]*=/, ""); print; found=1 } END { if (!found) print "" }' "$MOCK_SYSTEMD_STATE"
MOCK

cat > "$fixture/bin/systemd-run" <<'MOCK'
#!/usr/bin/env bash
set -Eeuo pipefail
printf '%s\n' "$@" >> "$MOCK_SYSTEMD_LOG"
cat > "$MOCK_SYSTEMD_STATE" <<'STATE'
LoadState=loaded
ActiveState=inactive
SubState=dead
Job=73
Result=success
ExecMainStatus=0
STATE
MOCK

cat > "$fixture/bin/flock" <<'MOCK'
#!/usr/bin/env bash
exit 0
MOCK
chmod +x "$fixture/bin/systemctl" "$fixture/bin/systemd-run" "$fixture/bin/flock"

# Bypass filesystem ownership checks using a controlled local fixture path.
secure_job_script() { printf '%s' "$fixture/bootstrap.sh"; }
read_phase() { printf '%s' "$MOCK_PHASE"; }
rollback_active() { [[ $MOCK_ROLLBACK == active ]]; }

out=$(launch_machine_job prepare '192.0.2.10 50000 192.0.2.20 22' '' --admin vpsadmin --ssh-port 22)
[[ $out == *'"state":"running"'* ]] || { printf 'queued launch must report running: %s\n' "$out" >&2; exit 1; }
grep -qx -- '--no-block' "$MOCK_SYSTEMD_LOG" || { printf 'launcher omitted --no-block\n' >&2; exit 1; }

# Repeating a request for the same loaded unit must not start a second systemd job.
out=$(launch_machine_job prepare '192.0.2.10 50000 192.0.2.20 22' '' --admin vpsadmin --ssh-port 22)
[[ $out == *'"state":"running"'* ]]
[[ $(wc -l < "$MOCK_SYSTEMD_LOG") -gt 1 ]]
[[ $(grep -c -- '--no-block' "$MOCK_SYSTEMD_LOG") == 1 ]]

# A unit queued but not yet active is running; inactive/dead with a nonzero Job is not a false failure.
cat > "$MOCK_SYSTEMD_STATE" <<'STATE'
LoadState=loaded
ActiveState=inactive
SubState=dead
Job=/org/freedesktop/systemd1/job/91
Result=success
ExecMainStatus=0
STATE
out=$(job_status prepare)
[[ $out == *'"state":"running"'* ]]

# A service that has left the queue and is executing remains running as well.
cat > "$MOCK_SYSTEMD_STATE" <<'STATE'
LoadState=loaded
ActiveState=activating
SubState=start
Job=0
Result=success
ExecMainStatus=0
STATE
out=$(job_status prepare)
[[ $out == *'"state":"running"'* ]]

# Missing unit stays missing even when the persistent bootstrap phase is pending.
rm -f "$MOCK_SYSTEMD_STATE"
MOCK_PHASE=pending
out=$(job_status prepare)
[[ $out == *'"state":"missing"'* && $out == *'"phase":"pending"'* ]]

# Successful oneshot remains active/exited after completion and must not look like a running job.
cat > "$MOCK_SYSTEMD_STATE" <<'STATE'
LoadState=loaded
ActiveState=active
SubState=exited
Job=0
Result=success
ExecMainStatus=0
STATE
out=$(job_status prepare)
[[ $out == *'"state":"succeeded"'* && $out == *'"exitCode":0'* ]]
MOCK_PHASE=complete
out=$(job_status finalize)
[[ $out == *'"state":"succeeded"'* ]]

# Retained failed units stay failed with their numeric exit status.
cat > "$MOCK_SYSTEMD_STATE" <<'STATE'
LoadState=loaded
ActiveState=failed
SubState=failed
Job=0
Result=exit-code
ExecMainStatus=7
STATE
MOCK_PHASE=pending
out=$(job_status prepare)
[[ $out == *'"state":"failed"'* && $out == *'"exitCode":7'* ]]

# Only fixed progress identifiers may cross the machine interface.
printf 'StatusText=openviya-v1:apt-install:failed:17\n' >> "$MOCK_SYSTEMD_STATE"
out=$(job_status prepare)
[[ $out == *'"step":"apt-install"'* && $out == *'"stepState":"failed"'* ]]
sed -i '/^StatusText=/d' "$MOCK_SYSTEMD_STATE"
printf 'StatusText=openviya-v1:FAKE-PRIVATE-OUTPUT:failed:17\n' >> "$MOCK_SYSTEMD_STATE"
out=$(job_status prepare)
[[ $out == *'"step":""'* && $out != *'FAKE-PRIVATE-OUTPUT'* ]]

export MOCK_NOTIFY_LOG="$fixture/notify-args"
cat > "$fixture/bin/systemd-notify" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$MOCK_NOTIFY_LOG"
MOCK
chmod +x "$fixture/bin/systemd-notify"
export NOTIFY_SOCKET=mock-only
desktop_step apt-install
grep -qx -- '--status=openviya-v1:apt-install:running:0' "$MOCK_NOTIFY_LOG"
desktop_step 'FAKE-PRIVATE-OUTPUT'
[[ $DESKTOP_CURRENT_STEP == apt-install ]]
! grep -q 'FAKE-PRIVATE-OUTPUT' "$MOCK_NOTIFY_LOG"
desktop_error 17 99 2>/dev/null
grep -qx -- '--status=openviya-v1:apt-install:failed:17' "$MOCK_NOTIFY_LOG"
grep -qx -- '--property=NotifyAccess=all' "$MOCK_SYSTEMD_LOG"

printf 'Mocked machine job protocol and safe progress checks passed; real systemd/bootstrap were not invoked.\n'
