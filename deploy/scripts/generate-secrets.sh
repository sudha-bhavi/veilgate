#!/usr/bin/env bash
# Writes deploy/.env.prod with strong random secrets for the production stack.
#
#   deploy/scripts/generate-secrets.sh <domain> <email>
#   e.g. deploy/scripts/generate-secrets.sh veilgate.example.com you@example.com
#
# The email is for Let's Encrypt (expiry notices). The file is readable only by you, and it is never committed.
# It will not overwrite an existing file. Mail settings (SMTP_*) are left empty on purpose: the API refuses to start until you fill them in.
set -euo pipefail

cd "$(dirname "$0")/.."   # the deploy folder
out=".env.prod"
domain="${1:-}"
email="${2:-}"

if [ -z "$domain" ] || [ -z "$email" ]; then
  echo "Usage: deploy/scripts/generate-secrets.sh <domain> <email>" >&2
  exit 1
fi
if [ -e "$out" ]; then
  echo "deploy/$out already exists, so it was not overwritten. Move it away first if you really want new secrets" >&2
  echo "(new database passwords would no longer match an existing database)." >&2
  exit 1
fi
command -v openssl >/dev/null || { echo "openssl is required" >&2; exit 1; }

password() { openssl rand -base64 36 | tr -d '/+=\n' | cut -c1-32; }   # letters and digits only: safe inside a connection string
key() { openssl rand -base64 32 | tr -d '\n'; }                        # 32 random bytes, base64: what the vault and MFA keys must be

# The signing key for access tokens (ES256 / P-256), on one line with literal \n, which the API accepts.
pem="$(openssl ecparam -name prime256v1 -genkey -noout | awk '{printf "%s\\n", $0}')"

umask 077   # the file is created readable only by its owner
cat > "$out" <<EOF
# Generated $(date -u +%Y-%m-%dT%H:%M:%SZ) by deploy/scripts/generate-secrets.sh. NEVER COMMIT THIS FILE.
VG_DOMAIN=$domain
ACME_EMAIL=$email

PG_ADMIN_PASSWORD=$(password)
PG_MIGRATOR_PASSWORD=$(password)
PG_APP_PASSWORD=$(password)
PG_READONLY_PASSWORD=$(password)

VAULT_KEK=$(key)
MFA_ENCRYPTION_KEY=$(key)

JWT_KID=key-$(date -u +%Y%m%d)
JWT_PRIVATE_KEY_PEM='$pem'

# Fill these in with your mail provider's details. The API will not start while SMTP_HOST is empty.
SMTP_HOST=
SMTP_PORT=587
SMTP_FROM=no-reply@$domain
SMTP_USER=
SMTP_PASSWORD=
EOF

echo "Wrote deploy/$out"
echo "Next: set SMTP_HOST (and SMTP_USER and SMTP_PASSWORD if your provider needs them) in that file, then back it up somewhere safe."
echo "Losing VAULT_KEK or MFA_ENCRYPTION_KEY means data encrypted with them can never be read again."
