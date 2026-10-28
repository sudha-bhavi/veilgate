import { decodeClaims, type Claims } from "./claims";

export type SessionState =
  | { status: "loading" } // the page just opened and the server has not been asked yet
  | { status: "signedOut" }
  | { status: "signedIn"; token: string; claims: Claims };

let state: SessionState = { status: "loading" };
const listeners = new Set<() => void>();

function set(next: SessionState) {
  state = next;
  listeners.forEach((listener) => listener());
}

export const getSession = (): SessionState => state;

export function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export const getAccessToken = (): string | null =>
  state.status === "signedIn" ? state.token : null;

export function setAccessToken(token: string): void {
  const claims = decodeClaims(token);
  if (claims) set({ status: "signedIn", token, claims });
  else set({ status: "signedOut" }); // a token we cannot read is no session
}

export function clearSession(): void {
  set({ status: "signedOut" });
}

/** For tests only: back to "page just opened". */
export function resetSessionForTests(): void {
  set({ status: "loading" });
}
