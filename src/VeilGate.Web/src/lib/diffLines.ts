export type DiffKind = "add" | "remove" | "hunk" | "meta" | "context";

/** What a line of a unified diff is, so it can be coloured. */
export function classifyDiffLine(line: string): DiffKind {
  if (line.startsWith("+++") || line.startsWith("---")) return "meta";
  if (line.startsWith("@@")) return "hunk";
  if (line.startsWith("+")) return "add";
  if (line.startsWith("-")) return "remove";
  return "context";
}
