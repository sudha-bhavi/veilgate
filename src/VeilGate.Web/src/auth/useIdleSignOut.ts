import { useCallback, useEffect, useRef, useState } from "react";
import { createIdleTimer } from "../lib/idle";

export const IDLE_TIMEOUT_MS = 15 * 60 * 1000;
export const IDLE_WARNING_MS = 60 * 1000;

const ACTIVITY_EVENTS = [
  "pointerdown",
  "keydown",
  "scroll",
  "touchstart",
  "mousemove",
] as const;
const THROTTLE_MS = 1000;

export function useIdleSignOut(
  onTimeout: () => void,
  timeoutMs = IDLE_TIMEOUT_MS,
  warnMs = IDLE_WARNING_MS,
) {
  const [warning, setWarning] = useState(false);
  const warningRef = useRef(false);
  const timerRef = useRef<ReturnType<typeof createIdleTimer> | null>(null);
  const onTimeoutRef = useRef(onTimeout);

  useEffect(() => {
    onTimeoutRef.current = onTimeout; // always call the latest callback without restarting the timer
  });

  useEffect(() => {
    const timer = createIdleTimer({
      timeoutMs,
      warnMs,
      onWarn: () => {
        warningRef.current = true;
        setWarning(true);
      },
      onTimeout: () => onTimeoutRef.current(),
    });
    timerRef.current = timer;
    timer.reset();

    let last = Date.now();
    const onActivity = () => {
      if (warningRef.current) return; // once the warning shows, only the button counts
      const now = Date.now();
      if (now - last < THROTTLE_MS) return;
      last = now;
      timer.reset();
    };

    ACTIVITY_EVENTS.forEach((name) =>
      window.addEventListener(name, onActivity, { passive: true }),
    );
    return () => {
      timer.stop();
      ACTIVITY_EVENTS.forEach((name) =>
        window.removeEventListener(name, onActivity),
      );
    };
  }, [timeoutMs, warnMs]);

  const stayActive = useCallback(() => {
    warningRef.current = false;
    setWarning(false);
    timerRef.current?.reset();
  }, []);

  return { warning, stayActive };
}
