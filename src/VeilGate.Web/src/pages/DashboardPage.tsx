import { useState, type ReactNode } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import BarChart from "../components/BarChart";
import Button from "../components/Button";
import { describeApiError } from "../lib/apiErrorText";
import {
  METRICS,
  RANGES,
  callerName,
  formatLatency,
  formatLeakRate,
  getDashboardSeries,
  getDashboardSummary,
  readRange,
  type MetricKey,
} from "../lib/dashboardApi";

function Stat({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="rounded-xl border border-line bg-card p-4">
      <dt className="text-muted">{label}</dt>
      <dd className="mt-1 text-2xl font-semibold">{value}</dd>
      {hint && <p className="mt-1 text-xs text-muted">{hint}</p>}
    </div>
  );
}

/**
 * How the gateway is doing: counts only, never text. The numbers come from hourly counters that the metrics consumer keeps, so
 * they lag the gateway by a moment. Latencies are the upper limit of a band, which is why they read "up to".
 */
export default function DashboardPage() {
  const [params, setParams] = useSearchParams();
  const range = readRange(params.get("range"));
  const [metric, setMetric] = useState<MetricKey>("requests");

  const summary = useQuery({
    queryKey: ["dashboard-summary", range],
    queryFn: () => getDashboardSummary(range),
    placeholderData: keepPreviousData,
  });
  const series = useQuery({
    queryKey: ["dashboard-series", range, metric],
    queryFn: () => getDashboardSeries(range, metric),
    placeholderData: keepPreviousData,
  });

  const s = summary.data;
  const byType = Object.entries(s?.entitiesByType ?? {}).sort(([, a], [, b]) => b - a);
  const mostOfAType = Math.max(1, ...byType.map(([, count]) => count));
  const chosen = METRICS.find((m) => m.key === metric)!;

  return (
    <main className="mx-auto max-w-6xl space-y-6 p-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Dashboard</h1>
          <p className="mt-1 text-muted">How the gateway is doing. Counts only: no text is ever kept here.</p>
        </div>
        <div role="group" aria-label="Time range" className="flex gap-2">
          {RANGES.map((option) => (
            <Button
              key={option.key}
              variant={option.key === range ? "primary" : "secondary"}
              aria-pressed={option.key === range}
              onClick={() => setParams(option.key === "24h" ? new URLSearchParams() : new URLSearchParams({ range: option.key }))}
            >
              {option.label}
            </Button>
          ))}
        </div>
      </header>

      {summary.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(summary.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => summary.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {summary.isPending && (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5" aria-label="Loading the dashboard">
          {[0, 1, 2, 3, 4].map((n) => (
            <div key={n} className="h-24 animate-pulse rounded-xl bg-line" />
          ))}
        </div>
      )}

      {s && (
        <>
          <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
            <Stat label="Requests" value={s.requests.toLocaleString()} />
            <Stat label="Items protected" value={s.entitiesProtected.toLocaleString()} />
            <Stat
              label="Blocked"
              value={s.blockedRequests.toLocaleString()}
              hint={`${s.failedRequests.toLocaleString()} failed`}
            />
            <Stat label="Slow calls (p95)" value={formatLatency(s.p95LatencyMs)} hint={`Typical: ${formatLatency(s.p50LatencyMs)}`} />
            <Stat
              label="Latest leak rate"
              value={formatLeakRate(s.latestLeakRate)}
              hint={<Link to="/evaluations" className="text-accent hover:underline">From the newest evaluation</Link>}
            />
          </dl>

          {s.requests === 0 && (
            <div className="rounded-xl border border-line bg-card p-6 text-center">
              <p>No requests in this period yet.</p>
            </div>
          )}
        </>
      )}

      <section aria-labelledby="chart-title" className="rounded-xl border border-line bg-card p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="chart-title" className="text-lg font-semibold">
            Over time
          </h2>
          <div role="group" aria-label="What to chart" className="flex gap-2">
            {METRICS.map((option) => (
              <Button
                key={option.key}
                variant={option.key === metric ? "primary" : "secondary"}
                aria-pressed={option.key === metric}
                onClick={() => setMetric(option.key)}
              >
                {option.label}
              </Button>
            ))}
          </div>
        </div>
        <div className="mt-4">
          {series.isError && (
            <p role="alert" className="text-danger">
              {describeApiError(series.error)}
            </p>
          )}
          {series.data && (
            <BarChart points={series.data.points} range={range} label={chosen.label} unit={metric === "latency" ? " ms" : ""} />
          )}
          {range !== "24h" && <p className="mt-2 text-xs text-muted">Days are UTC days.</p>}
        </div>
      </section>

      {s && (
        <div className="grid gap-6 lg:grid-cols-2">
          <section aria-labelledby="types-title" className="rounded-xl border border-line bg-card p-4">
            <h2 id="types-title" className="text-lg font-semibold">
              What was protected
            </h2>
            {byType.length === 0 ? (
              <p className="mt-3 text-muted">Nothing was protected in this period.</p>
            ) : (
              <ul className="mt-3 space-y-2">
                {byType.map(([type, count]) => (
                  <li key={type}>
                    <div className="flex justify-between">
                      <span className="font-mono text-[13px]">{type}</span>
                      <span>{count.toLocaleString()}</span>
                    </div>
                    <div className="mt-1 h-2 rounded bg-line" aria-hidden="true">
                      <div className="h-2 rounded bg-accent" style={{ width: `${(count / mostOfAType) * 100}%` }} />
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section aria-labelledby="callers-title" className="rounded-xl border border-line bg-card p-4">
            <h2 id="callers-title" className="text-lg font-semibold">
              Busiest callers
            </h2>
            {s.topClients.length === 0 ? (
              <p className="mt-3 text-muted">No calls in this period.</p>
            ) : (
              <ol className="mt-3 space-y-2">
                {s.topClients.map((client) => (
                  <li key={client.clientId} className="flex justify-between gap-4">
                    <span>{callerName(client)}</span>
                    <span className="text-muted">{client.requests.toLocaleString()} requests</span>
                  </li>
                ))}
              </ol>
            )}
            <Link to="/requests" className="mt-4 inline-block text-accent hover:underline">
              See all requests
            </Link>
            {" · "}
            <Link to="/security" className="inline-block text-accent hover:underline">
              Security events
            </Link>
          </section>
        </div>
      )}
    </main>
  );
}
