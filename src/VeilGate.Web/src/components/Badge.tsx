import type { ReactNode } from "react";

export type BadgeTone = "success" | "warning" | "danger" | "muted";

const tones: Record<BadgeTone, string> = {
  success: "border-success text-success",
  warning: "border-warning text-warning",
  danger: "border-danger text-danger",
  muted: "border-line text-muted",
};

/** A small status label. The words carry the meaning; the colour only backs them up. */
export default function Badge({
  tone = "muted",
  children,
}: {
  tone?: BadgeTone;
  children: ReactNode;
}) {
  return (
    <span
      className={`inline-flex rounded-full border px-2 py-0.5 text-xs font-medium ${tones[tone]}`}
    >
      {children}
    </span>
  );
}
