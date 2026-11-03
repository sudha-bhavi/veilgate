import type { LoginResult } from "../auth/authApi";
import { apiFetch } from "./api";
import type { Paged } from "./policiesApi";

export type UserStatus = "Invited" | "Active" | "Disabled" | "Locked";

export type TenantUser = {
  id: string;
  name: string;
  email: string;
  role: string;
  status: UserStatus;
  /** True when the person has a confirmed authenticator app. */
  hasMfa: boolean;
  lastLoginAt: string | null;
};

// The roles a Tenant Admin may hand out. The server refuses PlatformAdmin, which belongs to the VeilGate operator.
export const ASSIGNABLE_ROLES = [
  {
    value: "TenantAdmin",
    label: "Tenant Admin",
    help: "Manages people, API clients and providers.",
  },
  {
    value: "SecurityOfficer",
    label: "Security Officer",
    help: "Approves and publishes policies and runs evaluations.",
  },
  {
    value: "Developer",
    label: "Developer",
    help: "Uses the Playground and manages their own API clients.",
  },
  {
    value: "Auditor",
    label: "Auditor",
    help: "Read-only access to the audit log and evaluations.",
  },
] as const;

export const roleLabel = (role: string): string =>
  ASSIGNABLE_ROLES.find((r) => r.value === role)?.label ?? role;

export type UserListQuery = {
  role?: string;
  status?: string;
  q?: string;
  page?: number;
  pageSize?: number;
};

export function listUsers(query: UserListQuery = {}): Promise<Paged<TenantUser>> {
  const params = new URLSearchParams();
  if (query.role) params.set("role", query.role);
  if (query.status) params.set("status", query.status);
  if (query.q) params.set("q", query.q);
  params.set("page", String(query.page ?? 1));
  params.set("pageSize", String(query.pageSize ?? 25));
  return apiFetch<Paged<TenantUser>>(`/api/v1/users?${params.toString()}`);
}

/** The person exists straight away with status Invited. emailSent is false when the email could not be sent: use resend. */
export type InviteResult = {
  id: string;
  email: string;
  role: string;
  status: UserStatus;
  inviteExpiresAt: string;
  emailSent: boolean;
};

export const inviteUser = (email: string, role: string) =>
  apiFetch<InviteResult>("/api/v1/users", {
    method: "POST",
    body: { email, role },
  });

/** Needs a recent MFA check: the server answers 401 auth.insufficient_user_authentication until the person steps up. */
export const changeUserRole = (id: string, role: string) =>
  apiFetch<TenantUser>(`/api/v1/users/${id}`, {
    method: "PATCH",
    body: { role },
  });

export const disableUser = (id: string) =>
  apiFetch(`/api/v1/users/${id}/disable`, { method: "POST" });

export const enableUser = (id: string) =>
  apiFetch(`/api/v1/users/${id}/enable`, { method: "POST" });

export const resendInvite = (id: string) =>
  apiFetch<{ emailSent: boolean; inviteExpiresAt: string }>(
    `/api/v1/users/${id}/invite/resend`,
    { method: "POST" },
  );

/** Also needs a recent MFA check. */
export const resetMfa = (id: string) =>
  apiFetch(`/api/v1/users/${id}/mfa/reset`, { method: "POST" });

// ---- the emailed link: nobody is signed in on these two calls ----

export type Invitation = {
  tenantName: string;
  email: string;
  role: string;
  expiresAt: string;
};

/** The token goes in the query string, not the path: the server's request log records the path. */
export const getInvitation = (token: string) =>
  apiFetch<Invitation>(
    `/api/v1/auth/invitations?token=${encodeURIComponent(token)}`,
    { refresh: false },
  );

/** The answer has the same shape as the password step of sign-in, so the page carries on into two-step setup. */
export const acceptInvitation = (body: {
  token: string;
  fullName: string;
  password: string;
}) =>
  apiFetch<LoginResult>("/api/v1/users/accept-invite", {
    method: "POST",
    body,
    refresh: false,
  });
