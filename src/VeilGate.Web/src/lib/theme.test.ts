import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { applyTheme, currentTheme, setTheme } from "./theme";

function osPrefersLight(light: boolean) {
  vi.stubGlobal("matchMedia", () => ({ matches: light }));
}

describe("theme", () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.className = "";
  });
  afterEach(() => vi.unstubAllGlobals());

  it("uses the saved choice first", () => {
    osPrefersLight(true);
    localStorage.setItem("theme", "dark");
    expect(currentTheme()).toBe("dark");
  });

  it("follows the operating system when nothing is saved", () => {
    osPrefersLight(true);
    expect(currentTheme()).toBe("light");
    osPrefersLight(false);
    expect(currentTheme()).toBe("dark");
  });

  it("ignores a saved value it does not know", () => {
    osPrefersLight(false);
    localStorage.setItem("theme", "purple");
    expect(currentTheme()).toBe("dark");
  });

  it("applies dark by adding the class and light by removing it", () => {
    applyTheme("dark");
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    applyTheme("light");
    expect(document.documentElement.classList.contains("dark")).toBe(false);
  });

  it("remembers a choice made with setTheme", () => {
    setTheme("light");
    expect(localStorage.getItem("theme")).toBe("light");
  });
});
