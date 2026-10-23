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
}
