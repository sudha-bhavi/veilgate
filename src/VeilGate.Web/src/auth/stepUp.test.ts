import { describe, expect, it } from "vitest";
import { ApiError } from "../lib/api";
import { needsStepUp } from "./stepUp";

describe("needsStepUp", () => {
  it("recognises the server asking for a fresh MFA check", () => {
    expect(
      needsStepUp(
        new ApiError(401, "auth.insufficient_user_authentication", "Confirm"),
      ),
    ).toBe(true);
  });

  it("ignores other failures", () => {
    expect(
      needsStepUp(new ApiError(401, "auth.token_invalid", "Sign in again.")),
    ).toBe(false);
    expect(
      needsStepUp(
        new ApiError(403, "auth.insufficient_user_authentication", "x"),
      ),
    ).toBe(false);
    expect(needsStepUp(new Error("nope"))).toBe(false);
  });
});
