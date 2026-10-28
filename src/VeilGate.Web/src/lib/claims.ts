export type Claims = {
  sub: string;
  tid: string;
  role: string;
  sid: string;
  amr: string[];
  authTime: number; // seconds since 1970 of the last sign-in or step-up
  exp: number;
};

function readPayload(token: string): Record<string, unknown> | null {
  const payload = token.split(".")[1];
  if (!payload) return null;
  try {
    const base64 = payload
      .replace(/-/g, "+")
      .replace(/_/g, "/")
      .padEnd(Math.ceil(payload.length / 4) * 4, "=");
    const parsed: unknown = JSON.parse(atob(base64));
    return typeof parsed === "object" && parsed !== null
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

/**
 * Reads the claims out of an access token so the console knows the role and can hide what the person may not use.
 * The signature is NOT checked here: this is for display and navigation only, and the server checks every request.
 */
export function decodeClaims(token: string): Claims | null {
  const raw = readPayload(token);
  if (!raw) return null;
  if (
    typeof raw.sub !== "string" ||
    typeof raw.tid !== "string" ||
    typeof raw.role !== "string" ||
    typeof raw.exp !== "number"
  ) {
    return null;
  }
  return {
    sub: raw.sub,
    tid: raw.tid,
    role: raw.role,
    sid: String(raw.sid ?? ""),
    amr: Array.isArray(raw.amr) ? raw.amr.map(String) : [],
    authTime: Number(raw.auth_time ?? 0),
    exp: raw.exp,
  };
}

/** When any of our tokens stops being valid (seconds since 1970), or null if it cannot be read. */
export function expiryOf(token: string): number | null {
  const raw = readPayload(token);
  return raw && typeof raw.exp === "number" ? raw.exp : null;
}
