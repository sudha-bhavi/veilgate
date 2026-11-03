using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Evaluation;
using VeilGate.Application.Policies;
using VeilGate.Domain;
using VeilGate.Domain.ApiClients;
using VeilGate.Domain.Evaluation;
using VeilGate.Domain.Policies;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;

/// <summary>
/// Sample content for the demo organisation, so a fresh copy has something to look at: two policies, an API client,
/// a dataset of invented notes and a queued evaluation run. Every step checks first and skips what already exists,
/// so running the seeder again changes nothing.
/// </summary>
public static class DemoContent
{
    public static async Task SeedAsync(VeilGateDbContext db, Tenant tenant, User admin, User officer, DateTimeOffset now)
    {
        var published = await EnsurePolicyAsync(
            db, tenant, "HIPAA Safe Harbor", "Names, dates and identifiers handled the standard way.",
            PolicyTemplates.HipaaSafeHarbor, admin, officer, publish: true, now);

        // Left waiting for approval, so the approval workflow has something to show.
        await EnsurePolicyAsync(
            db, tenant, "Names only", "A light policy that only hides people's names.",
            PolicyTemplates.NamesOnly, admin, officer, publish: false, now);

        await EnsureApiClientAsync(db, tenant, admin, published);
        await EnsureEvaluationAsync(db, tenant, admin, published);
        await EnsureDemoUserAsync(db, tenant, now);
    }

    private static async Task<Policy> EnsurePolicyAsync(
        VeilGateDbContext db, Tenant tenant, string name, string description, string template,
        User author, User approver, bool publish, DateTimeOffset now)
    {
        var existing = await db.Policies.FirstOrDefaultAsync(p => p.TenantId == tenant.Id && p.Name == name);
        if (existing is not null)
        {
            Console.WriteLine($"already there: policy {name}");
            return existing;
        }

        var policy = Policy.Create(tenant.Id, name, description).Value;
        var draft = policy.NewDraft(PolicyTemplates.YamlOf(template), author.Id).Value;
        var submitted = draft.Submit(author.Id, $"Starting point from the {template} template.", now);
        if (submitted.IsFailure) throw new InvalidOperationException(submitted.Error.Message);

        if (publish)
        {
            // A different person approves than the one who submitted, the same rule the real workflow enforces.
            var published = policy.Publish(draft, null, approver.Id, now);
            if (published.IsFailure) throw new InvalidOperationException(published.Error.Message);
        }

        db.Policies.Add(policy);
        db.PolicyVersions.Add(draft);
        await db.SaveChangesAsync();
        Console.WriteLine($"created policy: {name} ({(publish ? "published" : "waiting for approval")})");
        return policy;
    }

    private static async Task EnsureApiClientAsync(VeilGateDbContext db, Tenant tenant, User owner, Policy defaultPolicy)
    {
        const string name = "ehr-bridge";
        if (await db.ApiClients.AnyAsync(c => c.TenantId == tenant.Id && c.Name == name))
        {
            Console.WriteLine($"already there: API client {name}");
            return;
        }

        var client = ApiClient.Create(
            tenant.Id, owner.Id, name, ["gateway:invoke", "gateway:detect", "gateway:deidentify"], defaultPolicy.Id).Value;
        var secret = ApiClientCredentials.NewSecret();
        var stored = ApiClientSecret.Create(tenant.Id, client.Id, new Argon2PasswordHasher().Hash(secret)).Value;

        db.ApiClients.Add(client);
        db.ApiClientSecrets.Add(stored);
        await db.SaveChangesAsync();

        // Only the hash is stored, so this is the one time the secret can be seen.
        Console.WriteLine($"created API client: {name}");
        Console.WriteLine($"  client id: {client.ClientId}");
        Console.WriteLine($"  secret:    {secret}   (shown only now: keep it if you want to call the gateway)");
    }

    private static async Task EnsureEvaluationAsync(VeilGateDbContext db, Tenant tenant, User requester, Policy policy)
    {
        const string datasetName = "synthetic-notes-v1";
        var dataset = await db.EvalDatasets.FirstOrDefaultAsync(d => d.TenantId == tenant.Id && d.Name == datasetName);
        if (dataset is null)
        {
            const int count = 200;
            dataset = EvalDataset.Create(tenant.Id, datasetName, count).Value;
            var notes = SyntheticDatasetGenerator.Generate(count, SyntheticDatasetGenerator.DefaultSeed);

            db.EvalDatasets.Add(dataset);
            for (var i = 0; i < notes.Count; i++)
                db.EvalCases.Add(EvalCase.Create(tenant.Id, dataset.Id, i, notes[i].Text, notes[i].Expected).Value);
            await db.SaveChangesAsync();
            Console.WriteLine($"created dataset: {datasetName} ({count} invented notes)");
        }

        if (await db.EvalRuns.AnyAsync(r => r.TenantId == tenant.Id && r.DatasetId == dataset.Id))
        {
            Console.WriteLine("already there: an evaluation run");
            return;
        }

        // The version number is the policy's latest one: the published text.
        var run = EvalRun.Queue(
            tenant.Id, dataset.Id, policy.Id, policy.LastVersionNo, EvalRun.Modes.Rules,
            EvalRun.DefaultRecallThreshold, requester.Id, dataset.CaseCount).Value;
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync();
        Console.WriteLine("queued an evaluation run: start the worker and it finishes in a few seconds");
    }

    /// <summary>
    /// The person the "Try the demo" button signs a visitor in as. A Security Officer, never an administrator: they can see
    /// policies, evaluations and the audit log, but not users, providers or API clients. Their password is random and thrown
    /// away, so nobody can sign in as them with a password, and they have no authenticator. Only the demo button reaches them,
    /// and that button is off unless the API is started with Demo__Enabled=true.
    /// </summary>
    private static async Task EnsureDemoUserAsync(VeilGateDbContext db, Tenant tenant, DateTimeOffset now)
    {
        const string email = "demo@acme.test";
        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            Console.WriteLine($"already there: {email}");
            return;
        }

        var user = User.Invite(tenant.Id, email, "Demo Visitor", UserRole.SecurityOfficer, now).Value;
        var unusablePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.AcceptInvitation(new Argon2PasswordHasher().Hash(unusablePassword), now);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Console.WriteLine($"created demo user: {email} (Security Officer, no usable password). Start the API with Demo__Enabled=true to show the button.");
    }
}