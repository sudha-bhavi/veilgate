function b64url(value: unknown): string {
  return btoa(JSON.stringify(value))
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}

/** An unsigned token with the claims the console reads. `sid` tells two tokens apart. */
export function fakeToken(overrides: Record<string, unknown> = {}): string {
  const payload = {
    sub: "11111111-1111-1111-1111-111111111111",
    tid: "22222222-2222-2222-2222-222222222222",
    role: "TenantAdmin",
    sid: "session-a",
    amr: ["pwd", "otp"],
    auth_time: 1_800_000_000,
    exp: 1_800_000_600,
    ...overrides,
  };
  return `${b64url({ alg: "none" })}.${b64url(payload)}.signature`;
}
