export type IdleOptions = {
  timeoutMs: number; // total quiet time before sign-out
  warnMs: number; // how long before that the warning shows
  onWarn: () => void;
  onTimeout: () => void;
};

export function createIdleTimer({
  timeoutMs,
  warnMs,
  onWarn,
  onTimeout,
}: IdleOptions) {
  let warnTimer: ReturnType<typeof setTimeout> | undefined;
  let endTimer: ReturnType<typeof setTimeout> | undefined;

  function stop() {
    clearTimeout(warnTimer);
    clearTimeout(endTimer);
  }

  function reset() {
    stop();
    warnTimer = setTimeout(onWarn, timeoutMs - warnMs);
    endTimer = setTimeout(onTimeout, timeoutMs);
  }

  return { reset, stop };
}
