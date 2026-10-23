using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using VeilGate.Infrastructure.Persistence;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>
/// Starts a throwaway PostgreSQL 16 in Docker, creates the same least-privilege roles as deploy/postgres/init.sh,
/// applies the real EF migrations, and hands out connection strings for the superuser (seeding) and vg_app (what the API uses).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public string AdminConnection => _pg.GetConnectionString();

    public string AppConnection => new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
    {
        Username = "vg_app", Password = "app-pw", Pooling = true, MaxPoolSize = 1, // one physical connection forces reuse, which exposes any setting leak
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();

        await using (var c = new NpgsqlConnection(AdminConnection))
        {
            await c.OpenAsync();
            const string sql = """
                CREATE EXTENSION IF NOT EXISTS citext;
                CREATE ROLE vg_migrator LOGIN PASSWORD 'mig-pw' NOSUPERUSER;
                CREATE ROLE vg_app LOGIN PASSWORD 'app-pw' NOSUPERUSER NOBYPASSRLS;
                CREATE ROLE vg_readonly LOGIN PASSWORD 'ro-pw' NOSUPERUSER NOBYPASSRLS;
                CREATE ROLE vg_platform NOLOGIN NOSUPERUSER NOBYPASSRLS;
                CREATE ROLE vg_audit_writer NOLOGIN NOSUPERUSER NOBYPASSRLS;
                GRANT CONNECT ON DATABASE postgres TO vg_app, vg_readonly;
                GRANT USAGE ON SCHEMA public TO vg_app, vg_readonly;
                """;
            await using var cmd = new NpgsqlCommand(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }

        // Migrate as the superuser; the migration SQL grants everything explicitly, so the owner does not matter.
        await using var db = AdminContext();
        await db.Database.MigrateAsync();
    }

    public VeilGateDbContext AdminContext()
        => new(new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(AdminConnection).Options);

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
