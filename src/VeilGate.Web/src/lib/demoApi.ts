import type { AccessTokenResponse } from "../auth/mfaApi";
import { apiFetch } from "./api";

/**
 * Whether this server offers "Try the demo". Any failure means "no": a missing button is better than a broken sign-in page.
 * This is a plain fetch on purpose, not apiFetch: nobody is signed in here, so there is nothing to refresh.
 */
export async function demoIsAvailable(): Promise<boolean> {
  try {
    const response = await fetch("/api/v1/system/info");
    if (!response.ok) return false;
    const info = (await response.json()) as { demoEnabled?: unknown };
    return info.demoEnabled === true;
  } catch {
    return false;
  }
}

/** Signs in as the demo user. The server sets the refresh cookie; the access token is in the answer. */
export const startDemo = () =>
  apiFetch<AccessTokenResponse>("/api/v1/auth/demo", {
    method: "POST",
    refresh: false,
  });
