import { describe, expect, it } from "vitest";
import { dayRange } from "./auditApi";

describe("dayRange", () => {
  it("covers whole days in the person's own time zone, sent as UTC", () => {
    expect(dayRange("2026-10-01", "2026-10-29")).toEqual({
      from: new Date("2026-10-01T00:00:00").toISOString(),
      to: new Date("2026-10-29T23:59:59.999").toISOString(),
    });
  });

  it("leaves out a day that is missing or not a date", () => {
    expect(dayRange(undefined, "")).toEqual({ from: undefined, to: undefined });
    expect(dayRange("yesterday", "2026-13-45")).toEqual({ from: undefined, to: undefined });
  });
});
