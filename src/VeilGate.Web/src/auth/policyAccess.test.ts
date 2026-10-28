import { describe, expect, it } from "vitest";
import { canAuthorPolicies, canReviewPolicies } from "./policyAccess";

describe("policy access", () => {
  it("lets admins and security officers write", () => {
    expect(canAuthorPolicies("TenantAdmin")).toBe(true);
    expect(canAuthorPolicies("SecurityOfficer")).toBe(true);
    expect(canAuthorPolicies("Developer")).toBe(false);
    expect(canAuthorPolicies("Auditor")).toBe(false);
  });

  it("lets only the security officer approve", () => {
    expect(canReviewPolicies("SecurityOfficer")).toBe(true);
    expect(canReviewPolicies("TenantAdmin")).toBe(false);
  });
});
