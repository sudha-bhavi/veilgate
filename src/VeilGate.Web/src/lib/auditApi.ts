import { apiFetch } from "./api";
import type { Paged } from "./policiesApi";

export type AuditActorType = "user" | "client" | "system";
export type AuditResult = "success" | "failure" | "denied";

export type AuditEvent = {
  id: string;
  seq: number;
  occurredAt: string;
  actorType: AuditActorType;
  actorId: string | null;
  actorName: string | null; // the person's or client's name when it still exists
  action: string;
  resourceType: string | null;
  resourceId: string | null;
  result: AuditResult;
  ip: string | null;
  details: Record<string, string>;
  hash: string;
};

/** What the server's chain check found. If not ok, brokenAtSeq is the first event that was deleted, changed or moved. */
export type ChainCheck = {
  ok: boolean;
  verified: number;
  brokenAtSeq: number | null;
  expectedHash: string | null;
  actualHash: string | null;
};

export type AuditQuery = {
  action?: string;
  actorType?: string;
  result?: string;
  resourceId?: string;
  /** Days as the date picker gives them ("2026-10-29"), in the person's own time zone. */
  fromDay?: string;
  toDay?: string;
  page?: number;
  pageSize?: number;
};

/**
 * Turns picked days into the exact range the server compares against: the start of the first day and the last millisecond
 * of the last day, in the person's time zone, sent as UTC. An empty or unreadable day is left out.
 */
export function dayRange(fromDay?: string, toDay?: string): { from?: string; to?: string } {
  const at = (day: string | undefined, time: string) => {
    if (!day || !/^\d{4}-\d{2}-\d{2}$/.test(day)) return undefined;
    const date = new Date(`${day}T${time}`); // no "Z": read as local time
    return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
  };
  return { from: at(fromDay, "00:00:00"), to: at(toDay, "23:59:59.999") };
}

export function listAuditEvents(query: AuditQuery = {}): Promise<Paged<AuditEvent>> {
  const params = new URLSearchParams();
  const { from, to } = dayRange(query.fromDay, query.toDay);
  if (from) params.set("from", from);
  if (to) params.set("to", to);
  if (query.action) params.set("action", query.action);
  if (query.actorType) params.set("actorType", query.actorType);
  if (query.result) params.set("result", query.result);
  if (query.resourceId) params.set("resourceId", query.resourceId);
  params.set("page", String(query.page ?? 1));
  params.set("pageSize", String(query.pageSize ?? 50));
  return apiFetch<Paged<AuditEvent>>(`/api/v1/audit-events?${params.toString()}`);
}

export const listAuditActions = async (): Promise<string[]> =>
  (await apiFetch<{ items: string[] }>("/api/v1/audit-events/actions")).items;

export const checkAuditChain = () => apiFetch<ChainCheck>("/api/v1/audit-events/verify");
