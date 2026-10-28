export type ToastKind = "success" | "error" | "info";
export type Toast = { id: number; kind: ToastKind; message: string };

// Errors stay longer so people have time to read them.
const SHOW_MS: Record<ToastKind, number> = {
  success: 4000,
  info: 4000,
  error: 8000,
};

let toasts: readonly Toast[] = [];
let nextId = 1;
const listeners = new Set<() => void>();

function set(next: readonly Toast[]) {
  toasts = next;
  listeners.forEach((listener) => listener());
}

export const getToasts = (): readonly Toast[] => toasts;

export function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function dismissToast(id: number): void {
  set(toasts.filter((toast) => toast.id !== id));
}

export function showToast(message: string, kind: ToastKind = "info"): void {
  const id = nextId++;
  set([...toasts, { id, kind, message }]);
  setTimeout(() => dismissToast(id), SHOW_MS[kind]);
}

/** For tests only. */
export function clearToastsForTests(): void {
  set([]);
}
