import { apiFetch } from "./api";
import { dayRange } from "./auditApi";
import type { Paged } from "./policiesApi";

export type RequestStatus = "success" | "failure" | "denied";

/** One gateway call: counts and names only, never the text that went through. */
export type GatewayRequest = {
  id: string;
  requestId: string;
  occurredAt: string;
  actorType: "user" | "client";
  clientId: string;
  clientName: string | null; // null when the client or person no longer exists
  action: string;
  status: RequestStatus;
  reason: string | null; // why a call was denied or failed
  model: string | null;
  provider: string | null;
  policyVersion: string | null;
  entityCount: number;
  entities: Record<string, number>;
  latencyMs: number | null;
};

export type RequestQuery = {
  action?: string;
  status?: string;
  requestId?: string;
  /** Days as the date picker gives them ("2026-10-29"), in the person's own time zone. */
  fromDay?: string;
  toDay?: string;
  page?: number;
  pageSize?: number;
};

export function listRequests(query: RequestQuery = {}): Promise<Paged<GatewayRequest>> {
  const params = new URLSearchParams();
  const { from, to } = dayRange(query.fromDay, query.toDay);
  if (from) params.set("from", from);
  if (to) params.set("to", to);
  if (query.action) params.set("action", query.action);
  if (query.status) params.set("status", query.status);
  if (query.requestId) params.set("requestId", query.requestId);
  params.set("page", String(query.page ?? 1));
  params.set("pageSize", String(query.pageSize ?? 50));
  return apiFetch<Paged<GatewayRequest>>(`/api/v1/requests?${params.toString()}`);
}

export const getRequest = (id: string) =>
  apiFetch<GatewayRequest>(`/api/v1/requests/${encodeURIComponent(id)}`);

export const listRequestActions = async (): Promise<string[]> =>
  (await apiFetch<{ items: string[] }>("/api/v1/requests/actions")).items;

/** Who made the call, in words: the name when it still exists, otherwise the kind and id. */
export function requestCaller(request: GatewayRequest): string {
  if (request.clientName) return request.clientName;
  const kind = request.actorType === "client" ? "API client" : "Person";
  return `${kind} ${request.clientId} (no longer exists)`;
}

/** "PHONE 2, SSN 1" for a count per type, or "None" when nothing was protected. */
export function entitySummary(entities: Record<string, number>): string {
  const parts = Object.entries(entities)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([type, count]) => `${type} ${count}`);
  return parts.length === 0 ? "None" : parts.join(", ");
}
