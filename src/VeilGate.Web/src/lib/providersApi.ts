import { apiFetch } from "./api";

/** What the API shows of a provider. The key itself never comes back, only its last four characters. */
export type LlmProvider = {
  id: string;
  name: string;
  baseUrl: string;
  keyLast4: string;
  models: string[];
  timeoutSeconds: number;
  priority: number;
  enabled: boolean;
  createdAt: string;
};

export type ProviderBody = {
  name: string;
  baseUrl: string;
  /** Required when creating. When editing, leave it out to keep the stored key. */
  apiKey?: string;
  models: string[];
  timeoutSeconds?: number;
  priority?: number;
  enabled?: boolean;
};

export type ProviderTestResult = {
  ok: boolean;
  latencyMs: number;
  errorCategory: string | null;
};

export const listProviders = async (): Promise<LlmProvider[]> =>
  (await apiFetch<{ items: LlmProvider[] }>("/api/v1/llm-providers")).items;

export const createProvider = (body: ProviderBody) =>
  apiFetch<LlmProvider>("/api/v1/llm-providers", { method: "POST", body });

export const updateProvider = (id: string, body: ProviderBody) =>
  apiFetch<LlmProvider>(`/api/v1/llm-providers/${id}`, {
    method: "PUT",
    body,
  });

/** The failover order: the first id is tried first. */
export const reorderProviders = (ids: string[]) =>
  apiFetch("/api/v1/llm-providers/order", { method: "PUT", body: { ids } });

/** Makes a real call to the provider, so it is limited like gateway traffic. */
export const testProvider = (id: string) =>
  apiFetch<ProviderTestResult>(`/api/v1/llm-providers/${id}/test`, {
    method: "POST",
  });

export const deleteProvider = (id: string) =>
  apiFetch(`/api/v1/llm-providers/${id}`, { method: "DELETE" });
