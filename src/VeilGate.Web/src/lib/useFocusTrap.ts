import { useEffect, type RefObject } from "react";

const FOCUSABLE = [
  "a[href]",
  "button:not([disabled])",
  "textarea:not([disabled])",
  'input:not([disabled]):not([type="hidden"])',
  "select:not([disabled])",
  '[tabindex]:not([tabindex="-1"])',
].join(",");

/**
 * For a dialog: moves focus into it when it opens, keeps Tab and Shift+Tab inside it, and gives focus back to whatever
 * had it when the dialog closes. Without this, keyboard and screen reader users end up behind the dialog.
 */
export function useFocusTrap(ref: RefObject<HTMLElement | null>): void {
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;

    const before = document.activeElement as HTMLElement | null;

    // Something inside may already have taken focus (autoFocus); only move it if not.
    if (!dialog.contains(document.activeElement)) {
      (dialog.querySelector<HTMLElement>(FOCUSABLE) ?? dialog).focus();
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Tab" || !dialog) return;
      const items = [...dialog.querySelectorAll<HTMLElement>(FOCUSABLE)];
      if (items.length === 0) return;
      const first = items[0];
      const last = items[items.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      before?.focus?.();
    };
  }, [ref]);
}
