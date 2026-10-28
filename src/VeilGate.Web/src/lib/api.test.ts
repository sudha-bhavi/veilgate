import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fakeToken } from "../test/fakeToken";
import { ApiError, apiFetch, restoreSession, signOut } from "./api";
import {
  getAccessToken,
  getSession,
  resetSessionForTests,
  setAccessToken,
} from "./session";

const OLD = fakeToken({ sid: "old" });
const NEW = fakeToken({ sid: "new" });

const json = (
  status: number,
  body?: unknown,
  headers?: Record<string, string>,
) =>
  new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers,
  });

/** Replaces fetch with a fake server and records every call as "METHOD path". */
function fakeServer(
  handler: (
    path: string,
    authorization: string | undefined,
    init: RequestInit,
  ) => Response | Promise<Response>,
) {
  const calls: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string, init: RequestInit = {}) => {
      calls.push(`${init.method ?? "GET"} ${path}`);
      const headers = (init.headers ?? {}) as Record<string, string>;
      return handler(path, headers.Authorization, init);
    }),
  );
  return calls;
}

const refreshOk = () =>
  json(200, { accessToken: NEW, tokenType: "Bearer", expiresIn: 600 });

beforeEach(() => resetSessionForTests());
afterEach(() => vi.unstubAllGlobals());

describe("apiFetch", () => {
  it("sends the access token and the JSON body", async () => {
    setAccessToken(OLD);
    let seen: RequestInit = {};
    fakeServer((_path, auth, init) => {
      seen = init;
      expect(auth).toBe(`Bearer ${OLD}`);
      return json(200, { ok: true });
    });

    const result = await apiFetch<{ ok: boolean }>("/api/v1/things", {
      method: "POST",
      body: { a: 1 },
    });

    expect(result).toEqual({ ok: true });
    expect(seen.body).toBe('{"a":1}');
    expect((seen.headers as Record<string, string>)["Content-Type"]).toBe(
      "application/json",
    );
  });

  it("returns nothing for an empty 204 response", async () => {
    setAccessToken(OLD);
    fakeServer(() => json(204));
    await expect(
      apiFetch("/api/v1/things", { method: "DELETE" }),
    ).resolves.toBeUndefined();
  });

  it("refreshes once on a 401 and repeats the call with the new token", async () => {
    setAccessToken(OLD);
    const calls = fakeServer((path, auth) => {
      if (path === "/api/v1/auth/refresh") return refreshOk();
      return auth === `Bearer ${NEW}`
        ? json(200, { who: "me" })
        : json(401, { code: "auth.expired", title: "expired" });
    });

    const result = await apiFetch<{ who: string }>("/api/v1/me");

    expect(result).toEqual({ who: "me" });
    expect(getAccessToken()).toBe(NEW);
    expect(calls).toEqual([
      "GET /api/v1/me",
      "POST /api/v1/auth/refresh",
      "GET /api/v1/me",
    ]);
  });

  it("shares one refresh between calls that fail at the same time", async () => {
    setAccessToken(OLD);
    const calls = fakeServer(async (path, auth) => {
      if (path === "/api/v1/auth/refresh") {
        await new Promise((r) => setTimeout(r, 10));
        return refreshOk();
      }
      return auth === `Bearer ${NEW}`
        ? json(200, { ok: true })
        : json(401, { code: "auth.expired" });
    });

    const results = await Promise.all([
      apiFetch("/api/v1/a"),
      apiFetch("/api/v1/b"),
      apiFetch("/api/v1/c"),
    ]);

    expect(results).toHaveLength(3);
    expect(calls.filter((c) => c.endsWith("/auth/refresh"))).toHaveLength(1);
  });

  it("signs out and throws the 401 when the refresh is refused, without trying again", async () => {
    setAccessToken(OLD);
    const calls = fakeServer((path) =>
      path === "/api/v1/auth/refresh"
        ? json(401, { code: "auth.refresh_invalid" })
        : json(401, { code: "auth.expired", title: "expired" }),
    );

    await expect(apiFetch("/api/v1/me")).rejects.toMatchObject({
      status: 401,
      code: "auth.expired",
    });

    expect(getSession().status).toBe("signedOut");
    expect(calls).toEqual(["GET /api/v1/me", "POST /api/v1/auth/refresh"]);
  });

  it("does not try to refresh when there is no session", async () => {
    const calls = fakeServer(() => json(401, { code: "auth.unauthorized" }));
    await expect(apiFetch("/api/v1/me")).rejects.toBeInstanceOf(ApiError);
    expect(calls).toEqual(["GET /api/v1/me"]);
  });

  it("uses a given bearer token and never refreshes for it", async () => {
    setAccessToken(OLD);
    const calls = fakeServer((_path, auth) => {
      expect(auth).toBe("Bearer mfa-token");
      return json(401, { code: "auth.code_invalid", title: "wrong code" });
    });

    await expect(
      apiFetch("/api/v1/auth/mfa/verify", {
        method: "POST",
        body: { code: "000000" },
        bearer: "mfa-token",
      }),
    ).rejects.toMatchObject({
      status: 401,
      code: "auth.code_invalid",
    });
    expect(calls).toHaveLength(1);
    expect(getAccessToken()).toBe(OLD); // the session was not touched
  });

  it("turns validation and rate-limit answers into an ApiError with the details", async () => {
    fakeServer((path) =>
      path === "/a"
        ? json(422, {
            code: "validation.failed",
            title: "Invalid",
            errors: { name: ["Name is required"] },
          })
        : json(
            429,
            { code: "rate.limited", title: "Slow down" },
            { "Retry-After": "30" },
          ),
    );

    await expect(apiFetch("/a")).rejects.toMatchObject({
      status: 422,
      code: "validation.failed",
      errors: { name: ["Name is required"] },
    });
    await expect(apiFetch("/b")).rejects.toMatchObject({
      status: 429,
      retryAfter: 30,
    });
  });

  it("reports an unreachable server as status 0 and leaves the session alone", async () => {
    setAccessToken(OLD);
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new TypeError("Failed to fetch")),
    );

    await expect(apiFetch("/api/v1/me")).rejects.toMatchObject({
      status: 0,
      code: "network.unreachable",
    });
    expect(getAccessToken()).toBe(OLD);
  });
});

