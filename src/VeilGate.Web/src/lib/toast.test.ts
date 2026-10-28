import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  clearToastsForTests,
  dismissToast,
  getToasts,
  showToast,
} from "./toast";

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  vi.useRealTimers();
  clearToastsForTests();
});

describe("toasts", () => {
  it("shows a message and removes it after a few seconds", () => {
    showToast("Saved", "success");
    expect(getToasts().map((t) => t.message)).toEqual(["Saved"]);
    vi.advanceTimersByTime(4000);
    expect(getToasts()).toEqual([]);
  });

  it("keeps an error longer than a success", () => {
    showToast("Failed", "error");
    vi.advanceTimersByTime(4000);
    expect(getToasts()).toHaveLength(1);
    vi.advanceTimersByTime(4000);
    expect(getToasts()).toEqual([]);
  });

  it("can be dismissed early", () => {
    showToast("Hello");
    dismissToast(getToasts()[0].id);
    expect(getToasts()).toEqual([]);
  });
});
