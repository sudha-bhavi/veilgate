using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>
/// Asks the database itself, after the real migrations have run, whether the isolation rules hold for EVERY table, not just the ones
/// a test happens to name. This is the "RLS enabled and forced on all tenant tables (automated check)" item of the go-live checklist.
/// When someone adds a table and forgets row-level security, one of these tests fails and names the table.
/// </summary>
[Collection("postgres")]
public class DatabaseSecurityAuditTests(PostgresFixture pg)
{
    // Tables that deliberately have no tenant_id column. Anything new must be added here on purpose, which forces a decision.
    private static readonly string[] TablesWithoutTenantId = ["tenants", "__EFMigrationsHistory"];

    private async Task<List<string>> Names(string sql)
    {
        await using var db = pg.AdminContext();   // the owner may read the catalog
        return await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
    }

    private const string TenantTables = """
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
          AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
        """;

    // ------------------------------------------------------- row-level security

    [Fact]
    public async Task Every_table_with_a_tenant_id_has_row_level_security_enabled_and_forced()
    {
        var unprotected = await Names($"""
            SELECT c.relname AS "Value" {TenantTables}
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            ORDER BY 1
            """);

        unprotected.Should().BeEmpty("a tenant table without ENABLE and FORCE ROW LEVEL SECURITY lets the table owner and any mistake read across tenants");
    }

    [Fact]
    public async Task Every_table_with_a_tenant_id_has_a_policy_for_the_app_role()
    {
        var withoutPolicy = await Names($"""
            SELECT c.relname AS "Value" {TenantTables}
              AND NOT EXISTS (SELECT 1 FROM pg_policies p
                              WHERE p.schemaname = 'public' AND p.tablename = c.relname AND 'vg_app' = ANY (p.roles))
            ORDER BY 1
            """);

        withoutPolicy.Should().BeEmpty("with row-level security on and no policy, the app would see nothing; a missing policy is a bug either way");
    }

    [Fact]
    public async Task Every_policy_for_the_app_role_is_tied_to_the_current_tenant()
    {
        var loose = await Names("""
            SELECT p.tablename || '.' || p.policyname AS "Value"
            FROM pg_policies p
            WHERE p.schemaname = 'public' AND 'vg_app' = ANY (p.roles)
              AND ((p.cmd <> 'INSERT'
                    AND NOT (coalesce(p.qual, '') LIKE '%app_current_tenant()%' OR coalesce(p.qual, '') LIKE '%app.tenant_id%'))
                   OR (p.cmd IN ('INSERT', 'ALL')
                    AND NOT (coalesce(p.with_check, '') LIKE '%app_current_tenant()%' OR coalesce(p.with_check, '') LIKE '%app.tenant_id%')))
            ORDER BY 1
            """);

        loose.Should().BeEmpty("every policy the app role runs under must compare the row with the tenant set for the connection (app_current_tenant())");
    }

    [Fact]
    public async Task The_tenants_table_is_protected_too()
    {
        var unprotected = await Names("""
            SELECT c.relname AS "Value" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = 'tenants' AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """);

        unprotected.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_tables_that_are_meant_to_have_no_tenant_id_lack_one()
    {
        var others = await Names("""
            SELECT c.relname AS "Value"
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
              AND NOT EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
            ORDER BY 1
            """);

        others.Except(TablesWithoutTenantId).Should().BeEmpty("a new table without tenant_id escapes the isolation checks above; add tenant_id, or list the table here on purpose");
    }

    // -------------------------------------------------------------- roles

    [Fact]
    public async Task The_runtime_roles_cannot_bypass_row_level_security_or_administer_the_database()
    {
        var risky = await Names("""
            SELECT rolname AS "Value" FROM pg_roles
            WHERE rolname IN ('vg_app', 'vg_readonly', 'vg_platform', 'vg_audit_writer')
              AND (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole)
            ORDER BY 1
            """);

        risky.Should().BeEmpty();
    }

    [Fact]
    public async Task The_runtime_roles_exist_so_the_check_above_means_something()
    {
        var found = await Names("""SELECT rolname AS "Value" FROM pg_roles WHERE rolname IN ('vg_app', 'vg_readonly', 'vg_platform') ORDER BY 1""");

        found.Should().Equal("vg_app", "vg_platform", "vg_readonly");
    }

    [Fact]
    public async Task The_read_only_role_has_no_write_access_to_any_table()
    {
        var writable = await Names("""
            SELECT DISTINCT table_name || ' (' || privilege_type || ')' AS "Value"
            FROM information_schema.role_table_grants
            WHERE grantee = 'vg_readonly' AND table_schema = 'public' AND privilege_type <> 'SELECT'
            ORDER BY 1
            """);

        writable.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_in_the_schema_is_granted_to_everyone()
    {
        var open = await Names("""
            SELECT DISTINCT table_name AS "Value"
            FROM information_schema.role_table_grants
            WHERE grantee = 'PUBLIC' AND table_schema = 'public'
            ORDER BY 1
            """);

        open.Should().BeEmpty();
    }

    [Theory]
    [InlineData("audit_events")]
    [InlineData("outbox_messages")]
    public async Task The_app_role_can_add_and_read_but_never_change_or_delete_append_only_tables(string table)
    {
        var granted = await Names($"""
            SELECT p AS "Value" FROM unnest(ARRAY['UPDATE', 'DELETE', 'TRUNCATE']) AS p
            WHERE has_table_privilege('vg_app', 'public.{table}', p)
            """);

        granted.Should().BeEmpty();
    }

    // ---------------------------------------------- SECURITY DEFINER functions

    [Fact]
    public async Task Every_security_definer_function_pins_its_search_path()
    {
        var unpinned = await Names("""
            SELECT p.proname AS "Value"
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.prosecdef
              AND NOT EXISTS (SELECT 1 FROM unnest(coalesce(p.proconfig, ARRAY[]::text[])) AS setting WHERE setting LIKE 'search_path=%')
            ORDER BY 1
            """);

        unpinned.Should().BeEmpty("a SECURITY DEFINER function runs with its owner's rights; without a fixed search_path a caller could shadow the tables it reads");
    }

    [Fact]
    public async Task No_security_definer_function_can_be_run_by_a_role_that_was_not_granted_it()
    {
        // vg_readonly was granted none of them, so if it can run one, the function is open to PUBLIC.
        var open = await Names("""
            SELECT p.proname AS "Value"
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.prosecdef AND has_function_privilege('vg_readonly', p.oid, 'EXECUTE')
            ORDER BY 1
            """);

        open.Should().BeEmpty("each SECURITY DEFINER function must REVOKE ALL FROM PUBLIC and grant EXECUTE to vg_app only");
    }

    [Fact]
    public async Task The_app_role_can_run_the_security_definer_functions_it_needs()
    {
        var missing = await Names("""
            SELECT f AS "Value" FROM unnest(ARRAY[
                'auth_find_user_by_email(citext)', 'auth_find_session_tenant(varchar)', 'auth_find_client_tenant(varchar)',
                'auth_find_invitation_tenant(varchar)', 'eval_claim_next_run(int, int)', 'outbox_claim_due(int, int)',
                'outbox_mark_sent(uuid)', 'outbox_mark_failed(uuid, varchar, int)', 'outbox_mark_dead(uuid, varchar)',
                'vault_purge_expired()', 'sessions_reap(int, int)']) AS f
            WHERE NOT has_function_privilege('vg_app', 'public.' || f, 'EXECUTE')
            """);

        missing.Should().BeEmpty();
    }
}
