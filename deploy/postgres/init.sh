#!/bin/bash
# Runs once when the Postgres volume is first created. Creates least-privilege roles (Database doc 6.2).
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
CREATE EXTENSION IF NOT EXISTS citext;

CREATE ROLE vg_migrator LOGIN PASSWORD '${PG_MIGRATOR_PASSWORD}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE ROLE vg_app      LOGIN PASSWORD '${PG_APP_PASSWORD}'      NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE vg_readonly LOGIN PASSWORD '${PG_READONLY_PASSWORD}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

REVOKE ALL ON DATABASE ${POSTGRES_DB} FROM PUBLIC;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO vg_migrator, vg_app, vg_readonly;

-- The migrator owns the schema objects it creates; the app role can only use them.
ALTER SCHEMA public OWNER TO vg_migrator;
GRANT USAGE ON SCHEMA public TO vg_app, vg_readonly;

ALTER DEFAULT PRIVILEGES FOR ROLE vg_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vg_app;
ALTER DEFAULT PRIVILEGES FOR ROLE vg_migrator IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO vg_app;
ALTER DEFAULT PRIVILEGES FOR ROLE vg_migrator IN SCHEMA public
  GRANT SELECT ON TABLES TO vg_readonly;
SQL
echo "VeilGate roles created."
