/** What the strength bar shows. `level` is 0 to 4 and `label` is always shown too, so colour is never the only signal. */
export type Strength = { level: 0 | 1 | 2 | 3 | 4; label: string };

/**
 * A hint, not a rule: VeilGate asks for length rather than symbols (12 to 128 characters), so length is what the bar rewards,
 * with a small bonus for a mix of character kinds. The server decides what is accepted.
 */
export function passwordStrength(password: string): Strength {
  if (password === "") return { level: 0, label: "" };
  if (password.length < 12) return { level: 1, label: "Too short" };

  const kinds = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((kind) => kind.test(password)).length;
  const points = (password.length >= 16 ? 1 : 0) + (password.length >= 24 ? 1 : 0) + (kinds >= 3 ? 1 : 0);
  if (points === 0) return { level: 2, label: "Fair" };
  if (points === 1) return { level: 3, label: "Good" };
  return { level: 4, label: "Strong" };
}
