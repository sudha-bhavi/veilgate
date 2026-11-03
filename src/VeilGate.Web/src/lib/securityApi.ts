import { apiFetch } from "./api";

/** One signed-in device of the current person. `userAgent` is the browser's own text: use describeDevice to show it. */
export type SessionRow = {
  id: string;
  userAgent: string | null;
  ip: string | null;
  createdAt: string;
  lastActiveAt: string;
  /** True for the session this page is using. */
  current: boolean;
};

export const listSessions = () => apiFetch<SessionRow[]>("/api/v1/auth/sessions");

/** Another session of yours. The server refuses the one in use (409 session.current). */
export const revokeSession = (id: string) =>
  apiFetch(`/api/v1/auth/sessions/${id}`, { method: "DELETE" });

/** Needs a recent MFA check: the server answers 401 auth.insufficient_user_authentication until the person steps up. */
export const signOutOtherSessions = () =>
  apiFetch<{ ended: number }>("/api/v1/auth/logout-all", {
    method: "POST",
    body: { keepCurrent: true },
  });
