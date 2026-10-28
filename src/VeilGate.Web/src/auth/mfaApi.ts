import { apiFetch } from "../lib/api";

export type AccessTokenResponse = {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
};
export type EnrollmentStart = { otpAuthUri: string; manualEntryKey: string };
export type EnrollmentResult = {
  mfaConfirmed: boolean;
  recoveryCodes: string[];
};

// All three use the short-lived token from the password step, not an access token.
export const verifyCode = (code: string, mfaToken: string) =>
  apiFetch<AccessTokenResponse>("/api/v1/auth/mfa/verify", {
    method: "POST",
    body: { code },
    bearer: mfaToken,
  });

export const startEnrollment = (mfaToken: string) =>
  apiFetch<EnrollmentStart>("/api/v1/auth/mfa/totp/start", {
    method: "POST",
    bearer: mfaToken,
  });

export const confirmEnrollment = (code: string, mfaToken: string) =>
  apiFetch<EnrollmentResult>("/api/v1/auth/mfa/totp/confirm", {
    method: "POST",
    body: { code },
    bearer: mfaToken,
  });
