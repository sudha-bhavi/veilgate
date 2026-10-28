using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;

// Development helper: creates a demo tenant and one user per role so the console can be tried by hand.
// It connects as the migration role (the only login role allowed to write across tenants) and refuses any database that
// is not on this machine, so it cannot be pointed at a shared or production server by mistake.

var arguments = args.Chunk(2).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1]);
if (!arguments.TryGetValue("--connection", out var connection) || !arguments.TryGetValue("--password", out var password))
{
    Console.Error.WriteLine("""
        Usage: dotnet run --project tools/VeilGate.Seeder -- --connection "<vg_migrator connection string>" --password "<at least 12 characters>"
        """);
    return 1;
}

var host = new NpgsqlConnectionStringBuilder(connection).Host;
if (host is not ("localhost" or "127.0.0.1" or "::1"))
{
    Console.Error.WriteLine($"Refusing to seed '{host}': this tool only works on a database on this machine.");
    return 1;
}
if (password.Length < 12)
{
    Console.Error.WriteLine("The password must be at least 12 characters.");
    return 1;
}

var options = new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(connection).Options;
await using var db = new VeilGateDbContext(options);

const string tenantName = "Acme Health";
var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Name == tenantName);
if (tenant is null)
{
    tenant = Tenant.Create(tenantName).Value;
    db.Tenants.Add(tenant);
    await db.SaveChangesAsync();
    Console.WriteLine($"created tenant: {tenantName}");
}

var hash = new Argon2PasswordHasher().Hash(password);
var now = DateTimeOffset.UtcNow;
(string Email, string Name, UserRole Role)[] people =
[
    ("admin@acme.test", "Alex Admin", UserRole.TenantAdmin),
    ("security@acme.test", "Sam Security", UserRole.SecurityOfficer),
    ("dev@acme.test", "Dana Developer", UserRole.Developer),
    ("auditor@acme.test", "Ari Auditor", UserRole.Auditor),
];

foreach (var (email, name, role) in people)
{
    if (await db.Users.AnyAsync(u => u.Email == email))
    {
        Console.WriteLine($"already there: {email}");
        continue;
    }

    var user = User.Invite(tenant.Id, email, name, role, now).Value;
    user.AcceptInvitation(hash, now);
    db.Users.Add(user);
    Console.WriteLine($"created user: {email} ({role})");
}

await db.SaveChangesAsync();
return 0;