import { useState, type ReactNode } from "react";
import { Link, useParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Badge from "../components/Badge";
import EvalStateBadge from "../components/EvalStateBadge";
import Pager from "../components/Pager";
import { describeApiError } from "../lib/apiErrorText";
import { isActive, modeLabel, percent, progressPercent } from "../lib/evalFormat";
import {
  getEvaluation,
  getLeaks,
  listDatasets,
  listEvaluablePolicies,
} from "../lib/evaluationsApi";
import { pageCount } from "../lib/paging";

const LEAK_PAGE_SIZE = 50;

function Metric({ label, value, hint }: { label: string; value: ReactNode; hint?: string }) {
  return (
    <div className="rounded-xl border border-line bg-card p-4">
      <p className="text-xs uppercase tracking-wide text-muted">{label}</p>
      <p className="mt-1 text-2xl font-semibold">{value}</p>
      {hint && <p className="mt-1 text-xs text-muted">{hint}</p>}
    </div>
  );
}

export default function EvaluationDetailPage() {
  const { id = "" } = useParams();
  const [leakPage, setLeakPage] = useState(1);

  const run = useQuery({
    queryKey: ["evaluation", id],
    queryFn: () => getEvaluation(id),
    // Keep asking every couple of seconds until the run has finished.
    refetchInterval: (query) => {
      const state = query.state.data?.state;
      return state && isActive(state) ? 2000 : false;
    },
  });
  const datasets = useQuery({ queryKey: ["datasets"], queryFn: listDatasets });
  const policies = useQuery({
    queryKey: ["policies", "evaluable"],
    queryFn: listEvaluablePolicies,
  });

  const result = run.data?.result ?? null;
  const leaks = useQuery({
    queryKey: ["evaluation-leaks", id, leakPage],
    queryFn: () => getLeaks(id, leakPage, LEAK_PAGE_SIZE),
    enabled: run.data?.state === "Succeeded" && (result?.leaks ?? 0) > 0,
    placeholderData: keepPreviousData,
  });

  if (run.isPending) {
    return (
      <main className="mx-auto max-w-5xl p-6">
        <div className="h-64 animate-pulse rounded-xl bg-line" />
      </main>
    );
  }

  if (run.isError) {
    return (
      <main className="mx-auto max-w-5xl space-y-3 p-6">
        <p role="alert" className="text-danger">
          {describeApiError(run.error)}
        </p>
        <Link to="/evaluations" className="text-accent underline">
          Back to evaluations
        </Link>
      </main>
    );
  }

  const current = run.data;
  const policyName =
    policies.data?.find((policy) => policy.id === current.policyId)?.name ?? "Policy";
  const datasetName =
    datasets.data?.find((dataset) => dataset.id === current.datasetId)?.name ?? "dataset";
  const percentDone = progressPercent(current.progress.done, current.progress.total);

  return (
    <main className="mx-auto max-w-5xl space-y-6 p-6">
      <Link to="/evaluations" className="text-muted hover:text-fg">
        ← Evaluations
      </Link>

      <header className="space-y-1">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-xl font-semibold">
            {policyName} v{current.policyVersion}
          </h1>
          <EvalStateBadge state={current.state} />
        </div>
        <p className="text-muted">
          On {datasetName} · {modeLabel(current.mode)} · recall target{" "}
          {percent(current.recallThreshold)} ·{" "}
          <Link to={`/policies/${current.policyId}`} className="text-accent hover:underline">
            open the policy
          </Link>
        </p>
      </header>

      {isActive(current.state) && (
        <section className="space-y-2 rounded-xl border border-line bg-card p-4">
          <p>
            {current.state === "Queued"
              ? "Waiting for a worker to pick this up…"
              : `Checking note ${current.progress.done} of ${current.progress.total}…`}
          </p>
          <div
            role="progressbar"
            aria-label="Evaluation progress"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percentDone}
            className="h-2 overflow-hidden rounded-full bg-line"
          >
            <div className="h-full bg-brand" style={{ width: `${percentDone}%` }} />
          </div>
          <p className="text-xs text-muted">This page updates by itself.</p>
        </section>
      )}

      {current.state === "Failed" && (
        <p role="alert" className="rounded-xl border border-danger p-4 text-danger">
          This run failed (code: {current.errorCode ?? "unknown"}). Start it again
          from the Evaluations page.
        </p>
      )}

      {result && (
        <>
          <section aria-label="Results" className="grid gap-4 sm:grid-cols-3">
            <Metric
              label="Recall"
              value={percent(result.recall)}
              hint="Share of sensitive items that were found"
            />
            <Metric
              label="Precision"
              value={percent(result.precision)}
              hint="Share of what was found that really was sensitive"
            />
            <Metric label="F1 score" value={percent(result.f1)} />
            <Metric label="Leak rate" value={percent(result.leakRate, 2)} />
            <Metric label="Leaks" value={result.leaks} hint="Sensitive values that got through" />
            <Metric
              label="Target"
              value={
                <Badge tone={result.gatePassed ? "success" : "danger"}>
                  {result.gatePassed ? "Passed" : "Missed"}
                </Badge>
              }
              hint={`Recall target ${percent(current.recallThreshold)}`}
            />
          </section>

          <section className="space-y-2">
            <h2 className="text-base font-semibold">By type</h2>
            <div className="overflow-x-auto rounded-xl border border-line bg-card">
              <table className="w-full text-left">
                <caption className="sr-only">Results by type of sensitive item</caption>
                <thead className="text-xs uppercase tracking-wide text-muted">
                  <tr>
                    <th className="px-4 py-2 font-medium">Type</th>
                    <th className="px-4 py-2 font-medium">Recall</th>
                    <th className="px-4 py-2 font-medium">Precision</th>
                    <th className="px-4 py-2 font-medium">Found</th>
                    <th className="px-4 py-2 font-medium">Wrongly flagged</th>
                    <th className="px-4 py-2 font-medium">Missed</th>
                  </tr>
                </thead>
                <tbody>
                  {Object.entries(result.perType)
                    .sort(([a], [b]) => a.localeCompare(b))
                    .map(([type, report]) => (
                      <tr key={type} className="border-t border-line">
                        <td className="px-4 py-2 font-medium">{type}</td>
                        <td className="px-4 py-2">{percent(report.recall)}</td>
                        <td className="px-4 py-2">{percent(report.precision)}</td>
                        <td className="px-4 py-2 text-muted">{report.tp}</td>
                        <td className="px-4 py-2 text-muted">{report.fp}</td>
                        <td className="px-4 py-2 text-muted">{report.fn}</td>
                      </tr>
                    ))}
                </tbody>
              </table>
            </div>
          </section>

          <section className="space-y-2">
            <h2 className="text-base font-semibold">Leaks</h2>
            {result.leaks === 0 ? (
              <p className="text-muted">No sensitive value got through.</p>
            ) : (
              <>
                {leaks.isError && (
                  <p role="alert" className="text-danger">
                    {describeApiError(leaks.error)}
                  </p>
                )}
                {leaks.data && (
                  <div className="overflow-x-auto rounded-xl border border-line bg-card">
                    <table className="w-full text-left">
                      <caption className="sr-only">Sensitive values that got through</caption>
                      <thead className="text-xs uppercase tracking-wide text-muted">
                        <tr>
                          <th className="px-4 py-2 font-medium">Note</th>
                          <th className="px-4 py-2 font-medium">Type</th>
                          <th className="px-4 py-2 font-medium">Position</th>
                          <th className="px-4 py-2 font-medium">Why</th>
                          <th className="px-4 py-2 font-medium">Value</th>
                        </tr>
                      </thead>
                      <tbody>
                        {leaks.data.items.map((leak, index) => (
                          <tr key={index} className="border-t border-line">
                            <td className="px-4 py-2 text-muted">{leak.note ?? "—"}</td>
                            <td className="px-4 py-2 font-medium">{leak.type}</td>
                            <td className="px-4 py-2 text-muted">
                              {leak.start}–{leak.end}
                            </td>
                            <td className="px-4 py-2">{leak.reason}</td>
                            <td className="px-4 py-2 font-mono text-[13px]">{leak.value ?? "—"}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                <Pager
                  page={leakPage}
                  pages={pageCount(result.leaks, LEAK_PAGE_SIZE)}
                  summary={`${result.leaks} ${result.leaks === 1 ? "leak" : "leaks"}`}
                  onPage={setLeakPage}
                />
                <p className="text-xs text-muted">
                  The notes are made up, so the values shown here are not real
                  patient data.
                </p>
              </>
            )}
          </section>
        </>
      )}
    </main>
  );
}
