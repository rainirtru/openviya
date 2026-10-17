#!/usr/bin/env bash
# Actual OpenSSH validation in a disposable container; never run on a VPS.
set -Eeuo pipefail
[[ -f /.dockerenv && ${OPENVIYA_DISPOSABLE_TEST:-} == yes ]] || exit 1
cd /work/ops/vps
# shellcheck source=/dev/null
source ./bootstrap-vps.sh
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
ssh-keygen -A >/dev/null
# Fail if the directory is in use/nonempty rather than recursively deleting it.
if [[ -d /run/sshd ]]; then rmdir -- /run/sshd; fi
if /usr/sbin/sshd -t > "$work/sshd.log" 2>&1; then
    die 'Expected sshd validation to require its runtime directory'
fi
grep -F 'Missing privilege separation directory: /run/sshd' "$work/sshd.log" >/dev/null
ssh_check
[[ $(stat -c '%u:%g:%a' /run/sshd) == 0:0:755 ]]
ssh_check
[[ $(stat -c '%u:%g:%a' /run/sshd) == 0:0:755 ]]
printf 'OpenSSH runtime checks passed: missing directory reproduced, created with safe ownership/mode, repeat validation passed.\n'
