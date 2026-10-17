#!/usr/bin/env python3
"""Print allowlisted OAuth failure codes/classes from persisted sessions; never print raw errors."""
import re
import subprocess
import urllib.request
import json
import gzip

query = """SELECT encode(a.attribute_bytes, 'hex')
FROM spring_session_attributes a JOIN spring_session s ON s.primary_id=a.session_primary_id
WHERE a.attribute_name='SPRING_SECURITY_LAST_EXCEPTION'
ORDER BY s.last_access_time DESC LIMIT 3;
"""
command = ['docker','compose','--project-directory','/opt/openviya-control','exec','-T','db','sh','-c',
           'export PGPASSWORD="$POSTGRES_PASSWORD"; exec psql -X -A -t -U openviya -d openviya']
result = subprocess.run(command, input=query, text=True, capture_output=True, check=True)
codes = ['invalid_client', 'invalid_grant', 'invalid_token_response', 'invalid_id_token',
         'authorization_request_not_found', 'invalid_state_parameter', 'invalid_nonce',
         'invalid_user_info_response', 'access_denied', 'invalid_redirect_uri',
         'missing_signature_verifier', 'unsupported_response_type', 'invalid_scope',
         'invalid_request', 'server_error', 'invalid_issuer', 'invalid_audience']
rows = [line.strip() for line in result.stdout.splitlines() if re.fullmatch('[0-9a-f]+',line.strip())]
print('Recent persisted OAuth failures:',len(rows))
for index, row in enumerate(rows,1):
    data = bytes.fromhex(row)
    print('Failure',index,'known codes:', ','.join(code for code in codes if code.encode() in data) or 'none')
    classes = sorted(set(match.decode() for match in re.findall(rb'(?:org\.|java\.|com\.)[A-Za-z0-9_.$]*(?:Exception|Error)', data)))
    print('Failure',index,'exception classes:', ','.join(classes))
    for phrase in ['Unable to resolve', 'UserInfo', 'userinfo', 'ID Token', 'issuer', 'audience',
                   'Invalid signature', 'PKIX', 'expired', 'client authentication', 'Content-Type',
                   'Authentication method', 'token endpoint', 'error validating', 'decode']:
        if phrase.encode().lower() in data.lower():
            print('Failure',index,'matched diagnostic category:',phrase)
    for marker in re.finditer(rb'\x74([\s\S]{2})', data):
        size = int.from_bytes(marker.group(1),'big')
        if not 8 <= size <= 4000:
            continue
        try:
            message = data[marker.end():marker.end()+size].decode('utf-8')
        except UnicodeDecodeError:
            continue
        if not (any(phrase in message for phrase in ['JWK set', 'JWK Set', 'JWKSet', 'JSON Web Key', 'JSON object', 'JSON text', 'Invalid JWK', 'JSONObject', 'JSON entity'])
                or message.startswith(('An error occurred', "Couldn't", 'Failed ', 'Cannot ', 'Error while', 'Unable ', 'Unexpected ', 'Missing '))):
            continue
        message = re.sub(r'https?://[^\s)\]<>]+','[URL]',message)
        message = re.sub(r'[A-Za-z0-9_+/=.-]{24,}','[redacted-long-value]',message)
        print('Sanitized key-set parsing diagnostic:', message[:500])
with urllib.request.urlopen('https://oauth.telegram.org/.well-known/jwks.json',timeout=15) as response:
    body = response.read()
    if response.headers.get('Content-Encoding','').lower() == 'gzip':
        body = gzip.decompress(body)
    keys = json.loads(body)
print('Public JWKS root type:',type(keys).__name__)
if isinstance(keys,dict):
    print('Public JWKS top-level field names:',','.join(keys))
    for key in keys.get('keys',[]):
        print('Public JWK metadata:',{name:key.get(name) for name in ['kty','crv','alg','use'] if name in key})
