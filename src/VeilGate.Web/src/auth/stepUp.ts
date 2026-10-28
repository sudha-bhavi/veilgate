import { ApiError, apiFetch } from "../lib/api";
import { setAccessToken } from "../lib/session";

export const STEP_UP_CODE = "auth.insufficient_user_authentication";

/** True when the server wants a fresh MFA check before it will do this. */
export const needsStepUp = (error: unknown): boolean =>
  error instanceof ApiError &&
  error.status === 401 &&
  error.code === STEP_UP_CODE;

/** Confirms the person again with a code. On success the session carries a fresh token. */
export async function stepUp(code: string): Promise<void> {
  // refresh: false matters. A wrong code also answers 401, and a retry after a refresh would count as a second wrong attempt.
  const result = await apiFetch<{ accessToken: string }>(
    "/api/v1/auth/step-up",
    {
      method: "POST",
      body: { code },
      refresh: false,
    },
  );
  setAccessToken(result.accessToken);
}
