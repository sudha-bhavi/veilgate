import { describe, expect, it } from "vitest";
import { navFor } from "./nav";

const labels = (role: string) => navFor(role).map((item) => item.label);

describe("navFor", () => {
  it("gives every tenant role the dashboard", () => {
    for (const role of [
      "TenantAdmin",
      "SecurityOfficer",
      "Developer",
      "Auditor",
    ]) {
      expect(labels(role)).toContain("Dashboard");
    }
  });

  it("shows Users only to the tenant admin", () => {
    expect(labels("TenantAdmin")).toContain("Users");
    expect(labels("Developer")).not.toContain("Users");
    expect(labels("Auditor")).not.toContain("Users");
  });

  it("shows Providers only to the tenant admin", () => {
    expect(labels("TenantAdmin")).toContain("Providers");
    expect(labels("Developer")).not.toContain("Providers");
    expect(labels("SecurityOfficer")).not.toContain("Providers");
  });

  it("lets developers see API clients and evaluations, but not auditors", () => {
    expect(labels("Developer")).toContain("API clients");
    expect(labels("Developer")).toContain("Evaluations");
    expect(labels("Auditor")).not.toContain("API clients");
    expect(labels("Auditor")).not.toContain("Evaluations");
  });

  it("shows nothing for a role it does not know", () => {
    expect(labels("Stranger")).toEqual([]);
  });
});
