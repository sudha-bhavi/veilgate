/** 0.9871 -> "98.7%" */
export const percent = (value: number, digits = 1): string =>
  `${(value * 100).toFixed(digits)}%`;

/** A change in a ratio, in percentage points: 0.012 -> "+1.2 pts", -0.5 -> "-50.0 pts". */
export const signedPoints = (delta: number): string =>
  `${delta > 0 ? "+" : ""}${(delta * 100).toFixed(1)} pts`;

/** A run that has not finished: the page keeps asking the server about it. */
export const isActive = (state: string): boolean =>
  state === "Queued" || state === "Running";

export function progressPercent(done: number, total: number): number {
  if (total <= 0) return 0;
  return Math.min(100, Math.round((done / total) * 100));
}

export type Verdict = "better" | "worse" | "same";

/** Whether a change is good news. For recall, precision and F1 higher is better; for leaks, lower is. */
export function verdict(delta: number, betterWhen: "higher" | "lower"): Verdict {
  if (Math.abs(delta) < 1e-9) return "same";
  const wentUp = delta > 0;
  return wentUp === (betterWhen === "higher") ? "better" : "worse";
}

export const modeLabel = (mode: string): string =>
  mode === "rules+ner" ? "Rules and NER model" : "Rules only";
