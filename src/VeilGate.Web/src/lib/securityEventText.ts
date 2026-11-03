import type { BadgeTone } from "../components/Badge";
import type { SecurityEvent, Severity } from "./securityEventsApi";

/** How serious an event looks: the badge colour and which icon to draw (the word itself is always shown too). */
export type SeverityLook = { tone: BadgeTone; level: "info" | "warning" | "critical" };

export function severityLook(severity: Severity): SeverityLook {
  switch (severity) {
    case "Critical":
    case "High":
      return { tone: "danger", level: "critical" };
    case "Medium":
      return { tone: "warning", level: "warning" };
    default:
      return { tone: "muted", level: "info" };
  }
}

const TITLES: Record<string, string> = {
  "auth.account_locked": "Account locked after repeated failed sign-ins",
  "mfa.reset": "Two-step sign-in was reset",
  "mfa.recovery_codes_regenerated": "Recovery codes were replaced",
  "auth.password_changed": "Password was changed",
  "oauth.invalid_secret": "An API client used a wrong secret",
  "oauth.ip_denied": "An API client called from a blocked address",
  "leak.gate_tripped": "A leak was stopped before it left the gateway",
  "output.leak": "Possible leak in a model's answer",
};

/** A sentence for the types we know. Anything new shows its raw type, which is still readable, instead of nothing. */
export const eventTitle = (type: string): string => TITLES[type] ?? type;

/** Who the event is about, for the "Person" column. */
export function eventSubject(event: SecurityEvent): string {
  if (event.userId) return event.userName ?? "A person who no longer exists";
  if (event.clientId) return event.clientName ? `API client ${event.clientName}` : "An API client that no longer exists";
  return "—";
}
