#!/usr/bin/env bash
# Runs only pure helpers after sourcing definitions; it never calls main or reads/writes host state.
set -Eeuo pipefail
script=${1:?Pass the bootstrap script path}
source "$script"

for sources in any '192.0.2.1/32' $'192.0.2.1/32\n2001:db8::1/128'; do
    PANEL_CIDR=$sources
    panel_sources_valid
done
for sources in '' $'any\n192.0.2.1/32' '192.0.2.1,192.0.2.2' $'192.0.2.1/32\n' '192.0.2.1/24' $'192.0.2.1/32\n\n192.0.2.2/32' 'fe80::1%eth0'; do
    PANEL_CIDR=$sources
    if panel_sources_valid 2>/dev/null; then
        printf 'Invalid panel source list accepted\n' >&2
        exit 1
    fi
done
PANEL_CIDR=$(printf '192.0.2.1/32\n%.0s' {1..101})
if panel_sources_valid 2>/dev/null; then
    printf 'Oversized panel source list accepted\n' >&2
    exit 1
fi
PANEL_ACCESS=both
PANEL_CIDR=$'192.0.2.1/32\n2001:db8::1/128'
ufw() { printf '%s|' "$@"; printf '\n'; }
rules=$(configure_panel_firewall)
[[ $rules == *'allow|from|192.0.2.1/32|to|any|port|38471|proto|tcp|'* ]]
[[ $rules == *'allow|from|2001:db8::1/128|to|any|port|38471|proto|tcp|'* ]]
[[ $(printf '%s\n' "$rules" | wc -l) == 2 ]]
PANEL_ACCESS=tunnel
[[ -z $(configure_panel_firewall) ]]
unset -f ufw

phase_is_fresh none
! phase_is_fresh pending
! phase_is_fresh complete
[[ $(inspection_phase_label none) == fresh ]]
[[ $(inspection_phase_label pending) == pending ]]
inspection_fresh_value none true true
! inspection_fresh_value pending true true
! inspection_fresh_value none false true
! inspection_fresh_value none true false

parse_machine_args 0123456789abcdef0123456789abcdef
[[ $MACHINE_RUN_N == 0123456789abcdef0123456789abcdef ]]
[[ $(unit_for prepare) == openviya-desktop-0123456789abcdef0123456789abcdef-prepare ]]
parse_machine_args 01234567-89ab-cdef-0123-456789abcdef
[[ $MACHINE_RUN_N == 0123456789abcdef0123456789abcdef ]]
if parse_machine_args '../bad-guid'; then
    printf 'unsafe run ID was accepted\n' >&2
    exit 1
fi
[[ $(json_escape 'quote" slash\ newline') == 'quote\" slash\\ newline' ]]
printf 'Machine helper runtime checks passed; bootstrap was not executed.\n'
