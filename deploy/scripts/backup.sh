#!/usr/bin/env bash
# Backs up the production database (everything: all tenants, the audit log, the vault) to one compressed file.
#
#   deploy/scripts/backup.sh [backup folder]        default folder: deploy/backups
#   BACKUP_KEEP_DAYS=30 deploy/scripts/backup.sh    keep backups for 30 days instead of 14
#
# What it does: dumps the database, checks that the file can be read back, writes a checksum next to it, and deletes backups of
# this kind that are older than BACKUP_KEEP_DAYS. Files are readable only by you.
#
# What it does NOT back up: the secrets in deploy/.env.prod. Keep a copy of that file somewhere safe and separate. Without
# VAULT_KEK and MFA_ENCRYPTION_KEY, data protected with them can never be read again, even from a perfect database backup.
# A backup that lives only on the machine it protects is not a backup: copy the files off the machine as well.
set -euo pipefail

deploy_dir="$(cd "$(dirname "$0")/.." && pwd)"
backup_dir="${1:-$deploy_dir/backups}"
keep_days="${BACKUP_KEEP_DAYS:-14}"

compose() { docker compose -f "$deploy_dir/docker-compose.prod.yml" --env-file "$deploy_dir/.env.prod" "$@"; }
checksum() { if command -v sha256sum >/dev/null; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

[ -f "$deploy_dir/.env.prod" ] || { echo "deploy/.env.prod is missing. Run deploy/scripts/generate-secrets.sh first." >&2; exit 1; }
case "$keep_days" in ''|*[!0-9]*) echo "BACKUP_KEEP_DAYS must be a whole number of days." >&2; exit 1 ;; esac

umask 077
mkdir -p "$backup_dir"
file="$backup_dir/veilgate-$(date -u +%Y%m%dT%H%M%SZ).dump"
trap 'rm -f "$file.partial"' EXIT   # a half-written file never keeps a real name

echo "Backing up the database to $file ..."
# Runs inside the database container as the superuser, so row-level security does not hide any tenant's rows from the dump.
compose exec -T postgres pg_dump -U postgres -d veilgate --format=custom > "$file.partial"
[ -s "$file.partial" ] || { echo "The backup is empty: something went wrong. Nothing was kept." >&2; exit 1; }
mv "$file.partial" "$file"

# A backup you cannot read is worth nothing, so read it back now.
compose exec -T postgres pg_restore --list < "$file" > /dev/null || { echo "The backup cannot be read back. It was kept as $file, but do not trust it." >&2; exit 1; }
( cd "$backup_dir" && checksum "$(basename "$file")" > "$(basename "$file").sha256" )

echo "Backup written and checked: $file ($(wc -c < "$file" | tr -d ' ') bytes)"

# Retention: remove only files this script made (veilgate-*.dump and their checksums), and only the old ones.
removed="$(find "$backup_dir" -maxdepth 1 -name 'veilgate-*.dump' -mtime +"$keep_days" -print -delete | wc -l | tr -d ' ')"
find "$backup_dir" -maxdepth 1 -name 'veilgate-*.dump.sha256' -mtime +"$keep_days" -delete
echo "Removed $removed backup(s) older than $keep_days days."

echo "Reminder: also keep deploy/.env.prod (the secrets) safe and separate, and copy this backup off the machine."
