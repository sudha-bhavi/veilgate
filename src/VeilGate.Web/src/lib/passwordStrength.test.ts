import { describe, expect, it } from "vitest";
import { passwordStrength } from "./passwordStrength";

describe("passwordStrength", () => {
  it("shows nothing for an empty box", () => {
    expect(passwordStrength("")).toEqual({ level: 0, label: "" });
  });

  it("calls anything under 12 characters too short, whatever it contains", () => {
    expect(passwordStrength("Aa1!Aa1!Aa1")).toEqual({ level: 1, label: "Too short" });
  });

  it("calls a plain 12-character password fair", () => {
    expect(passwordStrength("abcdefghijkl")).toEqual({ level: 2, label: "Fair" });
  });

  it("rewards length", () => {
    expect(passwordStrength("abcdefghijklmnop").level).toBe(3);
    expect(passwordStrength("a".repeat(24) + "b").level).toBe(4);
  });

  it("rewards a mix of character kinds a little", () => {
    expect(passwordStrength("Abcdefghij12").level).toBe(3);
    expect(passwordStrength("Abcdefghijklmn12").level).toBe(4);
  });
});
