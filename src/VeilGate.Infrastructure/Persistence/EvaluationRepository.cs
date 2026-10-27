using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Evaluation;

namespace VeilGate.Infrastructure.Persistence;

public sealed class EvaluationRepository(VeilGateDbContext db) : IEvaluationRepository
{
    // No tenant filter: row-level security already limits every query to the current tenant.
    public void AddDataset(EvalDataset dataset, IEnumerable<EvalCase> cases)
    {
        db.EvalDatasets.Add(dataset);
        db.EvalCases.AddRange(cases);
    }

    public Task<EvalDataset?> FindDatasetAsync(Guid id, CancellationToken ct) =>
        db.EvalDatasets.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<EvalDataset>> ListDatasetsAsync(CancellationToken ct) =>
        await db.EvalDatasets.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);

    public Task<bool> DatasetNameExistsAsync(string name, CancellationToken ct) =>
        db.EvalDatasets.AnyAsync(d => d.Name == name, ct);

    public async Task<IReadOnlyList<EvalCase>> GetCasesAsync(Guid datasetId, int afterOrdinal, int take, CancellationToken ct) =>
        await db.EvalCases.AsNoTracking()
            .Where(c => c.DatasetId == datasetId && c.Ordinal > afterOrdinal)
            .OrderBy(c => c.Ordinal).Take(take).ToListAsync(ct);

    public void AddRun(EvalRun run) => db.EvalRuns.Add(run);

    public Task<EvalRun?> FindRunAsync(Guid id, CancellationToken ct) =>
        db.EvalRuns.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<EvalRun>> ListRunsAsync(int skip, int take, CancellationToken ct) =>
        await db.EvalRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip(skip).Take(take).ToListAsync(ct);

    public Task<int> CountActiveRunsAsync(CancellationToken ct) =>
        db.EvalRuns.CountAsync(r => r.State == EvalRunState.Queued || r.State == EvalRunState.Running, ct);

    public void AddLeaks(IEnumerable<EvalLeak> leaks) => db.EvalRunLeaks.AddRange(leaks);

       public async Task<IReadOnlyList<EvalLeak>> ListLeaksAsync(Guid runId, int skip, int take, CancellationToken ct) =>
        await db.EvalRunLeaks.AsNoTracking().Where(l => l.RunId == runId)
            .OrderBy(l => l.CaseId).ThenBy(l => l.Start).ThenBy(l => l.EntityType)
            .Skip(skip).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<LeakDetail>> ListLeakDetailsAsync(Guid runId, int skip, int take, CancellationToken ct) =>
        await (from l in db.EvalRunLeaks.AsNoTracking()
               where l.RunId == runId
               join n in db.EvalCases.AsNoTracking() on l.CaseId equals n.Id into notes
               from c in notes.DefaultIfEmpty()
               orderby c.Ordinal, l.Start, l.EntityType
               select new LeakDetail(l.CaseId, c == null ? null : c.Ordinal, l.EntityType, l.Start, l.End, l.Reason,
                   c == null ? null : c.Text.Substring(l.Start, l.End - l.Start)))
            .Skip(skip).Take(take).ToListAsync(ct);

    public Task DeleteLeaksAsync(Guid runId, CancellationToken ct) =>
        db.EvalRunLeaks.Where(l => l.RunId == runId).ExecuteDeleteAsync(ct);
    public async Task<ClaimedRun?> ClaimNextRunAsync(TimeSpan staleAfter, int maxAttempts, CancellationToken ct)
    {
        var seconds = (int)staleAfter.TotalSeconds;
        var rows = await db.Database
            .SqlQuery<ClaimedRow>($"""SELECT run_id AS "RunId", run_tenant_id AS "TenantId" FROM eval_claim_next_run({seconds}, {maxAttempts})""")
            .ToListAsync(ct);
        return rows.Count == 0 ? null : new ClaimedRun(rows[0].RunId, rows[0].TenantId);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    internal sealed class ClaimedRow
    {
        public Guid RunId { get; set; }
        public Guid TenantId { get; set; }
    }
}