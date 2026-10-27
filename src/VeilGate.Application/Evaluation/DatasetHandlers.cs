using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Domain.Evaluation;

namespace VeilGate.Application.Evaluation;

/// <summary>Who is acting and from where. Both come from the verified token and the connection, never from the body.</summary>
public sealed record EvaluationActor(Guid UserId, string? Ip);

public sealed record DatasetView(Guid Id, string Name, int CaseCount, DateTimeOffset CreatedAt)
{
    public static DatasetView From(EvalDataset d) => new(d.Id, d.Name, d.CaseCount, d.CreatedAt);
}

/// <summary>Writes evaluation events to the audit log: who did what to which dataset or run. Counts and ids only, never note text.</summary>
public sealed class EvaluationAuditor(IAuditWriter audit, ITenantContext tenant, IClock clock, ILogger<EvaluationAuditor> logger)
{
    public async Task WriteAsync(EvaluationActor actor, string action, string resourceType, Guid resourceId, CancellationToken ct, int? count = null)
    {
        var details = new Dictionary<string, string>();
        if (count is { } c) details["count"] = c.ToString();

        var created = AuditEvent.Create(tenant.TenantId, clock.UtcNow, "user", actor.UserId, action, resourceType, resourceId, "success", actor.Ip, details);
        if (created.IsFailure)
        {
            logger.LogError("Could not build audit event {Action}: {Code}", action, created.Error.Code);
            return;
        }
        await audit.WriteAsync(created.Value, ct);
    }
}

public sealed record CreateSyntheticDatasetCommand(string Name, int? Count, int? Seed, EvaluationActor Actor);

public sealed class CreateSyntheticDatasetHandler(IEvaluationRepository datasets, ITenantContext tenant, EvaluationAuditor auditor)
{
    public const int DefaultCount = 200;
    public const int MaxDatasetsPerTenant = 20;

    public async Task<Result<DatasetView>> HandleAsync(CreateSyntheticDatasetCommand cmd, CancellationToken ct = default)
    {
        var count = cmd.Count ?? DefaultCount;
        var dataset = EvalDataset.Create(tenant.TenantId, cmd.Name, count);   // checks the name and the size
        if (dataset.IsFailure) return dataset.Error;

        if (await datasets.DatasetNameExistsAsync(dataset.Value.Name, ct)) return Error.Conflict("resource.conflict", "A dataset with this name already exists.");
        if ((await datasets.ListDatasetsAsync(ct)).Count >= MaxDatasetsPerTenant)
            return Error.Conflict("evaluation.dataset_limit", $"A tenant can keep at most {MaxDatasetsPerTenant} datasets.");

        var generated = SyntheticDatasetGenerator.Generate(count, cmd.Seed ?? SyntheticDatasetGenerator.DefaultSeed);
        var cases = new List<EvalCase>(count);
        for (var i = 0; i < generated.Count; i++)
        {
            var item = EvalCase.Create(tenant.TenantId, dataset.Value.Id, i, generated[i].Text, generated[i].Expected);
            if (item.IsFailure) return item.Error;
            cases.Add(item.Value);
        }

        datasets.AddDataset(dataset.Value, cases);
        await datasets.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "dataset.create", "eval_dataset", dataset.Value.Id, ct, count);
        return DatasetView.From(dataset.Value);
    }
}

public sealed class ListDatasetsHandler(IEvaluationRepository datasets)
{
    public async Task<IReadOnlyList<DatasetView>> HandleAsync(CancellationToken ct = default) =>
        (await datasets.ListDatasetsAsync(ct)).Select(DatasetView.From).ToList();
}

public sealed class GetDatasetHandler(IEvaluationRepository datasets)
{
    public async Task<Result<DatasetView>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var dataset = await datasets.FindDatasetAsync(id, ct);
        return dataset is null ? Error.NotFound() : DatasetView.From(dataset);
    }
}