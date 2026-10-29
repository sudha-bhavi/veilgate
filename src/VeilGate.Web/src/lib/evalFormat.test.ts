import { describe, expect, it } from "vitest";
import {
  isActive,
  modeLabel,
  percent,
  progressPercent,
  signedPoints,
  verdict,
} from "./evalFormat";

describe("percent", () => {
  it("writes a ratio as a percentage", () => {
    expect(percent(0.9871)).toBe("98.7%");
    expect(percent(1)).toBe("100.0%");
    expect(percent(0.5, 0)).toBe("50%");
  });
});

describe("signedPoints", () => {
  it("shows the direction of a change", () => {
    expect(signedPoints(0.012)).toBe("+1.2 pts");
    expect(signedPoints(-0.05)).toBe("-5.0 pts");
    expect(signedPoints(0)).toBe("0.0 pts");
  });
});

describe("isActive", () => {
  it("is true only for runs that have not finished", () => {
    expect(isActive("Queued")).toBe(true);
    expect(isActive("Running")).toBe(true);
    expect(isActive("Succeeded")).toBe(false);
    expect(isActive("Failed")).toBe(false);
  });
});

describe("progressPercent", () => {
  it("rounds, and copes with an empty run", () => {
    expect(progressPercent(1, 3)).toBe(33);
    expect(progressPercent(3, 3)).toBe(100);
    expect(progressPercent(0, 0)).toBe(0);
    expect(progressPercent(9, 3)).toBe(100);
  });
});

describe("verdict", () => {
  it("calls a higher recall better and a lower one worse", () => {
    expect(verdict(0.02, "higher")).toBe("better");
    expect(verdict(-0.02, "higher")).toBe("worse");
  });

  it("calls fewer leaks better", () => {
    expect(verdict(-3, "lower")).toBe("better");
    expect(verdict(3, "lower")).toBe("worse");
  });

  it("calls no change the same", () => {
    expect(verdict(0, "higher")).toBe("same");
    expect(verdict(1e-12, "lower")).toBe("same");
  });
});

describe("modeLabel", () => {
  it("names the two modes", () => {
    expect(modeLabel("rules")).toBe("Rules only");
    expect(modeLabel("rules+ner")).toBe("Rules and NER model");
  });
});
