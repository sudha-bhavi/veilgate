import { apiFetch } from "./api";
import type { Paged } from "./policiesApi";

export const SEVERITIES = ["Info", "Low", "Medium", "High", "Critical"] as const;
export type Severity = (typeof SEVERITIES)[number];

export type SecurityEvent = {
  id: string;
  occurredAt: string;
  severity: Severity;
  type: string;
  userId: string | null;
  userName: string | null; // "Name (email)" while the person still exists
  clientId: string | null;
  clientName: string | null;
  ip: string | null;
  details: Record<string, string>;
};

export type SecurityEventPerson = { id: string; name: string; email: string };

export type SecurityEventQuery = {
  userId?: string;
  severity?: string;
  page?: number;
  pageSize?: number;
};

/** Everyone sees their own events. Tenant Admins and Security Officers see the whole organization and may pass a userId. */
export function listSecurityEvents(query: SecurityEventQuery = {}): Promise<Paged<SecurityEvent>> {
  const params = new URLSearchParams();
  if (query.userId) params.set("userId", query.userId);
  if (query.severity) params.set("severity", query.severity);
  params.set("page", String(query.page ?? 1));
  params.set("pageSize", String(query.pageSize ?? 25));
  return apiFetch<Paged<SecurityEvent>>(`/api/v1/security-events?${params.toString()}`);
}

/** For the "User" filter. Only Tenant Admins and Security Officers may ask; anyone else gets 403. */
export const listSecurityEventPeople = async (): Promise<SecurityEventPerson[]> =>
  (await apiFetch<{ items: SecurityEventPerson[] }>("/api/v1/security-events/people")).items;
