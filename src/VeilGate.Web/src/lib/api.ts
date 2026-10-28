import { clearSession, getAccessToken, setAccessToken } from "./session";

/** What the server said went wrong: the HTTP status and the stable "code" from the problem+json body. */
export class ApiError extends Error {
  status: number;
  code: string;
  errors?: Record<string, string[]>; // field -> messages, on 422 validation.failed
  retryAfter?: number; // seconds, on 429

  constructor(
    status: number,
    code: string,
    message: string,
    errors?: Record<string, string[]>,
    retryAfter?: number,
  ) {
    super(message);
    this.status = status;
    this.code = code;
    this.errors = errors;
    this.retryAfter = retryAfter;
  }
}

type Options = {
  method?: string;
  body?: unknown;
  headers?: Record<string, string>;
  /** A different bearer token: the short-lived one from the password step, used by the MFA calls. */
  bearer?: string;
  /** Set false for calls that must never trigger a refresh (the refresh itself, logout). */
  refresh?: boolean;
};

let refreshing: Promise<boolean> | null = null;

/**
 * Trades the refresh cookie for a new access token. Only one refresh runs at a time and everyone shares its result:
 * refresh tokens rotate, so a second call with the already-used cookie would look like token theft and end the session.
 */
export function refreshSession(): Promise<boolean> {
  refreshing ??= doRefresh().finally(() => {
    refreshing = null;
  });
  return refreshing;
}

async function doRefresh(): Promise<boolean> {
  // A network failure throws and leaves the session alone: being offline is not the same as being signed out.
  const response = await fetch("/api/v1/auth/refresh", { method: "POST" });
  if (!response.ok) {
    clearSession();
    return false;
  }
  const body = (await response.json()) as { accessToken?: string };
  if (!body.accessToken) {
    clearSession();
    return false;
  }
  setAccessToken(body.accessToken);
  return getAccessToken() !== null;
}

async function send(path: string, o: Options): Promise<Response> {
  const headers: Record<string, string> = {
    Accept: "application/json",
    ...o.headers,
  };
  const token = o.bearer ?? getAccessToken();
  if (token) headers.Authorization = `Bearer ${token}`;

  let body: string | undefined;
  if (o.body !== undefined) {
    headers["Content-Type"] = "application/json";
    body = JSON.stringify(o.body);
  }

  try {
    return await fetch(path, { method: o.method ?? "GET", headers, body });
  } catch {
    throw new ApiError(0, "network.unreachable", "Could not reach the server.");
  }
}

async function toError(response: Response): Promise<ApiError> {
  let problem: {
    title?: string;
    code?: string;
    errors?: Record<string, string[]>;
  } = {};
  try {
    problem = await response.json();
  } catch {
    // not JSON: the generic message below is used
  }
  const retry = Number(response.headers.get("Retry-After"));
  return new ApiError(
    response.status,
    problem.code ?? "error.unknown",
    problem.title ?? `Request failed (${response.status})`,
    problem.errors,
    retry > 0 ? retry : undefined,
  );
}

/**
 * One call to the API. If the access token has expired (401), it is refreshed once and the call is repeated once.
 * If the refresh fails the person is signed out and the original 401 is thrown.
 */
export async function apiFetch<T = void>(
  path: string,
  options: Options = {},
): Promise<T> {
  let response = await send(path, options);

  const canRefresh =
    options.refresh !== false &&
    options.bearer === undefined &&
    getAccessToken() !== null;
  if (response.status === 401 && canRefresh) {
    let renewed = false;
    try {
      renewed = await refreshSession();
    } catch {
      throw new ApiError(
        0,
        "network.unreachable",
        "Could not reach the server.",
      );
    }
    if (renewed) response = await send(path, options);
  }

  if (!response.ok) throw await toError(response);
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** On page load the access token is gone (memory only), but the refresh cookie may still be good: try it once. */
export async function restoreSession(): Promise<void> {
  try {
    await refreshSession();
  } catch {
    clearSession(); // server unreachable: show the sign-in page
  }
}

export async function signOut(): Promise<void> {
  try {
    await apiFetch("/api/v1/auth/logout", { method: "POST", refresh: false });
  } catch {
    // the in-memory token is dropped below either way, and the server expires the cookie on its own
  }
  clearSession();
}
