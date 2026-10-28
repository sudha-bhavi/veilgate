import { useSyncExternalStore } from "react";
import {
  CircleAlert,
  CircleCheck,
  Info,
  X,
  type LucideIcon,
} from "lucide-react";
import {
  dismissToast,
  getToasts,
  subscribe,
  type ToastKind,
} from "../lib/toast";

const look: Record<ToastKind, { icon: LucideIcon; color: string }> = {
  success: { icon: CircleCheck, color: "text-success" },
  error: { icon: CircleAlert, color: "text-danger" },
  info: { icon: Info, color: "text-accent" },
};

export default function ToastHost() {
  const toasts = useSyncExternalStore(subscribe, getToasts);

  return (
    <div
      aria-live="polite"
      className="fixed bottom-4 right-4 z-50 flex w-80 flex-col gap-2"
    >
      {toasts.map(({ id, kind, message }) => {
        const { icon: Icon, color } = look[kind];
        return (
          <div
            key={id}
            role={kind === "error" ? "alert" : "status"}
            className="flex items-start gap-3 rounded-xl border border-line bg-surface p-3 shadow-lg"
          >
            <Icon
              className={`mt-0.5 size-4 shrink-0 ${color}`}
              aria-hidden="true"
            />
            <p className="flex-1">{message}</p>
            <button
              type="button"
              onClick={() => dismissToast(id)}
              aria-label="Dismiss"
              className="text-muted hover:text-fg"
            >
              <X className="size-4" aria-hidden="true" />
            </button>
          </div>
        );
      })}
    </div>
  );
}
