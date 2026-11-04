import { apiFetch } from "./api";

export const RANGES = [
  { key: "24h", label: "Last 24 hours", bucket: "hour" },
  { key: "7d", label: "Last 7 days", bucket: "day" },
  { key: "30d", label: "Last 30 days", bucket: "day" },
] as const;
export type RangeKey = (typeof RANGES)[number]["key"];

export const METRICS = [
  { key: "requests", label: "Requests" },
  { key: "entities", label: "Protected items" },
  { key: "latency", label: "Slow calls (p95)" },
] as const;
export type MetricKey = (typeof METRICS)[number]["key"];

export type ClientTotal = { clientId: string; name: string | null; requests: number };

export type DashboardSummary = {
  range: RangeKey;
  from: string;
  to: string;
  requests: number;
  entitiesProtected: number;
  blockedRequests: number;
  failedRequests: number;
  p50LatencyMs: number | null; // the upper limit of a latency band, not an exact time
  p95LatencyMs: number | null;
  latestLeakRate: number | null;
  entitiesByType: Record<string, number>;
  topClients: ClientTotal[];
};

export type SeriesPoint = { t: string; value: number | null };
export type DashboardSeries = { range: RangeKey; metric: MetricKey; points: SeriesPoint[] };

export const getDashboardSummary = (range: RangeKey) =>
  apiFetch<DashboardSummary>(`/api/v1/dashboard/summary?range=${range}`);

export const getDashboardSeries = (range: RangeKey, metric: MetricKey) =>
  apiFetch<DashboardSeries>(`/api/v1/dashboard/timeseries?range=${range}&metric=${metric}`);

/** Reads ?range= from the address bar; anything odd means the last 24 hours. */
export function readRange(value: string | null): RangeKey {
  return RANGES.find((range) => range.key === value)?.key ?? "24h";
}

/** A latency is the upper limit of a band, so it is shown as "up to". */
export const formatLatency = (ms: number | null): string => (ms === null ? "–" : `up to ${ms.toLocaleString()} ms`);

export const formatLeakRate = (rate: number | null): string =>
  rate === null ? "No evaluation yet" : `${(rate * 100).toFixed(1)}%`;

/** The label under a chart point: the hour for the 24-hour range, the day otherwise. */
export function pointLabel(t: string, range: RangeKey): string {
  const date = new Date(t);
  return range === "24h"
    ? date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : date.toLocaleDateString([], { month: "short", day: "numeric" });
}

/** The callers' names, with a plain fallback for one that no longer exists. */
export const callerName = (client: ClientTotal): string => client.name ?? `Caller ${client.clientId.slice(0, 8)} (no longer exists)`;
