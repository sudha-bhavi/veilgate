using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Domain.Tests;

internal static class UserRepositoryDoubles
{
    /// <summary>
    /// A mocking library ignores the interface's default method, so tests that mock IUserRepository set up the attempt counting themselves,
    /// the same way the database does it: a locked account is refused; otherwise the attempt is counted (and the fifth one locks).
    /// </summary>
    public static void CountAttemptsInMemory(this IUserRepository users) =>
        users.TryReserveLoginAttemptAsync(Arg.Any<User>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var user = call.Arg<User>();
                var now = call.Arg<DateTimeOffset>();
                if (user.IsLocked(now)) return Task.FromResult(false);
                user.RegisterFailedLogin(now);
                return Task.FromResult(true);
            });
}
