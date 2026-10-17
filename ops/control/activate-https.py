#!/usr/bin/env python3
"""Switch Caddy to automatic public TLS only when public DNS points here."""
import ipaddress
import pathlib
import shutil
import socket
import subprocess

root = pathlib.Path('/opt/openviya-control')
marker = root / 'private/https-activated'
if marker.exists():
    raise SystemExit(0)
hosts = ('api.openviya.online', 'openviya.online', 'www.openviya.online')
resolved = {}
for host in hosts:
    try:
        resolved[host] = {entry[4][0] for entry in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)}
    except socket.gaierror:
        print(f'HTTPS waiting: DNS for {host} is not ready')
        raise SystemExit(0)
if any(not addresses or any(ipaddress.ip_address(ip) != ipaddress.ip_address('31.44.7.93') for ip in addresses)
       for addresses in resolved.values()):
    print('HTTPS waiting: all A/AAAA records for api, apex and www must point to the control VPS')
    raise SystemExit(0)
shutil.copyfile(root / 'Caddyfile.https', root / 'proxy/Caddyfile')
subprocess.run(['docker', 'compose', '--project-directory', str(root), 'restart', 'proxy'], check=True)
marker.touch(mode=0o600)
print('Public DNS matches; automatic HTTPS enabled in Caddy, certificate issuance still needs verification')
