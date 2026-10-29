import type { PolicyDetail } from "../lib/policiesApi";
import { canAuthorPolicies, canReviewPolicies } from "./policyAccess";

export type WorkflowActions = {
  submit: boolean;
  publish: boolean;
  reject: boolean;
  archive: boolean;
  waitingForReview: boolean;
};

/** Which workflow buttons to show. The server enforces the same rules; this only avoids offering what would be refused. */
export function workflowActions(
  policy: PolicyDetail,
  role: string,
): WorkflowActions {
  const open = policy.status !== "Archived";
  const pending = policy.draft?.state === "PendingApproval";
  const reviewer = canReviewPolicies(role);
  return {
    submit: open && canAuthorPolicies(role) && policy.draft?.state === "Draft",
    publish: open && pending && reviewer,
    reject: open && pending && reviewer,
    archive: open && canAuthorPolicies(role),
    waitingForReview: open && pending && !reviewer,
  };
}
