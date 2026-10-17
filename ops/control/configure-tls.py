#!/usr/bin/env python3
"""Internal panel TLS, with a private CA trusted by the backend's Java image."""
import os
import pathlib
import sqlite3
import subprocess
import sys

os.umask(0o077)
root = pathlib.Path('/opt/openviya-control')
renew = '--renew' in sys.argv
volume = pathlib.Path(subprocess.check_output(['docker', 'volume', 'inspect', 'openviya-control_panel-data', '--format', '{{.Mountpoint}}'], text=True).strip())
folder = volume / 'tls'
folder.mkdir(mode=0o700, exist_ok=True)
ca_key = root / 'private/panel-ca.key'
ca = root / 'panel-ca.crt'
def run(args):
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
if not ca_key.exists():
    if renew:
        raise SystemExit('CA key missing; refusing to replace trusted authority')
    run(['openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes', '-days', '3650',
         '-subj', '/CN=OpenViya internal panel CA', '-addext', 'basicConstraints=critical,CA:TRUE',
         '-addext', 'keyUsage=critical,keyCertSign,cRLSign', '-keyout', str(ca_key), '-out', str(ca)])
if renew and subprocess.run(['openssl', 'x509', '-checkend', '2592000', '-noout', '-in', str(folder/'panel.crt')], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
    print('Panel certificate not due for renewal')
    raise SystemExit(0)
key = folder / 'panel.key'
if not key.exists():
    run(['openssl', 'genrsa', '-out', str(key), '3072'])
csr = root / 'private/panel.csr'
extension = root / 'private/panel-extensions.cnf'
extension.write_text('subjectAltName=DNS:panel,DNS:localhost,DNS:panel-control.openviya.online,IP:127.0.0.1\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n')
run(['openssl', 'req', '-new', '-key', str(key), '-subj', '/CN=panel', '-out', str(csr)])
temporary = folder / 'panel.crt.new'
run(['openssl', 'x509', '-req', '-in', str(csr), '-CA', str(ca), '-CAkey', str(ca_key),
     '-CAcreateserial', '-days', '365', '-sha256', '-extfile', str(extension), '-out', str(temporary)])
temporary.replace(folder / 'panel.crt')
run(['openssl', 'verify', '-CAfile', str(ca), str(folder / 'panel.crt')])
if not renew:
    db = sqlite3.connect(str(volume / 'x-ui.db'))
    for name, value in [('webCertFile','/etc/x-ui/tls/panel.crt'), ('webKeyFile','/etc/x-ui/tls/panel.key')]:
        db.execute('delete from settings where key=?', (name,))
        db.execute('insert into settings (key,value) values (?,?)', (name,value))
    db.commit()
    db.close()
    env = root / '.env'
    env.write_text(env.read_text().replace('http://panel:2053', 'https://panel:2053'))
else:
    subprocess.run(['docker','compose','--project-directory',str(root),'restart','panel'], check=True)
print('Internal panel certificate verified against private CA')
