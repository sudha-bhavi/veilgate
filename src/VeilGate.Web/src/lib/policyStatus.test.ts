import { describe, expect, it } from "vitest";
import { statusLabel } from "./policyStatus";

describe("statusLabel", () => {
  it("writes statuses as people say them", () => {
    expect(statusLabel("PendingApproval")).toBe("Pending approval");
    expect(statusLabel("Published")).toBe("Published");
  });

  it("shows an unknown status as it is", () => {
    expect(statusLabel("Mystery")).toBe("Mystery");
  });
});
