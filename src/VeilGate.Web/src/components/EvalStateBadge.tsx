import Badge, { type BadgeTone } from "./Badge";

const tones: Record<string, BadgeTone> = {
  Queued: "muted",
  Running: "warning",
  Succeeded: "success",
  Failed: "danger",
};

export default function EvalStateBadge({ state }: { state: string }) {
  return <Badge tone={tones[state] ?? "muted"}>{state}</Badge>;
}
