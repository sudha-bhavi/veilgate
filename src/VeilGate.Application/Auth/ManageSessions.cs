using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

/// <param name="UserAgent">The browser's own description, as stored. The console turns it into a name like "Chrome on macOS".</param>
/// <param name="Current">True for the session the request itself is using (the token's "sid").</param>
public sealed record SessionView(
    Guid Id, string? UserAgent, string? Ip, DateTimeOffset CreatedAt, DateTimeOffset LastActiveAt, bool Current);

/// <summary>GET /auth/sessions: the signed-in person's own active sessions, most recently used first.</summary>
public sealed class ListMySessionsHandler(ISessionRepository sessions, IClock clock)
{
    public async Task<List<SessionView>> HandleAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default)
    {
        var active = await sessions.ListActiveForUserAsync(userId, clock.UtcNow, ct);
        return active
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new SessionView(s.Id, s.UserAgent, s.Ip, s.CreatedAt, s.LastSeenAt, s.Id == currentSessionId))
            .ToList();
    }
}

/// <param name="CurrentSessionId">The "sid" of the token making the request.</param>
public sealed record RevokeMySessionCommand(Guid TenantId, Guid UserId, Guid CurrentSessionId, Guid SessionId, string? Ip = null);

/// <summary>
/// DELETE /auth/sessions/{id}. Only the person's own sessions. The session in use cannot be ended here (that is a sign-out),
/// and someone else's session, or one that is already over, is simply "not found".
/// </summary>
public sealed class RevokeMySessionHandler(ISessionRepository sessions, IClock clock, AuthAuditor auditor)
{
    public static readonly Error IsCurrent =
        Error.Conflict("session.current", "This is the session you are using. Sign out instead.");

    public async Task<Result> HandleAsync(RevokeMySessionCommand cmd, CancellationToken ct = default)
    {
        if (cmd.SessionId == cmd.CurrentSessionId) return Result.Failure(IsCurrent);

        var now = clock.UtcNow;
        var session = await sessions.FindForUserAsync(cmd.SessionId, cmd.UserId, ct);
        if (session is null || !session.IsActive(now)) return Result.Failure(Error.NotFound());

        session.Revoke("revoked_by_user", now);
        await sessions.SaveChangesAsync(ct);
        await auditor.SessionsEndedAsync(cmd.TenantId, cmd.UserId, AuthAuditor.SessionRevoke, cmd.Ip, 1, ct);
        return Result.Success();
    }
}

public sealed record LogoutAllCommand(Guid TenantId, Guid UserId, Guid CurrentSessionId, bool KeepCurrent, string? Ip = null);

/// <summary>POST /auth/logout-all: ends every session of the person, or every one except the one in use. The controller checks the recent-MFA proof.</summary>
public sealed class LogoutAllHandler(ISessionRepository sessions, IClock clock, AuthAuditor auditor)
{
    /// <returns>How many sessions were ended.</returns>
    public async Task<Result<int>> HandleAsync(LogoutAllCommand cmd, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var toEnd = (await sessions.ListActiveForUserAsync(cmd.UserId, now, ct))
            .Where(s => !(cmd.KeepCurrent && s.Id == cmd.CurrentSessionId))
            .ToList();
        if (toEnd.Count == 0) return 0;

        foreach (var session in toEnd) session.Revoke("logout_all", now);
        await sessions.SaveChangesAsync(ct);
        await auditor.SessionsEndedAsync(cmd.TenantId, cmd.UserId, AuthAuditor.LogoutAll, cmd.Ip, toEnd.Count, ct);
        return toEnd.Count;
    }
}
