#!/usr/bin/env bash
# Real UFW preflight regression; changes only a disposable container's rule files.
set -Eeuo pipefail
[[ -f /.dockerenv && ${OPENVIYA_DISPOSABLE_TEST:-} == yes ]] || exit 1
cd /work/ops/vps
# shellcheck source=/dev/null
source ./bootstrap-vps.sh
export LC_ALL=C
bash -n bootstrap-vps.sh
shellcheck bootstrap-vps.sh tests/verify-ufw.sh
check_ufw_rules
ufw allow 22/tcp >/dev/null
before=$(sha256sum /etc/ufw/user.rules /etc/ufw/user6.rules)
if (check_ufw_rules) > /tmp/ufw-refusal.log 2>&1; then
    die 'Existing SSH rule was incorrectly accepted as empty'
fi
grep -F 'Existing UFW rules refused' /tmp/ufw-refusal.log >/dev/null
[[ $(sha256sum /etc/ufw/user.rules /etc/ufw/user6.rules) == "$before" ]]
ufw --force delete allow 22/tcp >/dev/null
check_ufw_rules
# A failed inspection must also refuse provisioning, even with empty stdout.
# shellcheck disable=SC2329
if (ufw() { return 1; }; check_ufw_rules) >/dev/null 2>&1; then
    die 'Failed UFW inspection was accepted'
fi
printf 'UFW preflight passed: clean (None), genuine rule retained, empty after removal, inspection failure refused.\n'
