#!/usr/bin/env bash
# Proves that a backup can really be restored, without touching the live database.
#
#   deploy/scripts/restore-drill.sh [backup file] [--keep]
#
# With no file it uses the newest one in deploy/backups. It restores into a scratch database called veilgate_drill next to the live
# one, checks the result, and drops the scratch database again (or keeps it with --keep, to look around).
#
# A restore you have never tried is a hope, not a plan. The Deployment document asks for a drill before go-live and every quarter.
# It prints how long the restore took: that is your real recovery time for a database of this size.
set -euo pipefail

deploy_dir="$(cd "$(dirname "$0")/.." && pwd)"
drill_db="veilgate_drill"
keep=0
file=""
for argument in "$@"; do
  case "$argument" in
    --keep) keep=1 ;;
    *) file="$argument" ;;
  esac
done

compose() { docker compose -f "$deploy_dir/docker-compose.prod.yml" --env-file "$deploy_dir/.env.prod" "$@"; }
psql_in() { compose exec -T postgres psql -U postgres -v ON_ERROR_STOP=1 -tAq "$@"; }
verify_checksum() { if command -v sha256sum >/dev/null; then sha256sum -c "$@"; else shasum -a 256 -c "$@"; fi; }

[ -f "$deploy_dir/.env.prod" ] || { echo "deploy/.env.prod is missing." >&2; exit 1; }
if [ -z "$file" ]; then
  file="$(ls -1t "$deploy_dir"/backups/veilgate-*.dump 2>/dev/null | head -1 || true)"
  [ -n "$file" ] || { echo "No backup found in deploy/backups. Run deploy/scripts/backup.sh first." >&2; exit 1; }
fi
[ -f "$file" ] || { echo "Backup file not found: $file" >&2; exit 1; }

failures=0
pass() { echo "  PASS  $1"; }
fail() { echo "  FAIL  $1"; failures=$((failures + 1)); }
warn() { echo "  note  $1"; }

cleanup() { [ "$keep" = 1 ] || psql_in -d postgres -c "DROP DATABASE IF EXISTS $drill_db" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "Restore drill with $file"

echo "1. The file is the one that was written"
if [ -f "$file.sha256" ]; then
  ( cd "$(dirname "$file")" && verify_checksum "$(basename "$file").sha256" >/dev/null 2>&1 ) && pass "checksum matches" || fail "checksum does not match: the file changed or is damaged"
else
  warn "no .sha256 file next to it, so the checksum was not checked"
fi

echo "2. Restore into a scratch database"
psql_in -d postgres -c "DROP DATABASE IF EXISTS $drill_db" -c "CREATE DATABASE $drill_db"
started=$SECONDS
if compose exec -T postgres pg_restore -U postgres -d "$drill_db" --exit-on-error < "$file"; then
  pass "restored without errors in $((SECONDS - started)) seconds (your recovery time at this size)"
else
  fail "the restore reported errors"
  echo "RESTORE DRILL FAILED"
  exit 1
fi

echo "3. The restored database matches the live one"
tables="select string_agg(table_name, ',' order by table_name) from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE'"
[ "$(psql_in -d veilgate -c "$tables")" = "$(psql_in -d "$drill_db" -c "$tables")" ] && pass "the same tables exist" || fail "the tables differ"

counts="select table_name || '=' || (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from %I.%I', table_schema, table_name), false, true, '')))[1]::text from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE' order by table_name"
if [ "$(psql_in -d veilgate -c "$counts")" = "$(psql_in -d "$drill_db" -c "$counts")" ]; then
  pass "every table has the same number of rows"
else
  warn "row counts differ from the live database. That is normal if the system changed since this backup was taken. Differences:"
  diff <(psql_in -d veilgate -c "$counts") <(psql_in -d "$drill_db" -c "$counts") | sed 's/^/        /' || true
fi

latest="select max(\"MigrationId\") from \"__EFMigrationsHistory\""
[ "$(psql_in -d veilgate -c "$latest")" = "$(psql_in -d "$drill_db" -c "$latest")" ] && pass "same newest migration" || warn "the backup is from an older schema version than the live database"

echo "4. Security survived the restore"
no_rls="select string_agg(c.relname, ', ') from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relkind in ('r','p') and exists (select 1 from pg_attribute a where a.attrelid = c.oid and a.attname = 'tenant_id' and not a.attisdropped) and not (c.relrowsecurity and c.relforcerowsecurity)"
unprotected="$(psql_in -d "$drill_db" -c "$no_rls")"
[ -z "$unprotected" ] && pass "row-level security is enabled and forced on every tenant table" || fail "no forced row-level security on: $unprotected"

no_policy="select string_agg(c.relname, ', ') from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relkind in ('r','p') and exists (select 1 from pg_attribute a where a.attrelid = c.oid and a.attname = 'tenant_id' and not a.attisdropped) and not exists (select 1 from pg_policies p where p.schemaname = 'public' and p.tablename = c.relname and 'vg_app' = any (p.roles))"
unpoliced="$(psql_in -d "$drill_db" -c "$no_policy")"
[ -z "$unpoliced" ] && pass "every tenant table has its policy for the app role" || fail "no app-role policy on: $unpoliced"

# The decisive check: as the app role with NO tenant chosen, the users table must look empty. That is row-level security working.
seen="$(psql_in -d "$drill_db" -c "set role vg_app; select count(*) from users" | tail -n 1)"
real="$(psql_in -d "$drill_db" -c "select count(*) from users")"
if [ "$seen" = "0" ]; then
  [ "$real" = "0" ] && warn "the users table is empty, so this check proved little" || pass "as the app role with no tenant set, $real users are invisible (default deny)"
else
  fail "as the app role with no tenant set, $seen users are visible: row-level security is not protecting the restored data"
fi

echo
if [ "$failures" -eq 0 ]; then echo "RESTORE DRILL PASSED"; else echo "RESTORE DRILL FAILED ($failures check(s))"; exit 1; fi
[ "$keep" = 1 ] && echo "The scratch database $drill_db was kept. Drop it when you are done: ... exec postgres psql -U postgres -c 'DROP DATABASE $drill_db'"
exit 0
