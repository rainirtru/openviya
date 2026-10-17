#!/usr/bin/env bash
# Test-only CA; never import it on a workstation or a VPS.
[[ -f /.dockerenv && ${OPENVIYA_DISPOSABLE_TEST:-} == yes ]] || exit 1
init_test_ca() {
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -noenc -days 60 \
        -subj '/CN=OpenViya disposable test CA' \
        -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' \
        -keyout "$STATE/test-ca.key" -out "$STATE/test-ca.crt" 2>/dev/null
    install -m 644 "$STATE/test-ca.crt" /usr/local/share/ca-certificates/openviya-test.crt
    update-ca-certificates >/dev/null
}
issue_test_certificate() {
    local domain=$1 directory=$2
    mkdir -p "$directory"
    openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -noenc \
        -subj "/CN=$domain" -keyout "$directory/privkey.pem" -out "$directory/request.pem" 2>/dev/null
    printf 'subjectAltName=DNS:%s\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\n' "$domain" > "$directory/extensions"
    openssl x509 -req -in "$directory/request.pem" -CA "$STATE/test-ca.crt" \
        -CAkey "$STATE/test-ca.key" -CAcreateserial -days 30 \
        -extfile "$directory/extensions" -out "$directory/fullchain.pem" 2>/dev/null
}
