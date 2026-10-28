import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { describeApiError } from "./apiErrorText";

describe("describeApiError", () => {
  it("joins field messages from a validation failure", () => {
    const error = new ApiError(422, "validation.failed", "Validation failed", {
      Text: ["Text is required"],
      Model: ["Model is required for chat"],
    });
    expect(describeApiError(error)).toBe(
      "Text is required Model is required for chat",
    );
  });

  it("says how long to wait when rate limited", () => {
    expect(
      describeApiError(new ApiError(429, "rate", "Slow down", undefined, 12)),
    ).toBe("Too many requests. Try again in 12 seconds.");
  });

  it("explains a forbidden role", () => {
    expect(describeApiError(new ApiError(403, "forbidden", "Forbidden"))).toBe(
      "Your role is not allowed to do this.",
    );
  });

  it("falls back to the server's message, and to a generic one for non-API errors", () => {
    expect(
      describeApiError(
        new ApiError(0, "network.unreachable", "Could not reach the server."),
      ),
    ).toBe("Could not reach the server.");
    expect(describeApiError(new Error("x"))).toBe(
      "Something went wrong. Try again.",
    );
  });
});
