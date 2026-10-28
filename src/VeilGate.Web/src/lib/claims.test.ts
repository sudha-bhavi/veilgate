import { describe, expect, it } from "vitest";
import { fakeToken } from "../test/fakeToken";
import { decodeClaims, expiryOf } from "./claims";

describe("decodeClaims", () => {
  it("reads the claims the console needs", () => {
    const claims = decodeClaims(fakeToken({ role: "Developer", sid: "s1" }));
    expect(claims).toEqual({
      sub: "11111111-1111-1111-1111-111111111111",
      tid: "22222222-2222-2222-2222-222222222222",
      role: "Developer",
      sid: "s1",
      amr: ["pwd", "otp"],
      authTime: 1_800_000_000,
      exp: 1_800_000_600,
    });
  });

  it.each([
    ["not a token", "garbage"],
    ["no payload part", "a"],
    ["payload that is not JSON", "a.bm90IGpzb24.c"],
  ])("returns null for %s", (_name, token) => {
    expect(decodeClaims(token)).toBeNull();
  });

  it("returns null when a required claim is missing", () => {
    expect(decodeClaims(fakeToken({ role: undefined }))).toBeNull();
    expect(decodeClaims(fakeToken({ exp: "soon" }))).toBeNull();
  });
  describe("expiryOf", () => {
    it("reads the expiry even from a token without a role", () => {
      expect(expiryOf(fakeToken({ role: undefined, exp: 1_800_000_999 }))).toBe(
        1_800_000_999,
      );
    });

    it("returns null when there is none", () => {
      expect(expiryOf("garbage")).toBeNull();
      expect(expiryOf(fakeToken({ exp: undefined }))).toBeNull();
    });
  });
});
