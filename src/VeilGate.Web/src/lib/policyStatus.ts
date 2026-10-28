import type { PolicyStatus } from "./policiesApi";

export const POLICY_STATUSES: readonly PolicyStatus[] = [
  "Draft",
  "PendingApproval",
  "Published",
  "Archived",
];

const LABELS: Record<PolicyStatus, string> = {
  Draft: "Draft",
  PendingApproval: "Pending approval",
  Published: "Published",
  Archived: "Archived",
};

export const statusLabel = (status: string): string =>
  LABELS[status as PolicyStatus] ?? status;
