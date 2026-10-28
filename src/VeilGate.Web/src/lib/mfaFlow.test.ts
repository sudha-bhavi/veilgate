import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fakeToken } from "../test/fakeToken";
import { clearMfaFlow, getMfaFlow, secondsLeft, startMfaFlow } from "./mfaFlow";

const EXP = 1_800_000_600;

beforeEach(() => {
  clearMfaFlow();
  vi.useFakeTimers();
  vi.setSystemTime((EXP - 300) * 1000); // five minutes before the token expires
});
afterEach(() => vi.useRealTimers());

describe("mfaFlow", () => {
  it("holds the token and what it is for", () => {
    startMfaFlow(fakeToken(), "verify");
    expect(getMfaFlow()).toMatchObject({ purpose: "verify" });
  });

  it("counts down to the token expiry", () => {
    startMfaFlow(fakeToken({ exp: EXP }), "verify");
    const flow = getMfaFlow()!;
    expect(secondsLeft(flow)).toBe(300);
    vi.advanceTimersByTime(60_000);
    expect(secondsLeft(flow)).toBe(240);
    vi.advanceTimersByTime(10 * 60_000);
    expect(secondsLeft(flow)).toBe(0);
  });

  it("falls back to five minutes for a token it cannot read", () => {
    startMfaFlow("opaque", "enroll");
    expect(secondsLeft(getMfaFlow()!)).toBe(300);
  });

  it("forgets everything when cleared", () => {
    startMfaFlow(fakeToken(), "verify");
    clearMfaFlow();
    expect(getMfaFlow()).toBeNull();
  });
});
