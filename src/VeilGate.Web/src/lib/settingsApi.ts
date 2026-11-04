import { apiFetch } from "./api";

export type Settings = {
  organisation: { name: string; status: "Active" | "Suspended" };
  /** Server-wide rules, not per-organisation choices. */
  rules: {
    accessTokenMinutes: number;
    sessionIdleHours: number;
    sessionMaxDays: number;
    lockoutAttempts: number;
    lockoutMinutes: number;
    mfaRequired: boolean;
  };
};

export const getSettings = () => apiFetch<Settings>("/api/v1/settings");
