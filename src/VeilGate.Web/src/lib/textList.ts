/** Splits text on new lines and commas, trims each piece, and drops blanks and repeats (keeping the first of each). */
export function parseList(text: string): string[] {
  const seen = new Set<string>();
  for (const piece of text.split(/[\n,]+/)) {
    const item = piece.trim();
    if (item) seen.add(item);
  }
  return [...seen];
}
