export type Claims = {
  sub: string;
  tid: string;
  role: string;
  sid: string;
  amr: string[];
  authTime: number; // seconds since 1970 of the last sign-in or step-up
  exp: number;
};

/**
 * Reads the claims out of an access token so the console knows the role and can hide what the person may not use.
 * The signature is NOT checked here: this is for display and navigation only, and the server checks every request.
 */
export function decodeClaims(token: string): Claims | null {
  const payload = token.split(".")[1];
  if (!payload) return null;
  try {
    const base64 = payload
      .replace(/-/g, "+")
      .replace(/_/g, "/")
      .padEnd(Math.ceil(payload.length / 4) * 4, "=");
    const raw = JSON.parse(atob(base64)) as Record<string, unknown>;
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
  } catch {
    return null;
  }
}
