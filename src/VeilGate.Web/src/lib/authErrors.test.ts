import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { loginErrorMessage } from "./authErrors";

describe("loginErrorMessage", () => {
  it("never says which of email or password was wrong", () => {
    expect(
      loginErrorMessage(new ApiError(401, "auth.invalid_credentials", "x")),
    ).toBe("Email or password is incorrect");
  });

  it("explains a lock and a rate limit", () => {
    expect(
      loginErrorMessage(new ApiError(423, "auth.account_locked", "x")),
    ).toBe("Account temporarily locked. Try again in 15 minutes");
    expect(
      loginErrorMessage(
        new ApiError(429, "rate_limit.exceeded", "x", undefined, 30),
      ),
    ).toBe("Too many attempts. Wait 30 seconds");
    expect(
      loginErrorMessage(new ApiError(429, "rate_limit.exceeded", "x")),
    ).toContain("Too many attempts");
  });

  it("tells the person when the server cannot be reached", () => {
    expect(
      loginErrorMessage(new ApiError(0, "network.unreachable", "x")),
    ).toContain("Could not reach the server");
  });

  it("has a generic message for anything else", () => {
    expect(loginErrorMessage(new ApiError(500, "error.unknown", "x"))).toBe(
      "Something went wrong. Try again.",
    );
    expect(loginErrorMessage(new Error("boom"))).toBe(
      "Something went wrong. Try again.",
    );
  });
});
