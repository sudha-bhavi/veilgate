import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useIdleSignOut } from "./useIdleSignOut";

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe("useIdleSignOut", () => {
  it("warns shortly before the timeout, then signs out", () => {
    const onTimeout = vi.fn();
    const { result } = renderHook(() =>
      useIdleSignOut(onTimeout, 10_000, 2_000),
    );

    act(() => vi.advanceTimersByTime(7_999));
    expect(result.current.warning).toBe(false);

    act(() => vi.advanceTimersByTime(1));
    expect(result.current.warning).toBe(true);
    expect(onTimeout).not.toHaveBeenCalled();

    act(() => vi.advanceTimersByTime(2_000));
    expect(onTimeout).toHaveBeenCalledTimes(1);
  });

  it("restarts the clock when the person chooses to stay", () => {
    const onTimeout = vi.fn();
    const { result } = renderHook(() =>
      useIdleSignOut(onTimeout, 10_000, 2_000),
    );

    act(() => vi.advanceTimersByTime(8_000));
    expect(result.current.warning).toBe(true);

    act(() => result.current.stayActive());
    expect(result.current.warning).toBe(false);

    act(() => vi.advanceTimersByTime(9_999));
    expect(onTimeout).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(1));
    expect(onTimeout).toHaveBeenCalledTimes(1);
  });

  it("treats activity before the warning as a fresh start", () => {
    const { result } = renderHook(() => useIdleSignOut(vi.fn(), 10_000, 2_000));

    act(() => vi.advanceTimersByTime(5_000));
    act(() => {
      window.dispatchEvent(new Event("keydown"));
    });

    act(() => vi.advanceTimersByTime(7_999));
    expect(result.current.warning).toBe(false);
    act(() => vi.advanceTimersByTime(1));
    expect(result.current.warning).toBe(true);
  });
});
