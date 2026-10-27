using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.LlmProviders;

namespace VeilGate.Infrastructure.Persistence;

public sealed class LlmProviderRepository(VeilGateDbContext db) : ILlmProviderRepository
{
    // No tenant filter: row-level security already limits every query to the current tenant.
    public void Add(LlmProvider provider) => db.LlmProviders.Add(provider);
    public void Remove(LlmProvider provider) => db.LlmProviders.Remove(provider);

    public Task<LlmProvider?> FindAsync(Guid id, CancellationToken ct) =>
        db.LlmProviders.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<LlmProvider>> ListAsync(CancellationToken ct) =>
        await db.LlmProviders.OrderBy(p => p.Priority).ThenBy(p => p.Name).ToListAsync(ct);

    public Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken ct) =>
        db.LlmProviders.AnyAsync(p => p.Name == name && (exceptId == null || p.Id != exceptId), ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
