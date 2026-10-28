import { describe, expect, it } from "vitest";
import { toSegments } from "./highlight";

describe("toSegments", () => {
  it("returns one plain piece when nothing was found", () => {
    expect(toSegments("hello", [])).toEqual([{ text: "hello" }]);
  });

  it("marks a span in the middle", () => {
    expect(
      toSegments("Hi Jane Roe!", [{ start: 3, end: 11, type: "PERSON" }]),
    ).toEqual([
      { text: "Hi " },
      { text: "Jane Roe", type: "PERSON" },
      { text: "!" },
    ]);
  });

  it("copes with spans given out of order", () => {
    const result = toSegments("ab cd", [
      { start: 3, end: 5, type: "B" },
      { start: 0, end: 2, type: "A" },
    ]);
    expect(result.map((s) => s.text)).toEqual(["ab", " ", "cd"]);
  });

  it("lets the earlier span win when two overlap", () => {
    const result = toSegments("abcdef", [
      { start: 0, end: 4, type: "A" },
      { start: 2, end: 6, type: "B" },
    ]);
    expect(result).toEqual([
      { text: "abcd", type: "A" },
      { text: "ef", type: "B" },
    ]);
  });

  it("ignores spans that fall outside the text", () => {
    expect(toSegments("abc", [{ start: 10, end: 12, type: "X" }])).toEqual([
      { text: "abc" },
    ]);
  });
});
