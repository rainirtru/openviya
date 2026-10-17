#!/usr/bin/env python3
"""Configure the pinned panel before its listener starts. Never print credentials."""
import json
import pathlib
import sqlite3
import subprocess

root = pathlib.Path('/opt/openviya-control')
access = json.loads((root / 'private/access.json').read_text())
compose = ['docker', 'compose', '--project-directory', str(root)]
result = subprocess.run(compose + ['run', '--rm', '--no-deps', '--entrypoint', '/app/x-ui',
    'panel', 'setting', '-port', '2053', '-listenIP', '0.0.0.0',
    '-username', access['panel_username'], '-password', access['panel_password'],
    '-webBasePath', access['panel_path']], capture_output=True)
if result.returncode:
    (root / 'private/panel-setup.log').write_bytes(result.stdout + result.stderr)
    raise SystemExit('Panel CLI failed; see private server log')
volume = subprocess.check_output(['docker', 'volume', 'inspect',
    'openviya-control_panel-data', '--format', '{{.Mountpoint}}'], text=True).strip()
db = sqlite3.connect(str(pathlib.Path(volume) / 'x-ui.db'))
if not {'key', 'value'} <= {r[1] for r in db.execute('pragma table_info(settings)')}:
    raise SystemExit('Unexpected panel settings schema')
settings = {'webPort': '2053', 'webListen': '0.0.0.0', 'webBasePath': access['panel_path'],
    'subEnable': 'false', 'subJsonEnable': 'false', 'subClashEnable': 'false',
    'subListen': '127.0.0.1', 'sessionMaxAge': '60',
    'webCertFile': '', 'webKeyFile': ''}
for key, value in settings.items():
    db.execute('delete from settings where key=?', (key,))
    db.execute('insert into settings (key,value) values (?,?)', (key, value))
db.commit()
actual = dict(db.execute('select key,value from settings'))
assert all(actual.get(k) == v for k,v in settings.items()), 'Settings readback failed'
assert db.execute('select count(*) from inbounds').fetchone()[0] == 0, 'Unexpected inbound'
assert db.execute('select count(*) from users').fetchone()[0] == 1, 'Unexpected administrator count'
assert db.execute('pragma quick_check').fetchone()[0] == 'ok', 'Panel DB integrity failed'
db.close()
print('Panel configured: private listener, subscriptions off, zero VPN inbounds')
