import { apiFetch } from "../lib/api";

export type LoginResult = {
  mfaRequired: boolean;
  mfaEnrollmentRequired: boolean;
  mfaToken: string | null;
};

export const login = (email: string, password: string) =>
  apiFetch<LoginResult>("/api/v1/auth/login", {
    method: "POST",
    body: { email, password },
  });
