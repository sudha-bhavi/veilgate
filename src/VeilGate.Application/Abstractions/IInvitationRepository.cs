using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

/// <summary>
/// Loads and saves invitations of the current tenant. There is no SaveChanges here on purpose: every repository shares one
/// DbContext per request, so IUserRepository.SaveChangesAsync saves the person and the invitation together.
/// </summary>
public interface IInvitationRepository
{
    void Add(Invitation invitation);

    /// <summary>Runs BEFORE a tenant is set: which tenant owns the invitation behind this token hash? (through the lookup function)</summary>
    Task<Guid?> FindTenantByTokenHashAsync(string tokenHash, CancellationToken ct);

    Task<Invitation?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>Every invitation ever sent to this person, used or not. Resend uses it to switch the old links off.</summary>
    Task<List<Invitation>> ListForUserAsync(Guid userId, CancellationToken ct);
}