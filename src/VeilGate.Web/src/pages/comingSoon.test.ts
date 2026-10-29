import { describe, expect, it } from "vitest";
import { NAV_ITEMS, rolesForPath } from "../auth/nav";
import { COMING_SOON } from "./comingSoon";

describe("COMING_SOON", () => {
  it("only lists pages that have a sidebar link, so who may open them is decided in one place", () => {
    const navPaths = NAV_ITEMS.map((item) => item.path);
    for (const page of COMING_SOON) {
      expect(navPaths).toContain(page.path);
    }
  });

  it("lists each page once", () => {
    const paths = COMING_SOON.map((page) => page.path);
    expect(new Set(paths).size).toBe(paths.length);
  });

  it("has something to say on every page", () => {
    for (const page of COMING_SOON) {
      expect(page.summary).not.toBe("");
      expect(page.planned.length).toBeGreaterThan(0);
    }
  });
});

describe("rolesForPath", () => {
  it("gives the roles of a sidebar link", () => {
    expect(rolesForPath("/users")).toEqual(["TenantAdmin"]);
    expect(rolesForPath("/audit")).toContain("Auditor");
  });

  it("gives no roles for an unknown path, so nobody gets in by accident", () => {
    expect(rolesForPath("/nowhere")).toEqual([]);
  });
});
