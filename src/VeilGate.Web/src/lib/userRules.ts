import type { TenantUser } from "./usersApi";

export type UserAction = "role" | "resend" | "reset" | "disable" | "enable";

export const ACTION_LABELS: Record<UserAction, string> = {
  role: "Change role",
  resend: "Resend invite",
  reset: "Reset two-step",
  disable: "Disable",
  enable: "Enable",
};

/**
 * Which actions the row offers for a person. This only decides what to show: the server refuses the rest on its own
 * (you cannot act on your own account, and the last active Tenant Admin is protected).
 */
export function actionsFor(user: TenantUser, selfId: string): UserAction[] {
  if (user.id.toLowerCase() === selfId.toLowerCase()) return [];
  switch (user.status) {
    case "Invited":
      return ["resend", "role", "disable"];
    case "Disabled":
      return ["enable"];
    default:
      return ["role", "reset", "disable"]; // Active, and Locked
  }
}
