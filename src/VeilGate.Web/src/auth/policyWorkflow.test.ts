import { describe, expect, it } from "vitest";
import type { PolicyDetail, PolicyStatus } from "../lib/policiesApi";
import { workflowActions } from "./policyWorkflow";

const policy = (
  status: PolicyStatus,
  draftState: string | null,
): PolicyDetail => ({
  id: "p1",
  name: "Strict",
  description: null,
  status,
  current: null,
  draft: draftState
    ? {
        id: "v1",
        versionNo: 1,
        state: draftState,
        yaml: "",
        summary: null,
        publishedAt: null,
      }
    : null,
  etag: '"a"',
  createdAt: "2026-01-01T00:00:00Z",
});

describe("workflowActions", () => {
  it("lets an author submit a draft, but not publish it", () => {
    const actions = workflowActions(policy("Draft", "Draft"), "TenantAdmin");
    expect(actions).toMatchObject({
      submit: true,
      publish: false,
      reject: false,
      archive: true,
    });
  });

  it("gives readers no buttons", () => {
    const actions = workflowActions(policy("Draft", "Draft"), "Developer");
    expect(Object.values(actions).some(Boolean)).toBe(false);
  });

  it("lets a security officer decide on a submitted draft", () => {
    const actions = workflowActions(
      policy("PendingApproval", "PendingApproval"),
      "SecurityOfficer",
    );
    expect(actions).toMatchObject({
      publish: true,
      reject: true,
      submit: false,
      waitingForReview: false,
    });
  });

  it("tells everyone else that a submitted draft is waiting", () => {
    const actions = workflowActions(
      policy("PendingApproval", "PendingApproval"),
      "TenantAdmin",
    );
    expect(actions).toMatchObject({
      publish: false,
      reject: false,
      waitingForReview: true,
    });
  });

  it("offers nothing on an archived policy", () => {
    const actions = workflowActions(policy("Archived", null), "SecurityOfficer");
    expect(Object.values(actions).some(Boolean)).toBe(false);
  });
});
