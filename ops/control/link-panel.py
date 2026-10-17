#!/usr/bin/env python3
"""Provision backend API credentials; nodes/list requires admin on pinned v3.10.2."""
import http.cookiejar
import json
import pathlib
import urllib.request
import urllib.error
import ssl

root = pathlib.Path('/opt/openviya-control')
access = json.loads((root / 'private/access.json').read_text())
base = 'https://127.0.0.1:38471' + access['panel_path']
tls = ssl.create_default_context(cafile=str(root / 'panel-ca.crt'))
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), urllib.request.HTTPSHandler(context=tls))
def request(path, data=None, csrf=None):
    headers = {'Content-Type': 'application/json', 'Origin': 'https://127.0.0.1:38471'}
    if csrf:
        headers['X-CSRF-Token'] = csrf
    req = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(), headers=headers)
    with client.open(req, timeout=20) as response:
        result = json.load(response)
    if not result.get('success'):
        raise RuntimeError('Panel operation rejected: ' + path)
    return result.get('obj')
csrf = request('csrf-token')
request('login', {'username': access['panel_username'], 'password': access['panel_password']}, csrf)
csrf = request('csrf-token')
token_file = root / 'private/panel-api-token'
if token_file.exists():
    token = token_file.read_text().strip()
else:
    for existing in request('panel/api/setting/apiTokens') or []:
        if existing.get('name') == 'openviya-backend-control' and existing.get('scope') == 'admin':
            request('panel/api/setting/apiTokens/delete/' + str(existing['id']), {'expectedScope': 'admin'}, csrf)
    result = request('panel/api/setting/apiTokens/create', {'name': 'openviya-backend-control', 'scope': 'admin', 'expiresAt': 0}, csrf)
    token = result.get('token')
    if isinstance(token, str) and token:
        token_file.write_text(token + '\n')
        token_file.chmod(0o600)
if not isinstance(token, str) or not token:
    raise SystemExit('Token response invalid; no backend configuration changed')
for path in ['panel/api/server/status', 'panel/api/nodes/list', 'panel/api/inbounds/list']:
    req = urllib.request.Request(base + path, headers={'Authorization': 'Bearer ' + token})
    try:
        with urllib.request.urlopen(req, timeout=20, context=tls) as response:
            body = json.load(response)
    except urllib.error.HTTPError as error:
        raise SystemExit('Panel readback rejected: ' + path + ', HTTP ' + str(error.code))
    assert body.get('success'), 'Panel token readback failed'
for existing in request('panel/api/setting/apiTokens') or []:
    if existing.get('name') == 'openviya-backend-monitor' and existing.get('scope') == 'monitor':
        request('panel/api/setting/apiTokens/delete/' + str(existing['id']), {'expectedScope': 'monitor'}, csrf)
obsolete = root / 'private/panel-monitor-token'
if obsolete.exists():
    obsolete.unlink()
env = root / '.env'
lines = [line for line in env.read_text().splitlines() if not line.startswith(('OPENVIYA_3XUI_ENABLED=', 'OPENVIYA_3XUI_BEARER_TOKEN='))]
lines += ['OPENVIYA_3XUI_ENABLED=true', 'OPENVIYA_3XUI_BEARER_TOKEN=' + token]
env.write_text('\n'.join(lines) + '\n')
env.chmod(0o600)
print('Backend linked with dedicated admin-scoped token; all three read-only endpoints passed')
