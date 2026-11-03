import { describe, expect, it } from "vitest";
import { actionsFor } from "./userRules";
import type { TenantUser } from "./usersApi";

const person = (over: Partial<TenantUser> = {}): TenantUser => ({
  id: "u2",
  name: "Ann Lee",
  email: "ann@acme.test",
  role: "Developer",
  status: "Active",
  hasMfa: true,
  lastLoginAt: null,
  ...over,
});

describe("actionsFor", () => {
  it("offers nothing on your own row", () => {
    expect(actionsFor(person({ id: "ME" }), "me")).toEqual([]);
  });

  it("lets you change the role, reset two-step sign-in or disable an active person", () => {
    expect(actionsFor(person(), "u1")).toEqual(["role", "reset", "disable"]);
  });

  it("lets you resend, change the role or disable someone who has not accepted yet", () => {
    expect(actionsFor(person({ status: "Invited" }), "u1")).toEqual(["resend", "role", "disable"]);
  });

  it("lets you only enable a disabled person", () => {
    expect(actionsFor(person({ status: "Disabled" }), "u1")).toEqual(["enable"]);
  });

  it("does not offer a reset to someone with nothing to reset yet", () => {
    expect(actionsFor(person({ status: "Invited" }), "u1")).not.toContain("reset");
  });
});
