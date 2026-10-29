import { describe, expect, it } from "vitest";
import { applyInput, digitsOnly, removeAt, typedIntoBox } from "./codeInput";
import { formatCountdown } from "./format";

const empty = ["", "", "", "", "", ""];

describe("applyInput", () => {
  it("puts a typed digit in the box and moves on to the next", () => {
    expect(applyInput(empty, 2, "7")).toEqual({
      boxes: ["", "", "7", "", "", ""],
      focus: 3,
    });
  });

  it("stays on the last box after the sixth digit", () => {
    expect(applyInput(["1", "2", "3", "4", "5", ""], 5, "6")).toEqual({
      boxes: ["1", "2", "3", "4", "5", "6"],
      focus: 5,
    });
  });

  it("ignores anything that is not a digit", () => {
    expect(applyInput(empty, 0, "a")).toEqual({ boxes: empty, focus: 0 });
  });

  it("fills every box from a pasted code, whatever box it lands in, dropping spaces and dashes", () => {
    expect(applyInput(empty, 3, "123 456").boxes).toEqual([
      "1",
      "2",
      "3",
      "4",
      "5",
      "6",
    ]);
    expect(applyInput(empty, 0, "12-34-56").boxes).toEqual([
      "1",
      "2",
      "3",
      "4",
      "5",
      "6",
    ]);
  });

  it("ignores digits past the sixth", () => {
    expect(applyInput(empty, 0, "12345678").boxes).toEqual([
      "1",
      "2",
      "3",
      "4",
      "5",
      "6",
    ]);
  });

  it("does not change the boxes it was given", () => {
    const boxes = [...empty];
    applyInput(boxes, 0, "1");
    expect(boxes).toEqual(empty);
  });
});

describe("removeAt", () => {
  it("clears a filled box and stays", () => {
    expect(removeAt(["1", "2", "", "", "", ""], 1)).toEqual({
      boxes: ["1", "", "", "", "", ""],
      focus: 1,
    });
  });

  it("clears the previous box when this one is already empty", () => {
    expect(removeAt(["1", "2", "", "", "", ""], 2)).toEqual({
      boxes: ["1", "", "", "", "", ""],
      focus: 1,
    });
  });

  it("stops at the first box", () => {
    expect(removeAt(empty, 0)).toEqual({ boxes: empty, focus: 0 });
  });
});

describe("typedIntoBox", () => {
  it("keeps only the newest digit of a normal key press", () => {
    expect(typedIntoBox("47")).toBe("7");
    expect(typedIntoBox("a")).toBe("");
  });

  it("keeps the whole code when autofill puts it into one box", () => {
    expect(typedIntoBox("099066")).toBe("099066");
    expect(applyInput(empty, 0, typedIntoBox("099066")).boxes).toEqual([
      "0",
      "9",
      "9",
      "0",
      "6",
      "6",
    ]);
  });
});

describe("helpers", () => {
  it("digitsOnly keeps only digits", () => {
    expect(digitsOnly("a1 b2-3")).toBe("123");
  });

  it("formatCountdown pads minutes and seconds", () => {
    expect(formatCountdown(272)).toBe("04:32");
    expect(formatCountdown(0)).toBe("00:00");
    expect(formatCountdown(65)).toBe("01:05");
  });
});
