#!/bin/sh
# Makes, once per stack, a certificate authority of the stack's own and a certificate from it for api.github.com, for the GitHub limit
# group (tests/e2e/github.spec.ts). Deluno trusts the authority (SSL_CERT_DIR names its folder) and the stack's GitHub stand-in
# (github.mjs) presents the certificate. Nothing here leaves the stack: the authority's key is never written beside the authority.
set -eu
out="$1"
mkdir -p "$out/ca" "$out/leaf"
if [ -f "$out/ca/e2e-ca.pem" ]; then exit 0; fi
work="$(mktemp -d)"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/ca.key" -out "$out/ca/e2e-ca.pem" -days 30 \
  -subj "/CN=Deluno end-to-end test authority" -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
openssl req -newkey rsa:2048 -nodes -keyout "$out/leaf/api.key" -out "$work/api.csr" -subj "/CN=api.github.com" 2>/dev/null
printf 'subjectAltName=DNS:api.github.com\nbasicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n' > "$work/ext.cnf"
openssl x509 -req -in "$work/api.csr" -CA "$out/ca/e2e-ca.pem" -CAkey "$work/ca.key" -CAcreateserial -out "$out/leaf/api.pem" -days 30 -extfile "$work/ext.cnf" 2>/dev/null
chmod -R a+rX "$out"
rm -rf "$work"
