export const CODE_LENGTH = 6;

export const digitsOnly = (value: string): string => value.replace(/\D/g, "");

type Result = { boxes: string[]; focus: number };

/** Typing or pasting `input` into box `index`. Six pasted digits fill every box; anything that is not a digit is ignored. */
export function applyInput(
  boxes: string[],
  index: number,
  input: string,
): Result {
  const digits = digitsOnly(input);
  if (!digits) return { boxes, focus: index };

  const next = [...boxes];
  let position = digits.length >= CODE_LENGTH ? 0 : index;
  for (const digit of digits) {
    if (position >= CODE_LENGTH) break;
    next[position] = digit;
    position++;
  }
  return { boxes: next, focus: Math.min(position, CODE_LENGTH - 1) };
}

/**
 * What a box's change event typed. Normally one key, so only the newest digit counts (the box may still hold the old one).
 * Authenticator and password-manager autofill put the whole code into one box at once: then every digit counts.
 */
export function typedIntoBox(value: string): string {
  const digits = digitsOnly(value);
  return digits.length >= CODE_LENGTH ? digits : digits.slice(-1);
}

/** Backspace: clears this box, or, if it is already empty, the one before it. */
export function removeAt(boxes: string[], index: number): Result {
  const next = [...boxes];
  if (next[index]) {
    next[index] = "";
    return { boxes: next, focus: index };
  }
  const previous = Math.max(0, index - 1);
  next[previous] = "";
  return { boxes: next, focus: previous };
}
