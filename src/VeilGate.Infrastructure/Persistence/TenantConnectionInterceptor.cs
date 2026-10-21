using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Persistence;

/// <summary>
/// After each connection opens, sets app.tenant_id so PostgreSQL row-level security applies (Database doc 6.1).
/// If no tenant is set the setting stays empty and RLS returns zero rows (default deny).
/// Npgsql resets session state (DISCARD ALL) when a connection returns to the pool, so the value cannot leak between requests;
/// a test in the Infrastructure suite checks this.
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!tenant.IsSet) return;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "select set_config('app.tenant_id', @t, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "t";
        p.Value = tenant.TenantId.ToString();
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (!tenant.IsSet) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "select set_config('app.tenant_id', @t, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "t";
        p.Value = tenant.TenantId.ToString();
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }
}
