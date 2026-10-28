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
