import { describe, expect, it } from "vitest";
import { describeIssue } from "./policyIssues";

describe("describeIssue", () => {
  it("names the line and column", () => {
    expect(
      describeIssue({ line: 3, column: 5, message: "Unknown action" }),
    ).toBe("Line 3, column 5: Unknown action");
  });

  it("leaves out the position when there is none", () => {
    expect(describeIssue({ line: 0, column: 0, message: "Empty policy" })).toBe(
      "Empty policy",
    );
  });
});
