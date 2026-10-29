export const ALL_TENANT_ROLES = [
  "TenantAdmin",
  "SecurityOfficer",
  "Developer",
  "Auditor",
] as const;
export const PLAYGROUND_ROLES = [
  "TenantAdmin",
  "SecurityOfficer",
  "Developer",
] as const;

// These three mirror the server's rules (ApiClientManager, LlmProviderAdmin, EvaluationOperator).
export const API_CLIENT_ROLES = ["TenantAdmin", "Developer"] as const;
export const PROVIDER_ROLES = ["TenantAdmin"] as const;
export const EVALUATION_ROLES = [
  "TenantAdmin",
  "SecurityOfficer",
  "Developer",
] as const;
