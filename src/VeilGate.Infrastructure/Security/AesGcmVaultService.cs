using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Vault;
using VeilGate.Infrastructure.Persistence;

namespace VeilGate.Infrastructure.Security;

/// <summary>
/// The vault (Backend doc 6.2): every original value is encrypted with AES-256-GCM under the tenant's data key,
/// with a fresh 96-bit nonce and the tenant, session and token bound in as associated data. Moving a row to another
/// session, tenant or token makes decryption fail.
/// </summary>
public sealed class AesGcmVaultService(VeilGateDbContext db, IKeyProvider keys, IClock clock) : IVaultService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);

    private static byte[] Aad(Guid tenantId, Guid sessionId, string token) => Encoding.UTF8.GetBytes($"{tenantId}|{sessionId}|{token}");

    public async Task<VaultSession> OpenAsync(Guid tenantId, Guid clientId, TimeSpan ttl, string? subjectId = null, CancellationToken ct = default)
    {
        ttl = ttl < MinTtl ? MinTtl : ttl > MaxTtl ? MaxTtl : ttl;
        var now = clock.UtcNow;
        var dek = await keys.GetActiveKeyAsync(tenantId, ct);

        return new VaultSession
        {
            TenantId = tenantId,
            ClientId = clientId,
            CreatedAt = now,
            ExpiresAt = now + ttl,
            // A separate sub-key for dates, so the date offset never reveals anything about the encryption key itself.
            DateShiftKey = HMACSHA256.HashData(dek.Key, "veilgate-date-shift"u8),
            SubjectId = subjectId,
        };
    }

    public async Task StoreAsync(VaultSession session, CancellationToken ct = default)
    {
        if (!await db.VaultSessions.AnyAsync(s => s.Id == session.Id, ct))
            db.VaultSessions.Add(session);

        var already = (await db.VaultTokens.Where(t => t.SessionId == session.Id).Select(t => t.Token).ToListAsync(ct)).ToHashSet();
        var dek = await keys.GetActiveKeyAsync(session.TenantId, ct);

        foreach (var (token, plaintext) in session.Tokens)
        {
            if (already.Contains(token)) continue;

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plain = Encoding.UTF8.GetBytes(plaintext);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(dek.Key, TagSize))
                aes.Encrypt(nonce, plain, cipher, tag, Aad(session.TenantId, session.Id, token));

            db.VaultTokens.Add(new VaultToken
            {
                TenantId = session.TenantId,
                SessionId = session.Id,
                Token = token,
                EntityType = TypeOf(token),
                Ciphertext = cipher,
                Nonce = nonce,
                Tag = tag,
                DekVersion = dek.Version,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<VaultSession?> FindAsync(Guid sessionId, Guid clientId, CancellationToken ct = default)
    {
        // Row-level security already hides other tenants' sessions. Every miss returns the same null.
        var now = clock.UtcNow;
        return await db.VaultSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ClientId == clientId && s.ExpiresAt > now, ct);
    }

    public async Task<string?> ResolveAsync(VaultSession session, string token, CancellationToken ct = default)
    {
        if (session.ExpiresAt <= clock.UtcNow) return null;

        var row = await db.VaultTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.SessionId == session.Id && t.Token == token, ct);
        if (row is null) return null;

        var dek = await keys.GetKeyAsync(row.TenantId, row.DekVersion, ct);
        var plain = new byte[row.Ciphertext.Length];
        using var aes = new AesGcm(dek.Key, TagSize);
        // Throws CryptographicException if the data, the nonce, the tag or the binding was changed.
        aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plain, Aad(row.TenantId, row.SessionId, row.Token));
        return Encoding.UTF8.GetString(plain);
    }

    public Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        // vault_tokens rows go with their session through ON DELETE CASCADE.
        return db.VaultSessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);
    }

    /// <summary>"[[PERSON_7F3A]]" -> "PERSON" (strip the brackets and the "_XXXX" suffix).</summary>
    private static string TypeOf(string token) => token.Length > 9 ? token[2..^7] : token;
}
