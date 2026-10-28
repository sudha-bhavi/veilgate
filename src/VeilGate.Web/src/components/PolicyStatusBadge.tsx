import { statusLabel } from "../lib/policyStatus";

const tone: Record<string, string> = {
  PendingApproval: "border-warning text-warning",
  Published: "border-success text-success",
};

export default function PolicyStatusBadge({ status }: { status: string }) {
  return (
    <span
      className={`inline-flex rounded-full border px-2 py-0.5 text-xs font-medium ${tone[status] ?? "border-line text-muted"}`}
    >
      {statusLabel(status)}
    </span>
  );
}
