using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Vault;
using VeilGate.Infrastructure.Persistence;

namespace VeilGate.Infrastructure.Security;

/// <summary>
/// Development key provider: each tenant's data key (DEK) is random, stored in tenant_keys wrapped (AES-GCM) by a master key (KEK)
/// taken from configuration. Production swaps this for a Key Vault implementation of the same interface.
/// </summary>
public sealed class LocalKeyProvider(VeilGateDbContext db, IClock clock, byte[] masterKey) : IKeyProvider
{
    private readonly AesGcmSecretProtector _wrapper = new(masterKey);

    // Binding the wrapped key to its tenant and version means a row copied to another tenant cannot be unwrapped.
    private static string Context(Guid tenantId, int version) => $"dek|{tenantId}|{version}";

    public async Task<DataKey> GetActiveKeyAsync(Guid tenantId, CancellationToken ct = default)
    {
        var row = await db.TenantKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId && k.Status == "Active")
            .OrderByDescending(k => k.DekVersion)
            .FirstOrDefaultAsync(ct);

        row ??= await CreateFirstKeyAsync(tenantId, ct);
        return Unwrap(row);
    }

    public async Task<DataKey> GetKeyAsync(Guid tenantId, int version, CancellationToken ct = default)
    {
        var row = await db.TenantKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.DekVersion == version, ct)
            ?? throw new InvalidOperationException($"No data key version {version} for this tenant.");
        return Unwrap(row);
    }

    private async Task<TenantKey> CreateFirstKeyAsync(Guid tenantId, CancellationToken ct)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var created = new TenantKey
        {
            TenantId = tenantId,
            DekVersion = 1,
            WrappedDek = Encoding.UTF8.GetBytes(_wrapper.Protect(dek, Context(tenantId, 1))),
            Status = "Active",
            CreatedAt = clock.UtcNow,
        };

        db.TenantKeys.Add(created);
        try
        {
            await db.SaveChangesAsync(ct);
            return created;
        }
        catch (DbUpdateException)
        {
            // Another request created version 1 at the same moment (primary key clash). Use theirs.
            db.Entry(created).State = EntityState.Detached;
            return await db.TenantKeys.AsNoTracking().FirstAsync(k => k.TenantId == tenantId && k.DekVersion == 1, ct);
        }
    }

    private DataKey Unwrap(TenantKey row) =>
        new(row.DekVersion, _wrapper.Unprotect(Encoding.UTF8.GetString(row.WrappedDek), Context(row.TenantId, row.DekVersion)));
}
