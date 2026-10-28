import { describe, expect, it } from "vitest";
import { pageCount, readPage } from "./paging";

describe("pageCount", () => {
  it("rounds up and never goes below one", () => {
    expect(pageCount(0, 25)).toBe(1);
    expect(pageCount(25, 25)).toBe(1);
    expect(pageCount(26, 25)).toBe(2);
  });
});

describe("readPage", () => {
  it("accepts whole numbers from 1 up", () => {
    expect(readPage("3")).toBe(3);
  });

  it("falls back to 1 for anything else", () => {
    for (const value of [null, "", "0", "-2", "1.5", "abc"]) {
      expect(readPage(value)).toBe(1);
    }
  });
});
