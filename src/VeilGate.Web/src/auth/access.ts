import type { SessionState } from "../lib/session";

export type Decision = "wait" | "login" | "forbidden" | "allow";

export function decideAccess(
  session: SessionState,
  allowedRoles?: readonly string[],
): Decision {
  if (session.status === "loading") return "wait";
  if (session.status === "signedOut") return "login";
  if (allowedRoles && !allowedRoles.includes(session.claims.role))
    return "forbidden";
  return "allow";
}
