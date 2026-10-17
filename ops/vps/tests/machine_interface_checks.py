#!/usr/bin/env python3
"""Static safety regressions for desktop machine commands; never runs bootstrap."""
from pathlib import Path
import shutil
import subprocess
import sys
import os


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "ops" / "vps" / "bootstrap-vps.sh"
text = SCRIPT.read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def section(start: str, end: str) -> str:
    a = text.index(start)
    b = text.index(end, a)
    return text[a:b]


inspect = section("desktop_inspect() {", "parse_machine_args() {")
require("desktop-inspect) desktop_inspect" in text, "inspect command must be dispatched")
require('"schemaVersion":1' in inspect, "inspect JSON needs a schema version")
require('inspection_fresh_value "$phase" "$supported" "$fresh"' in inspect, "freshness must be evaluated against the unmodified internal phase")
require('inspection_phase_label "$phase"' in inspect, "pristine phase should be mapped only in returned JSON")
require("/etc/os-release" in inspect and "/run/systemd/system" in inspect, "inspect must identify supported systemd OSes")
for marker in ("ufw status", "ufw show added", "nft list ruleset", "iptables-save", "docker.service", "firewalld.service", "nftables.service", "Match", "ss -H -ltn"):
    require(marker in inspect, f"inspect must read-only check {marker}")
for forbidden in ("python3", "apt-get", "ufw --force reset", "install -d", "systemctl enable", "systemctl restart"):
    require(forbidden not in inspect, f"inspect must not execute mutating/runtime-dependent {forbidden}")

prepare = section("prepare() {", "validate_acme_options() {")
require("[[ ! -e $STATE/phase ]]" in prepare, "prepare must refuse existing state")
require("PREPARE_CONNECTION=${SSH_CONNECTION:-console}" in prepare, "detached prepare must persist captured SSH_CONNECTION")

launch = section("launch_machine_job() {", "desktop_prepare() {")
require(launch.count("systemd-run --no-block --quiet --unit=\"$unit\"") == 2, "both launch paths must start a persistent systemd unit without waiting for its completion")
require("RemainAfterExit=yes" in launch and "UMask=0077" in launch, "job must persist its result and use a private umask")
require("secure_job_script" in launch and "launcher.lock" in launch, "launcher must validate copied script and serialize per-run launches")
require("--setenv=\"SSH_CONNECTION=$capture\"" in launch, "job must receive captured SSH connection")
require("--setenv=\"SUDO_USER=$sudo_user\"" in launch, "finalize job must receive verified sudo user")
require("0:0:700" in text, "secure job directory and script must have root:root ownership and mode 0700")

finalize_launcher = section("desktop_finalize() {", "desktop_job_status() {")
for marker in ('${SUDO_USER:-} == "$ADMIN"', '$SSH_CONNECTION != "$PREPARE_CONNECTION"', "read_phase", 'local_port == "$SSH_PORT"', "openviya-access-rollback.timer"):
    require(marker in finalize_launcher, f"finalize launcher must verify {marker}")

status = section("job_status() {", "launch_machine_job() {")
for marker in ("LoadState", "ActiveState", "SubState", "Job", "Result", "ExecMainStatus", "unit_job_queued", "state=running", "state=succeeded", "state=failed", "state=missing"):
    require(marker in status, f"job status must inspect {marker}")
require("journalctl" not in status and "systemctl status" not in status, "status must not expose raw logs")

credentials = section("desktop_credentials() {", "main() {")
require("[[ $phase == complete" in credentials and "stat -c '%u:%a' \"$STATE/credentials\"" in credentials, "credentials must be phase and owner/mode gated")
require(credentials.index("stat -c") < credentials.index('source "$STATE/credentials"'), "credentials must validate ownership before parsing saved %q values")
require("python3 -" in credentials and "os.environ['PANEL_PASSWORD']" in credentials, "credentials must serialize from environment, not process arguments")

main = text[text.index("main() {"):]
require("$command != desktop-inspect" in main and "$command != desktop-prepare" in main and "$command != desktop-finalize" in main and "$command != desktop-job-status" in main and "$command != desktop-credentials" in main, "machine commands must bypass the global bootstrap lock")
require("desktop-prepare) desktop_prepare" in main and "desktop-finalize) desktop_finalize" in main and "desktop-job-status) desktop_job_status" in main and "desktop-credentials)" in main, "machine commands must be dispatched")

bash = os.environ.get("OPENVIYA_BASH") or (shutil.which("bash") if os.name != "nt" else None)
if bash:
    proc = subprocess.run([bash, "-n", str(SCRIPT)], capture_output=True, text=True)
    require(proc.returncode == 0, f"bash syntax check failed: {proc.stderr}")
else:
    print("SKIP bash syntax check (bash unavailable)")

print("Machine interface static checks passed; bootstrap was not executed.")
