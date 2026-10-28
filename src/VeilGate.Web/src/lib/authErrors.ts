import { ApiError } from "./api";

const GENERIC = "Something went wrong. Try again.";

/** What to tell someone whose sign-in failed. It never says whether the email exists. */
export function loginErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return GENERIC;
  switch (error.status) {
    case 0:
      return "Could not reach the server. Check your connection and try again.";
    case 401:
      return "Email or password is incorrect";
    case 423:
      return "Account temporarily locked. Try again in 15 minutes";
    case 429:
      return error.retryAfter
        ? `Too many attempts. Wait ${error.retryAfter} seconds`
        : "Too many attempts. Wait a moment and try again";
    default:
      return GENERIC;
  }
}

export type MfaOutcome =
  | { kind: "wrong_code"; message: string }
  | { kind: "expired" } // the short-lived token is no longer good: start again at sign-in
  | { kind: "locked" }
  | { kind: "failed"; message: string }; // rate limit, network, anything else: stay on the page and say so

/** What a failed code check means for the screen. */
export function mfaErrorOutcome(error: unknown): MfaOutcome {
  if (!(error instanceof ApiError)) return { kind: "failed", message: GENERIC };
  switch (error.status) {
    case 401:
      return error.code === "mfa.code_invalid"
        ? { kind: "wrong_code", message: "Code is incorrect or expired" }
        : { kind: "expired" };
    case 423:
      return { kind: "locked" };
    case 429:
      return {
        kind: "failed",
        message: error.retryAfter
          ? `Too many attempts. Wait ${error.retryAfter} seconds`
          : "Too many attempts. Wait a moment and try again",
      };
    case 0:
      return {
        kind: "failed",
        message:
          "Could not reach the server. Check your connection and try again.",
      };
    case 400:
    case 422:
      return { kind: "failed", message: error.message };
    default:
      return { kind: "failed", message: GENERIC };
  }
}
