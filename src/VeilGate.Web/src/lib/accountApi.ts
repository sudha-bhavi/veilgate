import { apiFetch } from "./api";

export type MfaStatus = {
  /** The authenticator app, or null when none is confirmed. */
  authenticator: { addedAt: string } | null;
  recoveryCodesLeft: number;
  recoveryCodesTotal: number;
};

export const getMfaStatus = () => apiFetch<MfaStatus>("/api/v1/auth/mfa/factors");

/** Needs a recent MFA check: the server answers 401 auth.insufficient_user_authentication until the person steps up. The old codes stop working. */
export const regenerateRecoveryCodes = async (): Promise<string[]> =>
  (await apiFetch<{ recoveryCodes: string[] }>("/api/v1/auth/mfa/recovery-codes/regenerate", { method: "POST" })).recoveryCodes;

/** A wrong current password is 400 password.current_incorrect and counts toward the account lockout. */
export const changePassword = (currentPassword: string, newPassword: string) =>
  apiFetch("/api/v1/auth/password/change", {
    method: "POST",
    body: { currentPassword, newPassword },
  });
