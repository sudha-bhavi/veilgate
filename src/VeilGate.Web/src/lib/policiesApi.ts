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

export type PolicyIssue = { line: number; column: number; message: string };

export type PolicyValidation = {
  valid: boolean;
  errors: PolicyIssue[];
  warnings: string[];
};

/** Checks the saved text (the draft, or the published version when there is no draft). */
export const validatePolicy = (id: string) =>
  apiFetch<PolicyValidation>(`/api/v1/policies/${id}/validate`, {
    method: "POST",
  });

export const submitPolicy = (id: string, summary: string) =>
  apiFetch(`/api/v1/policies/${id}/submit`, {
    method: "POST",
    body: { summary },
  });

/** Needs a recent MFA check: the server answers 401 auth.insufficient_user_authentication until the person steps up. */
export const publishPolicy = (id: string) =>
  apiFetch<{ versionNumber: number }>(`/api/v1/policies/${id}/publish`, {
    method: "POST",
  });

export const rejectPolicy = (id: string, reason: string) =>
  apiFetch(`/api/v1/policies/${id}/reject`, {
    method: "POST",
    body: { reason },
  });

export const archivePolicy = (id: string) =>
  apiFetch(`/api/v1/policies/${id}/archive`, { method: "POST" });

/** Also needs a recent MFA check. */
export const rollbackPolicy = (id: string, versionId: string) =>
  apiFetch<{ versionNumber: number }>(`/api/v1/policies/${id}/rollback`, {
    method: "POST",
    body: { versionId },
  });

export type PolicyVersionItem = {
  id: string;
  versionNo: number;
  state: string;
  createdBy: string;
  submittedBy: string | null;
  approvedBy: string | null;
  publishedAt: string | null;
  summary: string | null;
};

export const listVersions = (id: string) =>
  apiFetch<Paged<PolicyVersionItem>>(
    `/api/v1/policies/${id}/versions?page=1&pageSize=100`,
  );

export const getDiff = (id: string, from: number, to: number) =>
  apiFetch<{ from: number; to: number; unifiedDiff: string }>(
    `/api/v1/policies/${id}/diff?from=${from}&to=${to}`,
  );

export type TestedEntity = {
  type: string;
  start: number;
  end: number;
  confidence: number;
  action: string;
};

export type PolicyTestResult = {
  entities: TestedEntity[];
  maskedText: string | null;
  blocked: boolean;
  blockedTypes: string[];
};

/** Runs sample text through the saved draft (or the published version) without storing anything. */
export const testPolicy = (id: string, text: string) =>
  apiFetch<PolicyTestResult>(`/api/v1/policies/${id}/test`, {
    method: "POST",
    body: { text },
  });
