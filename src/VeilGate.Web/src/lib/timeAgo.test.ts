import { describe, expect, it } from "vitest";
import { timeAgo } from "./timeAgo";

const NOW = Date.parse("2026-11-04T12:00:00Z");
const ago = (ms: number) => new Date(NOW - ms).toISOString();
const MIN = 60_000;
const HOUR = 60 * MIN;

describe("timeAgo", () => {
  it("says now for the last minute, and for a clock that is slightly ahead", () => {
    expect(timeAgo(ago(20_000), NOW)).toBe("now");
    expect(timeAgo(ago(-5_000), NOW)).toBe("now");
  });

  it("counts minutes, then hours", () => {
    expect(timeAgo(ago(5 * MIN), NOW)).toBe("5 min ago");
    expect(timeAgo(ago(3 * HOUR + 10 * MIN), NOW)).toBe("3 h ago");
  });

  it("says yesterday between one and two days", () => {
    expect(timeAgo(ago(30 * HOUR), NOW)).toBe("yesterday");
  });

  it("gives the date for anything older, and nothing for a value it cannot read", () => {
    expect(timeAgo(ago(5 * 24 * HOUR), NOW)).toBe(new Date(NOW - 5 * 24 * HOUR).toLocaleDateString());
    expect(timeAgo("not a date", NOW)).toBe("");
  });
});
