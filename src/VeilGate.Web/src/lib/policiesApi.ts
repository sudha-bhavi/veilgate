import { apiFetch } from "./api";

export type PolicyStatus =
  | "Draft"
  | "PendingApproval"
  | "Published"
  | "Archived";

export type PolicyListItem = {
  id: string;
  name: string;
  status: PolicyStatus;
  currentVersion: number | null;
  hasDraft: boolean;
  createdAt: string;
};

export type Paged<T> = {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
};

export type PolicyListQuery = {
  status?: string;
  q?: string;
  page?: number;
  pageSize?: number;
};

export function listPolicyPage(
  query: PolicyListQuery = {},
): Promise<Paged<PolicyListItem>> {
  const params = new URLSearchParams();
  if (query.status) params.set("status", query.status);
  if (query.q) params.set("q", query.q);
  params.set("page", String(query.page ?? 1));
  params.set("pageSize", String(query.pageSize ?? 25));
  return apiFetch<Paged<PolicyListItem>>(
    `/api/v1/policies?${params.toString()}`,
  );
}

// The three starting points the server accepts (PolicyTemplates.cs).
export const POLICY_TEMPLATES = [
  {
    value: "blank",
    label: "Blank",
    help: "Start empty and add your own rules.",
  },
  {
    value: "hipaa-safe-harbor",
    label: "HIPAA Safe Harbor",
    help: "Identifiers redacted, people tokenized, dates shifted.",
  },
  {
    value: "names-only",
    label: "Names only",
    help: "Hide people, let everything else through.",
  },
] as const;

export type CreatePolicyInput = {
  name: string;
  template: string;
  description?: string;
};

export const createPolicy = (input: CreatePolicyInput) =>
  apiFetch<{ id: string; etag: string }>("/api/v1/policies", {
    method: "POST",
    body: {
      name: input.name,
      template: input.template,
      description: input.description || undefined,
    },
  });
export type PolicyVersionView = {
  id: string;
  versionNo: number;
  state: string;
  yaml: string;
  summary: string | null;
  publishedAt: string | null;
};

export type PolicyDetail = {
  id: string;
  name: string;
  description: string | null;
  status: PolicyStatus;
  current: PolicyVersionView | null;
  draft: PolicyVersionView | null;
  etag: string;
  createdAt: string;
};

export const getPolicy = (id: string) =>
  apiFetch<PolicyDetail>(`/api/v1/policies/${id}`);

/** The ETag is the "version I started from": the server refuses the save if someone changed the policy meanwhile. */
export const saveDraft = (id: string, yaml: string, etag: string) =>
  apiFetch<{ etag: string; versionNo: number }>(
    `/api/v1/policies/${id}/draft`,
    {
      method: "PUT",
      body: { yaml },
      headers: { "If-Match": etag },
    },
  );

/** What a person would edit: the draft if there is one, otherwise the published text. */
export const editableYaml = (policy: PolicyDetail): string =>
  (policy.draft ?? policy.current)?.yaml ?? "";
