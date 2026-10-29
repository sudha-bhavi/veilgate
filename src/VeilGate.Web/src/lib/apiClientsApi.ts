import { apiFetch } from "./api";
import type { Paged } from "./policiesApi";

export type ApiClientStatus = "Active" | "Revoked";

export type ApiClient = {
  id: string;
  clientId: string;
  name: string;
  scopes: string[];
  status: ApiClientStatus;
  defaultPolicyId: string | null;
  ipAllowlist: string[];
  rateLimitPerMinute: number;
  ownerUserId: string;
  createdAt: string;
};

/** What creating a client returns. The secret is in this response and nowhere else, ever again. */
export type CreatedApiClient = {
  id: string;
  clientId: string;
  clientSecret: string;
  name: string;
  scopes: string[];
  status: ApiClientStatus;
  defaultPolicyId: string | null;
  ipAllowlist: string[];
  rateLimitPerMinute: number;
};

// The four scopes the server accepts (ApiClient.AllowedScopes).
export const API_CLIENT_SCOPES = [
  {
    value: "gateway:invoke",
    label: "Invoke",
    help: "Send chat requests through the gateway.",
  },
  {
    value: "gateway:detect",
    label: "Detect",
    help: "Find sensitive items in text.",
  },
  {
    value: "gateway:deidentify",
    label: "De-identify",
    help: "Mask text and keep a vault session for later.",
  },
  {
    value: "gateway:reidentify",
    label: "Re-identify",
    help: "Put original values back. The most sensitive permission.",
  },
] as const;

// How long the old secret keeps working after a rotation (the server accepts exactly these).
export const GRACE_OPTIONS = [
  { hours: 1, label: "1 hour" },
  { hours: 24, label: "24 hours" },
  { hours: 168, label: "7 days" },
] as const;

export type ApiClientListQuery = {
  status?: string;
  q?: string;
  page?: number;
  pageSize?: number;
};

export function listApiClients(
  query: ApiClientListQuery = {},
): Promise<Paged<ApiClient>> {
  const params = new URLSearchParams();
  if (query.status) params.set("status", query.status);
  if (query.q) params.set("q", query.q);
  params.set("page", String(query.page ?? 1));
  params.set("pageSize", String(query.pageSize ?? 25));
  return apiFetch<Paged<ApiClient>>(`/api/v1/api-clients?${params.toString()}`);
}

export const getApiClient = (id: string) =>
  apiFetch<ApiClient>(`/api/v1/api-clients/${id}`);

export type CreateApiClientBody = {
  name: string;
  scopes: string[];
  defaultPolicyId?: string;
  ipAllowlist?: string[];
  rateLimitPerMinute?: number;
};

/** Needs a recent MFA check: the server answers 401 auth.insufficient_user_authentication until the person steps up. */
export const createApiClient = (body: CreateApiClientBody) =>
  apiFetch<CreatedApiClient>("/api/v1/api-clients", { method: "POST", body });

/** Only the fields that change. A field left out stays as it is; an empty ipAllowlist means "from anywhere". */
export type UpdateApiClientBody = {
  scopes?: string[];
  ipAllowlist?: string[];
  rateLimitPerMinute?: number;
  defaultPolicyId?: string;
};

export const updateApiClient = (id: string, body: UpdateApiClientBody) =>
  apiFetch<ApiClient>(`/api/v1/api-clients/${id}`, { method: "PATCH", body });

/** Also needs a recent MFA check. */
export const rotateSecret = (id: string, graceHours: number) =>
  apiFetch<{ clientSecret: string; previousSecretsEndAt: string | null }>(
    `/api/v1/api-clients/${id}/rotate-secret`,
    { method: "POST", body: { graceHours } },
  );

export const revokeApiClient = (id: string) =>
  apiFetch(`/api/v1/api-clients/${id}`, { method: "DELETE" });
