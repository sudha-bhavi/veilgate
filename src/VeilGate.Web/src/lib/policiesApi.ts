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
