import { describe, expect, it } from "vitest";
import { fakeToken } from "../test/fakeToken";
import { decodeClaims } from "../lib/claims";
import type { SessionState } from "../lib/session";
import { decideAccess } from "./access";

function signedIn(role: string): SessionState {
  const token = fakeToken({ role });
  return { status: "signedIn", token, claims: decodeClaims(token)! };
}

describe("decideAccess", () => {
  it("waits while the page is still asking the server", () => {
    expect(decideAccess({ status: "loading" }, ["TenantAdmin"])).toBe("wait");
  });

  it("sends someone who is signed out to sign in", () => {
    expect(decideAccess({ status: "signedOut" })).toBe("login");
  });

  it("lets any signed-in person through when no roles are required", () => {
    expect(decideAccess(signedIn("Auditor"))).toBe("allow");
  });

  it("allows a listed role and refuses an unlisted one", () => {
    expect(
      decideAccess(signedIn("SecurityOfficer"), [
        "TenantAdmin",
        "SecurityOfficer",
      ]),
    ).toBe("allow");
    expect(
      decideAccess(signedIn("Developer"), ["TenantAdmin", "SecurityOfficer"]),
    ).toBe("forbidden");
  });
});
