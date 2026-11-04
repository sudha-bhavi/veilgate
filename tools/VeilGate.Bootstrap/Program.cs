using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Infrastructure.Bootstrap;
using VeilGate.Infrastructure.Email;
using VeilGate.Infrastructure.Persistence;

// One-time setup of a fresh VeilGate: creates the first organisation and its first administrator, and prints a single-use link for
// that person. It connects as the schema owner (VEILGATE_MIGRATOR_CONNECTION), so only run it where you hold that login.
//
//   bootstrap --organisation "Acme Health" --email you@example.com --name "Your Name"
//   bootstrap --reissue          a new link, if the first one was lost before it was used
//
// The link's address comes from CONSOLE_BASE_URL (for example https://veilgate.example.com).

const int UsageError = 2;

string? organisation = null, email = null, name = null;
var reissue = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--organisation" when i + 1 < args.Length: organisation = args[++i]; break;
        case "--email" when i + 1 < args.Length: email = args[++i]; break;
        case "--name" when i + 1 < args.Length: name = args[++i]; break;
        case "--reissue": reissue = true; break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}");
            return Usage();
    }
}

var connection = Environment.GetEnvironmentVariable("VEILGATE_MIGRATOR_CONNECTION");
var baseUrl = Environment.GetEnvironmentVariable("CONSOLE_BASE_URL");
if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(baseUrl))
{
    Console.Error.WriteLine("VEILGATE_MIGRATOR_CONNECTION and CONSOLE_BASE_URL must be set.");
    return UsageError;
}
if (!reissue && (organisation is null || email is null || name is null)) return Usage();

await using var db = new VeilGateDbContext(new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(connection).Options);
var bootstrapper = new FirstAdminBootstrapper(db, new Clock());
var result = reissue
    ? await bootstrapper.ReissueAsync()
    : await bootstrapper.RunAsync(organisation!, email!, name!);

if (result.IsFailure)
{
    Console.Error.WriteLine($"Nothing was changed: {result.Error.Message}");
    return 1;
}

var link = new ConsoleLinks(Options.Create(new ConsoleOptions { BaseUrl = baseUrl })).AcceptInvitation(result.Value.InvitationToken);
Console.WriteLine(reissue ? "A new invitation link was made for the first administrator. The old link no longer works." : $"Created the organisation and its first administrator ({email}).");
Console.WriteLine();
Console.WriteLine($"Send this link to that person. It works once and stops working at {result.Value.ExpiresAt:yyyy-MM-dd HH:mm} UTC:");
Console.WriteLine();
Console.WriteLine(link);
Console.WriteLine();
Console.WriteLine("They open it, choose a password, and set up their authenticator app.");
Console.WriteLine("This link is shown only now. VeilGate keeps just a fingerprint of it, so it cannot be shown again.");
Console.WriteLine("If it is lost before it is used, run this again with --reissue.");
return 0;

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          bootstrap --organisation "Acme Health" --email you@example.com --name "Your Name"
          bootstrap --reissue
        """);
    return UsageError;
}

file sealed class Clock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
