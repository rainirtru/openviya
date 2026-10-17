#!/usr/bin/env python3
"""Check the public authorization flow without printing credentials or state/nonce values."""
import http.cookiejar
import http.cookies
import json
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

origin = 'https://api.openviya.online'
callback = origin + '/login/oauth2/code/telegram'
inspection = json.loads(subprocess.check_output(
    ['docker', 'inspect', 'openviya-control-app-1'], text=True))[0]
env = dict(item.split('=', 1) for item in inspection['Config']['Env'] if '=' in item)
assert env.get('OPENVIYA_TELEGRAM_OIDC_ENABLED') == 'true', 'OIDC is not enabled'
assert env.get('OPENVIYA_TELEGRAM_OIDC_CLIENT_ID', '').strip(), 'Client ID missing'
assert env.get('OPENVIYA_TELEGRAM_OIDC_CLIENT_SECRET', '').strip(), 'Client Secret missing'
assert env.get('OPENVIYA_TELEGRAM_OIDC_REDIRECT_URI') == callback, 'Callback does not match'
for _ in range(45):
    try:
        with urllib.request.urlopen(origin + '/actuator/health/readiness', timeout=5) as response:
            if json.load(response).get('status') == 'UP':
                print('Public HTTPS readiness: UP')
                break
    except (urllib.error.URLError, TimeoutError):
        pass
    time.sleep(2)
else:
    raise SystemExit('Backend did not become ready')

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

jar = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar), NoRedirect())
url = origin + '/api/v1/auth/telegram/login'
authorization = None
for _ in range(4):
    try:
        response = client.open(url, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        assert response.code in (302, 303), 'Login did not redirect, HTTP ' + str(response.code)
        for value in response.headers.get_all('Set-Cookie', []):
            cookie = http.cookies.SimpleCookie()
            cookie.load(value)
            for name, morsel in cookie.items():
                print('Session cookie attributes:', name, 'Path=' + morsel['path'],
                      'Secure=' + str(bool(morsel['secure'])), 'HttpOnly=' + str(bool(morsel['httponly'])),
                      'SameSite=' + morsel['samesite'])
        target = urllib.parse.urljoin(url, response.headers.get('Location', ''))
    parsed = urllib.parse.urlsplit(target)
    if parsed.scheme == 'https' and parsed.netloc == 'oauth.telegram.org' and parsed.path == '/auth':
        authorization = urllib.parse.parse_qs(parsed.query)
        break
    assert target.startswith(origin + '/'), 'Unexpected redirect origin'
    url = target
assert authorization, 'Telegram authorization redirect missing'
checks = {
    'client_id matches configured value': authorization.get('client_id') == [env['OPENVIYA_TELEGRAM_OIDC_CLIENT_ID'].strip()],
    'exact HTTPS callback': authorization.get('redirect_uri') == [callback],
    'Authorization Code flow': authorization.get('response_type') == ['code'],
    'openid and profile scopes': {'openid', 'profile'} <= set(authorization.get('scope', [''])[0].split()),
    'state present': bool(authorization.get('state', [''])[0]),
    'nonce present': bool(authorization.get('nonce', [''])[0]),
    'PKCE S256': authorization.get('code_challenge_method') == ['S256'] and bool(authorization.get('code_challenge', [''])[0]),
    'secure callback session cookie': any(cookie.secure and cookie.path == '/' for cookie in jar),
}
for label, passed in checks.items():
    print(('PASS ' if passed else 'FAIL ') + label)
assert all(checks.values()), 'OIDC authorization contract check failed'
print('Actual Telegram consent and code exchange still require the account owner in a browser')
