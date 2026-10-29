import { describe, expect, it } from "vitest";
import type { ApiClient } from "./apiClientsApi";
import {
  clientFormErrors,
  emptyClientForm,
  formFromClient,
  rateLimitOf,
  toCreateBody,
  toUpdateBody,
} from "./apiClientForm";

const client = (over: Partial<ApiClient> = {}): ApiClient => ({
  id: "c1",
  clientId: "vg_abc",
  name: "Billing app",
  scopes: ["gateway:invoke", "gateway:detect"],
  status: "Active",
  defaultPolicyId: null,
  ipAllowlist: ["203.0.113.0/24"],
  rateLimitPerMinute: 600,
  ownerUserId: "u1",
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

describe("rateLimitOf", () => {
  it("treats a blank box as the default", () => {
    expect(rateLimitOf("  ")).toBeNull();
  });

  it("accepts whole numbers in range", () => {
    expect(rateLimitOf("1200")).toBe(1200);
  });

  it("rejects everything else", () => {
    for (const text of ["0", "10001", "1.5", "abc", "-3"]) {
      expect(rateLimitOf(text)).toBe("invalid");
    }
  });
});

describe("clientFormErrors", () => {
  it("asks for a name and a permission", () => {
    const form = { ...emptyClientForm(), scopes: [] };
    expect(clientFormErrors(form, "create")).toEqual({
      name: "Name is required.",
      scopes: "Pick at least one permission.",
    });
  });

  it("does not ask for a name when editing", () => {
    expect(clientFormErrors(emptyClientForm(), "edit")).toEqual({});
  });
});

describe("toCreateBody", () => {
  it("leaves out what was not filled in", () => {
    const form = { ...emptyClientForm(), name: "  Billing app " };
    expect(toCreateBody(form)).toEqual({
      name: "Billing app",
      scopes: ["gateway:invoke"],
      defaultPolicyId: undefined,
      ipAllowlist: undefined,
      rateLimitPerMinute: undefined,
    });
  });

  it("sends the networks and the limit when given", () => {
    const form = {
      ...emptyClientForm(),
      name: "x",
      rateLimit: "50",
      ipAllowlist: "10.0.0.0/8\n192.168.1.5",
      defaultPolicyId: "p1",
    };
    expect(toCreateBody(form)).toMatchObject({
      rateLimitPerMinute: 50,
      ipAllowlist: ["10.0.0.0/8", "192.168.1.5"],
      defaultPolicyId: "p1",
    });
  });
});

describe("toUpdateBody", () => {
  it("is empty when nothing changed", () => {
    expect(toUpdateBody(formFromClient(client()), client())).toEqual({});
  });

  it("sends only what changed", () => {
    const form = { ...formFromClient(client()), rateLimit: "1200" };
    expect(toUpdateBody(form, client())).toEqual({ rateLimitPerMinute: 1200 });
  });

  it("ignores the order of scopes", () => {
    const form = {
      ...formFromClient(client()),
      scopes: ["gateway:detect", "gateway:invoke"],
    };
    expect(toUpdateBody(form, client())).toEqual({});
  });

  it("sends an empty list when the allowlist is cleared, meaning from anywhere", () => {
    const form = { ...formFromClient(client()), ipAllowlist: "" };
    expect(toUpdateBody(form, client())).toEqual({ ipAllowlist: [] });
  });

  it("can switch the default policy but not clear it", () => {
    const withPolicy = client({ defaultPolicyId: "p1" });
    const cleared = { ...formFromClient(withPolicy), defaultPolicyId: "" };
    expect(toUpdateBody(cleared, withPolicy)).toEqual({});
    const switched = { ...formFromClient(withPolicy), defaultPolicyId: "p2" };
    expect(toUpdateBody(switched, withPolicy)).toEqual({ defaultPolicyId: "p2" });
  });
});
