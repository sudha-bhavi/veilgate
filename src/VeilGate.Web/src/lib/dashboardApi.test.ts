import { describe, expect, it } from "vitest";
import { callerName, formatLatency, formatLeakRate, readRange } from "./dashboardApi";

describe("readRange", () => {
  it("reads the three known ranges", () => {
    expect(readRange("24h")).toBe("24h");
    expect(readRange("7d")).toBe("7d");
    expect(readRange("30d")).toBe("30d");
  });

  it("falls back to the last 24 hours for anything odd", () => {
    expect(readRange(null)).toBe("24h");
    expect(readRange("90d")).toBe("24h");
    expect(readRange("")).toBe("24h");
  });
});

describe("formatLatency", () => {
  it("says up to, because a latency is the limit of a band", () => {
    expect(formatLatency(250)).toBe("up to 250 ms");
  });

  it("shows a dash when there were no calls", () => {
    expect(formatLatency(null)).toBe("–");
  });
});

describe("formatLeakRate", () => {
  it("shows a percentage with one decimal", () => {
    expect(formatLeakRate(0.02)).toBe("2.0%");
    expect(formatLeakRate(0)).toBe("0.0%");
  });

  it("says so when there has been no evaluation", () => {
    expect(formatLeakRate(null)).toBe("No evaluation yet");
  });
});

describe("callerName", () => {
  it("uses the name while the caller exists", () => {
    expect(callerName({ clientId: "c1", name: "ehr-bridge", requests: 3 })).toBe("ehr-bridge");
  });

  it("names a caller that no longer exists instead of leaving it blank", () => {
    expect(callerName({ clientId: "0192a000-0000", name: null, requests: 3 })).toBe("Caller 0192a000 (no longer exists)");
  });
});
