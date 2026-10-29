import { describe, expect, it } from "vitest";
import { parseList } from "./textList";

describe("parseList", () => {
  it("splits on lines and commas and trims", () => {
    expect(parseList(" a , b\nc \n\n")).toEqual(["a", "b", "c"]);
  });

  it("drops repeats but keeps the order", () => {
    expect(parseList("b\na\nb")).toEqual(["b", "a"]);
  });

  it("gives an empty list for blank text", () => {
    expect(parseList("  \n , ")).toEqual([]);
  });
});