describe("restoreSession", () => {
  it("signs in again from the refresh cookie", async () => {
    fakeServer(() => refreshOk());
    await restoreSession();
    expect(getAccessToken()).toBe(NEW);
  });

  it("ends up signed out when there is no valid cookie", async () => {
    fakeServer(() => json(401, { code: "auth.refresh_invalid" }));
    await restoreSession();
    expect(getSession().status).toBe("signedOut");
  });

  it("ends up signed out when the server cannot be reached", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new TypeError("Failed to fetch")),
    );
    await restoreSession();
    expect(getSession().status).toBe("signedOut");
  });

  it("makes one refresh call even if asked twice at once (React StrictMode runs effects twice)", async () => {
    const calls = fakeServer(async () => {
      await new Promise((r) => setTimeout(r, 10));
      return refreshOk();
    });
    await Promise.all([restoreSession(), restoreSession()]);
    expect(calls).toHaveLength(1);
  });
});

describe("signOut", () => {
  it("asks the server to end the session and clears the token", async () => {
    setAccessToken(OLD);
    const calls = fakeServer(() => json(204));
    await signOut();
    expect(calls).toEqual(["POST /api/v1/auth/logout"]);
    expect(getSession().status).toBe("signedOut");
  });

  it("clears the token even if the server cannot be reached", async () => {
    setAccessToken(OLD);
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new TypeError("Failed to fetch")),
    );
    await signOut();
    expect(getSession().status).toBe("signedOut");
  });
});
