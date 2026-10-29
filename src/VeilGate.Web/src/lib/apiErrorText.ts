import { ApiError } from "./api";

export function describeApiError(error: unknown): string {
  if (!(error instanceof ApiError)) return "Something went wrong. Try again.";
  if (error.status === 429) {
    return error.retryAfter
      ? `Too many requests. Try again in ${error.retryAfter} seconds.`
      : "Too many requests. Wait a moment and try again.";
  }
  if (error.status === 403) return "Your role is not allowed to do this.";
  const fields = error.errors
    ? Object.values(error.errors)
        .flat()
        .filter((message): message is string => typeof message === "string")
    : [];
  return fields.length > 0 ? fields.join(" ") : error.message;
}
