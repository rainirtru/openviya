#!/usr/bin/env bash
# Isolated state-transition tests; never runs real systemctl or apt-get.
set -Eeuo pipefail
cd /work/ops/vps
# shellcheck source=/dev/null
source ./bootstrap-vps.sh
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
# shellcheck disable=SC2329
systemctl() {
    printf '%s\n' "$*" >> "$work/systemctl.log"
    case $1 in
        show)
            if [[ $* == *--value* ]]; then cat "$work/$2.state"; fi
            ;;
        unmask)
            if [[ ! -e $work/stuck-mask ]]; then
                case ${!#} in
                    apt-daily.service|apt-daily-upgrade.service) printf 'loaded\n' > "$work/${!#}.state" ;;
                    *) die 'Unmask attempted for an unrelated service' ;;
                esac
            fi
            ;;
        enable) if [[ -e $work/fail-timer ]]; then return 1; fi ;;
    esac
    return 0
}
# shellcheck disable=SC2329
apt-get() {
    printf '%s\n' "$*" >> "$work/apt.log"
    printf 'loaded\n' > "$work/apt-daily.service.state"
    printf 'loaded\n' > "$work/apt-daily-upgrade.service.state"
}
# shellcheck disable=SC2329
journalctl() { :; }
printf 'loaded\n' > "$work/apt-daily.service.state"
printf 'loaded\n' > "$work/apt-daily-upgrade.service.state"
enable_apt_timers
[[ ! -e $work/apt.log ]]
printf 'not-found\n' > "$work/apt-daily.service.state"
enable_apt_timers
grep -Fx -- '-o Dpkg::Options::=--force-confold install --reinstall -y apt' "$work/apt.log" >/dev/null
rm "$work/apt.log"
printf 'masked\n' > "$work/apt-daily.service.state"
printf 'masked\n' > "$work/apt-daily-upgrade.service.state"
enable_apt_timers
grep -Fx 'unmask apt-daily.service' "$work/systemctl.log" >/dev/null
grep -Fx 'unmask apt-daily-upgrade.service' "$work/systemctl.log" >/dev/null
[[ ! -e $work/apt.log ]]
touch "$work/stuck-mask"
printf 'masked\n' > "$work/apt-daily.service.state"
if (enable_apt_timers) >/dev/null 2>&1; then die 'A service that remained masked was accepted'; fi
[[ ! -e $work/apt.log ]]
printf 'bad-setting\n' > "$work/apt-daily.service.state"
if (enable_apt_timers) >/dev/null 2>&1; then die 'Invalid service configuration was accepted'; fi
printf 'loaded\n' > "$work/apt-daily.service.state"
touch "$work/fail-timer"
if (enable_apt_timers) >/dev/null 2>&1; then die 'Failed timer startup was accepted'; fi
printf 'APT timer checks passed: loaded, missing/reinstalled, both services unmasked, persistent mask/config/start failures refused.\n'
