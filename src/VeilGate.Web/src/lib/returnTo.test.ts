import { describe, expect, it } from "vitest";
import { safeReturnTo } from "./returnTo";

describe("safeReturnTo", () => {
  it.each(["/dashboard", "/policies?tab=versions", "/evaluations/abc#top"])(
    "keeps the in-app path %s",
    (path) => {
      expect(safeReturnTo(path)).toBe(path);
    },
  );

  it.each([
    null,
    undefined,
    "",
    "https://evil.example",
    "//evil.example",
    "/\\evil.example",
    "javascript:alert(1)",
    "/ok\nSet-Cookie: x=1",
    "dashboard",
  ])("falls back for %s", (value) => {
    expect(safeReturnTo(value)).toBe("/dashboard");
  });

  it("uses the fallback it is given", () => {
    expect(safeReturnTo("//evil.example", "/home")).toBe("/home");
  });
});
