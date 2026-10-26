using Microsoft.EntityFrameworkCore.Migrations;

namespace VeilGate.Infrastructure.Persistence.Sql;

/// <summary>
/// Raw SQL helpers that migrations call for things EF cannot model: row-level security, grants and the login lookup function
/// (Database doc section 6). Each helper is idempotent so a migration can be re-applied safely.
/// </summary>
public static class RlsMigrationExtensions
{
    /// <summary>Creates app_current_tenant(): the tenant set by the API for this connection, or NULL when none (default deny).</summary>
    public static void CreateTenantFunction(this MigrationBuilder mb) => mb.Sql("""
        CREATE OR REPLACE FUNCTION app_current_tenant() RETURNS uuid
        LANGUAGE sql STABLE
        AS $$ SELECT nullif(current_setting('app.tenant_id', true), '')::uuid $$;
        """);

    /// <summary>tenants: a tenant sees only its own row. Platform admin role sees all. Only vg_platform may write.</summary>
    public static void ProtectTenantsTable(this MigrationBuilder mb) => mb.Sql("""
        ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
        ALTER TABLE tenants FORCE ROW LEVEL SECURITY;

        DROP POLICY IF EXISTS tenants_isolation ON tenants;
        DROP POLICY IF EXISTS tenants_platform  ON tenants;
        DROP POLICY IF EXISTS tenants_migrator  ON tenants;
        CREATE POLICY tenants_isolation ON tenants FOR SELECT TO vg_app USING (id = app_current_tenant());
        CREATE POLICY tenants_platform  ON tenants FOR ALL TO vg_platform USING (true) WITH CHECK (true);
        CREATE POLICY tenants_migrator  ON tenants FOR ALL TO vg_migrator USING (true) WITH CHECK (true);

        REVOKE ALL ON tenants FROM vg_app, vg_readonly, vg_platform;
        GRANT SELECT ON tenants TO vg_app;
        GRANT SELECT, INSERT, UPDATE ON tenants TO vg_platform;
        """);

    /// <summary>Standard protection for a table that has a tenant_id column.</summary>
    public static void ProtectTenantTable(this MigrationBuilder mb, string table)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(table, "^[a-z_]+$"))
            throw new ArgumentException("Table name must be lowercase letters and underscores.", nameof(table));

        mb.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS {table}_isolation ON {table};
            DROP POLICY IF EXISTS {table}_migrator  ON {table};
            CREATE POLICY {table}_isolation ON {table} FOR ALL TO vg_app
                USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
            CREATE POLICY {table}_migrator ON {table} FOR ALL TO vg_migrator USING (true) WITH CHECK (true);

            REVOKE ALL ON {table} FROM vg_app, vg_readonly;
            GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO vg_app;
            """);
    }

       /// <summary>
    /// Like ProtectTenantTable, but the app role may only read and add rows. No UPDATE, no DELETE (Database doc 6.2).
    /// There is deliberately no update or delete policy, so row-level security refuses them even if a grant were added by mistake.
    /// </summary>
    public static void ProtectAppendOnlyTable(this MigrationBuilder mb, string table)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(table, "^[a-z_]+$"))
            throw new ArgumentException("Table name must be lowercase letters and underscores.", nameof(table));

        mb.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS {table}_select   ON {table};
            DROP POLICY IF EXISTS {table}_insert   ON {table};
            DROP POLICY IF EXISTS {table}_migrator ON {table};
            CREATE POLICY {table}_select ON {table} FOR SELECT TO vg_app USING (tenant_id = app_current_tenant());
            CREATE POLICY {table}_insert ON {table} FOR INSERT TO vg_app WITH CHECK (tenant_id = app_current_tenant());
            CREATE POLICY {table}_migrator ON {table} FOR ALL TO vg_migrator USING (true) WITH CHECK (true);

            REVOKE ALL ON {table} FROM vg_app, vg_readonly;
            GRANT SELECT, INSERT ON {table} TO vg_app;
            """);
    }

    /// <summary>
    /// Triggers that refuse UPDATE, DELETE and TRUNCATE on audit_events for everyone, including the table owner (Database doc 6.3).
    /// Grants stop the app role; the trigger stops mistakes and stolen admin credentials too.
    /// </summary>
    public static void CreateAuditAppendOnlyTriggers(this MigrationBuilder mb) => mb.Sql("""
        CREATE OR REPLACE FUNCTION audit_block_mutation() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'audit_events is append-only'; END $$;

        DROP TRIGGER IF EXISTS audit_no_update   ON audit_events;
        DROP TRIGGER IF EXISTS audit_no_truncate ON audit_events;
        CREATE TRIGGER audit_no_update BEFORE UPDATE OR DELETE ON audit_events
            FOR EACH ROW EXECUTE FUNCTION audit_block_mutation();
        CREATE TRIGGER audit_no_truncate BEFORE TRUNCATE ON audit_events
            FOR EACH STATEMENT EXECUTE FUNCTION audit_block_mutation();
        """);
   
    /// <summary>
    /// Login has to find a user by email BEFORE the tenant is known. This SECURITY DEFINER function is the one narrow,
    /// read-only door through row-level security. It returns only what the login flow needs and never the whole row.
    /// Email is globally unique (unique index ux_users_email), so it returns at most one row.
    /// </summary>
    public static void CreateLoginLookupFunction(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS auth_find_user_by_email(citext);
        CREATE FUNCTION auth_find_user_by_email(p_email citext)
        RETURNS TABLE (id uuid, tenant_id uuid, role varchar, status varchar, password_hash text,
                       failed_attempts smallint, locked_until timestamptz, token_version int)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            SELECT u.id, u.tenant_id, u.role, u.status, u.password_hash, u.failed_attempts, u.locked_until, u.token_version
            FROM users u WHERE u.email = p_email
        $$;
        REVOKE ALL ON FUNCTION auth_find_user_by_email(citext) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION auth_find_user_by_email(citext) TO vg_app;
        """);

    /// <summary>
    /// Refresh has to find a session BEFORE the tenant is known (the request carries only the refresh token).
    /// Like the login lookup, this is a narrow read-only door through row-level security: it returns only the tenant id.
    /// It matches the previous hash too, so a replayed old token can still be found and the session revoked.
    /// </summary>
    public static void CreateSessionLookupFunction(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS auth_find_session_tenant(varchar);
        CREATE FUNCTION auth_find_session_tenant(p_hash varchar)
        RETURNS uuid
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            SELECT s.tenant_id FROM sessions s
            WHERE s.current_refresh_hash = p_hash OR s.previous_refresh_hash = p_hash
            LIMIT 1
        $$;
        REVOKE ALL ON FUNCTION auth_find_session_tenant(varchar) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION auth_find_session_tenant(varchar) TO vg_app;
        """);
}
