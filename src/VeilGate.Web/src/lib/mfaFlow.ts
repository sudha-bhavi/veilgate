import { expiryOf } from "./claims";

export type MfaPurpose = "verify" | "enroll";

type Flow = { token: string; purpose: MfaPurpose; expiresAt: number }; // expiresAt in milliseconds

/**
 * The short-lived token from the password step, kept in memory only: never in the URL, storage or router state
 * (browser history state survives a reload). It dies with the page, which is what we want.
 */
let flow: Flow | null = null;

const FALLBACK_MS = 5 * 60_000;

export function startMfaFlow(token: string, purpose: MfaPurpose): void {
  const exp = expiryOf(token);
  flow = {
    token,
    purpose,
    expiresAt: exp ? exp * 1000 : Date.now() + FALLBACK_MS,
  };
}

export const getMfaFlow = (): Flow | null => flow;

export function clearMfaFlow(): void {
  flow = null;
}

export function secondsLeft(f: Flow, now = Date.now()): number {
  return Math.max(0, Math.ceil((f.expiresAt - now) / 1000));
}
