export const POLICY_AUTHOR_ROLES = ["TenantAdmin", "SecurityOfficer"] as const;
export const POLICY_REVIEWER_ROLES = ["SecurityOfficer"] as const;

const has = (roles: readonly string[], role: string) => roles.includes(role);

export const canAuthorPolicies = (role: string) =>
  has(POLICY_AUTHOR_ROLES, role);
export const canReviewPolicies = (role: string) =>
  has(POLICY_REVIEWER_ROLES, role);
