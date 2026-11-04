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

    /// <summary>
    /// POST /oauth/token has to find an API client BEFORE the tenant is known (the request carries only the client id).
    /// The same narrow read-only door as the login and session lookups: it returns only the tenant id of an ACTIVE client,
    /// so a revoked client is indistinguishable from one that never existed.
    /// </summary>
    public static void CreateClientLookupFunction(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS auth_find_client_tenant(varchar);
        CREATE FUNCTION auth_find_client_tenant(p_client_id varchar)
        RETURNS uuid
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            SELECT c.tenant_id FROM api_clients c
            WHERE c.client_id = p_client_id AND c.status = 'Active'
            LIMIT 1
        $$;
        REVOKE ALL ON FUNCTION auth_find_client_tenant(varchar) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION auth_find_client_tenant(varchar) TO vg_app;
        """);

        /// <summary>
    /// The evaluation worker has no tenant until it has a run, so it takes work through this one narrow SECURITY DEFINER door.
    /// One atomic statement marks the oldest queued run as running (SKIP LOCKED: two workers never get the same one) and returns
    /// only its id and tenant. Runs left "running" by a crashed worker are put back in the queue, or failed once they have used
    /// all their attempts.
    /// </summary>
    public static void CreateEvalClaimFunction(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS eval_claim_next_run(int, int);
        CREATE FUNCTION eval_claim_next_run(p_stale_seconds int, p_max_attempts int)
        RETURNS TABLE (run_id uuid, run_tenant_id uuid)
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
        BEGIN
            UPDATE eval_runs r SET state = 'Failed', error_code = 'evaluation.abandoned', finished_at = now()
            WHERE r.state = 'Running' AND r.attempts >= p_max_attempts
              AND r.started_at < now() - make_interval(secs => p_stale_seconds);

            UPDATE eval_runs r SET state = 'Queued', done_cases = 0
            WHERE r.state = 'Running' AND r.attempts < p_max_attempts
              AND r.started_at < now() - make_interval(secs => p_stale_seconds);

            RETURN QUERY
            UPDATE eval_runs r SET state = 'Running', attempts = r.attempts + 1, started_at = now(), done_cases = 0
            WHERE r.id = (SELECT q.id FROM eval_runs q WHERE q.state = 'Queued' ORDER BY q.created_at, q.id FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING r.id, r.tenant_id;
        END $$;
        REVOKE ALL ON FUNCTION eval_claim_next_run(int, int) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION eval_claim_next_run(int, int) TO vg_app;
        """);
    /// <summary>
    /// The accept-invitation page has to find an invitation BEFORE the tenant is known (the link carries only the token).
    /// The same narrow read-only door as the other lookups: it returns only the tenant id. Whether the invitation is still
    /// usable (unused, not expired) is decided in the domain, with the application's clock, so the rule lives in one place.
    /// </summary>
    public static void CreateInvitationLookupFunction(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS auth_find_invitation_tenant(varchar);
        CREATE FUNCTION auth_find_invitation_tenant(p_hash varchar)
        RETURNS uuid
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            SELECT i.tenant_id FROM invitations i WHERE i.token_hash = p_hash LIMIT 1
        $$;
        REVOKE ALL ON FUNCTION auth_find_invitation_tenant(varchar) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION auth_find_invitation_tenant(varchar) TO vg_app;
        """);

    /// <summary>
    /// The outbox publisher works across every tenant and has none set, and the app role may only read and add rows. So it takes
    /// work through four narrow SECURITY DEFINER doors. "Claiming" does not hold a lock while the message is being sent; it
    /// counts the attempt and pushes next_attempt_at into the future (a lease). If the publisher crashes, the lease runs out and
    /// the message is claimed again. A message that is sent is marked sent; one that keeps failing is marked dead, after the
    /// publisher has put it on the .dlq topic (sent_at set with last_error_code kept means "dead-lettered").
    /// </summary>
    public static void CreateOutboxFunctions(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS outbox_claim_due(int, int);
        CREATE FUNCTION outbox_claim_due(p_limit int, p_lease_seconds int)
        RETURNS TABLE (msg_id uuid, msg_tenant_id uuid, msg_topic varchar, msg_key varchar, msg_payload text,
                       msg_headers text, msg_attempts int, msg_created_at timestamptz)
        LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            UPDATE outbox_messages m
            SET attempts = m.attempts + 1, next_attempt_at = now() + make_interval(secs => p_lease_seconds)
            WHERE m.id IN (
                SELECT q.id FROM outbox_messages q
                WHERE q.sent_at IS NULL AND q.next_attempt_at <= now()
                ORDER BY q.next_attempt_at, q.id
                FOR UPDATE SKIP LOCKED
                LIMIT p_limit)
            RETURNING m.id, m.tenant_id, m.topic, m.key, m.payload::text, m.headers::text, m.attempts, m.created_at
        $$;

        DROP FUNCTION IF EXISTS outbox_mark_sent(uuid);
        CREATE FUNCTION outbox_mark_sent(p_id uuid)
        RETURNS void
        LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            UPDATE outbox_messages SET sent_at = now(), last_error_code = NULL WHERE id = p_id AND sent_at IS NULL
        $$;

        DROP FUNCTION IF EXISTS outbox_mark_failed(uuid, varchar, int);
        CREATE FUNCTION outbox_mark_failed(p_id uuid, p_error varchar, p_backoff_cap_seconds int)
        RETURNS void
        LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            UPDATE outbox_messages
            SET last_error_code = left(p_error, 100),
                next_attempt_at = now() + make_interval(secs => least(p_backoff_cap_seconds, power(2, attempts)))
            WHERE id = p_id AND sent_at IS NULL
        $$;

        DROP FUNCTION IF EXISTS outbox_mark_dead(uuid, varchar);
        CREATE FUNCTION outbox_mark_dead(p_id uuid, p_error varchar)
        RETURNS void
        LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            UPDATE outbox_messages SET sent_at = now(), last_error_code = left(p_error, 100) WHERE id = p_id AND sent_at IS NULL
        $$;

        REVOKE ALL ON FUNCTION outbox_claim_due(int, int) FROM PUBLIC;
        REVOKE ALL ON FUNCTION outbox_mark_sent(uuid) FROM PUBLIC;
        REVOKE ALL ON FUNCTION outbox_mark_failed(uuid, varchar, int) FROM PUBLIC;
        REVOKE ALL ON FUNCTION outbox_mark_dead(uuid, varchar) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION outbox_claim_due(int, int) TO vg_app;
        GRANT EXECUTE ON FUNCTION outbox_mark_sent(uuid) TO vg_app;
        GRANT EXECUTE ON FUNCTION outbox_mark_failed(uuid, varchar, int) TO vg_app;
        GRANT EXECUTE ON FUNCTION outbox_mark_dead(uuid, varchar) TO vg_app;
        """);

    /// <summary>
    /// The housekeeping workers clean up across every tenant and have none set, so they use two narrow SECURITY DEFINER doors that
    /// only delete what is already finished. A vault session that has expired is removed (its tokens go with it through the cascade).
    /// A login session is removed once it ended (revoked, past its hard limit, or idle for too long) longer ago than the grace
    /// period. The grace period keeps a just-ended session around, so a replayed old refresh token is still recognised as reuse.
    /// </summary>
    public static void CreateHousekeepingFunctions(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS vault_purge_expired();
        CREATE FUNCTION vault_purge_expired()
        RETURNS int
        LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            WITH gone AS (DELETE FROM vault_sessions WHERE expires_at <= now() RETURNING 1)
            SELECT count(*)::int FROM gone
        $$;

        DROP FUNCTION IF EXISTS sessions_reap(int, int);
        CREATE FUNCTION sessions_reap(p_idle_seconds int, p_grace_seconds int)
        RETURNS int
        LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp
        AS $$
            WITH gone AS (
                DELETE FROM sessions s
                WHERE LEAST(coalesce(s.revoked_at, 'infinity'::timestamptz), s.expires_at,
                            s.last_seen_at + make_interval(secs => p_idle_seconds))
                      < now() - make_interval(secs => p_grace_seconds)
                RETURNING 1)
            SELECT count(*)::int FROM gone
        $$;

        REVOKE ALL ON FUNCTION vault_purge_expired() FROM PUBLIC;
        REVOKE ALL ON FUNCTION sessions_reap(int, int) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION vault_purge_expired() TO vg_app;
        GRANT EXECUTE ON FUNCTION sessions_reap(int, int) TO vg_app;
        """);

    public static void DropHousekeepingFunctions(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS vault_purge_expired();
        DROP FUNCTION IF EXISTS sessions_reap(int, int);
        """);

    public static void DropOutboxFunctions(this MigrationBuilder mb) => mb.Sql("""
        DROP FUNCTION IF EXISTS outbox_claim_due(int, int);
        DROP FUNCTION IF EXISTS outbox_mark_sent(uuid);
        DROP FUNCTION IF EXISTS outbox_mark_failed(uuid, varchar, int);
        DROP FUNCTION IF EXISTS outbox_mark_dead(uuid, varchar);
        """);
}
