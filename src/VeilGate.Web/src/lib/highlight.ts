export type Span = { start: number; end: number; type: string };
export type Segment = { text: string; type?: string };

/** Cuts text into plain and highlighted pieces. Where spans overlap, the earlier one wins. */
export function toSegments(text: string, spans: readonly Span[]): Segment[] {
  const sorted = [...spans].sort((a, b) => a.start - b.start || b.end - a.end);
  const out: Segment[] = [];
  let cursor = 0;
  for (const { start, end, type } of sorted) {
    const from = Math.max(start, cursor);
    const to = Math.min(end, text.length);
    if (from >= to) continue;
    if (from > cursor) out.push({ text: text.slice(cursor, from) });
    out.push({ text: text.slice(from, to), type });
    cursor = to;
  }
  if (cursor < text.length) out.push({ text: text.slice(cursor) });
  return out;
}
