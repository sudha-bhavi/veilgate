import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Badge, { type BadgeTone } from "../components/Badge";
import Button from "../components/Button";
import { describeApiError } from "../lib/apiErrorText";
import { ApiError } from "../lib/api";
import { entitySummary, getRequest, requestCaller, type RequestStatus } from "../lib/requestsApi";

const STATUS_TONES: Record<RequestStatus, BadgeTone> = {
  success: "success",
  failure: "danger",
  denied: "warning",
};

/** One gateway call in full. Like the list, it shows counts and names only, never the text that went through. */
export default function RequestDetailPage() {
  const { id = "" } = useParams();
  const request = useQuery({ queryKey: ["requests", id], queryFn: () => getRequest(id) });

  const back = (
    <Link to="/requests" className="text-muted hover:text-fg">
      ← Requests
    </Link>
  );

  if (request.isPending) {
    return (
      <main className="mx-auto max-w-3xl space-y-4 p-6" aria-label="Loading the request">
        {back}
        <div className="h-40 animate-pulse rounded-xl bg-line" />
      </main>
    );
  }

  if (request.isError) {
    const missing = request.error instanceof ApiError && request.error.status === 404;
    return (
      <main className="mx-auto max-w-3xl space-y-4 p-6">
        {back}
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{missing ? "This request was not found." : describeApiError(request.error)}</p>
          {!missing && (
            <Button className="mt-3" variant="secondary" onClick={() => request.refetch()}>
              Retry
            </Button>
          )}
        </div>
      </main>
    );
  }

  const r = request.data;
  const rows: [string, string | null][] = [
    ["Request id", r.requestId],
    ["When", new Date(r.occurredAt).toLocaleString()],
    ["Sent by", requestCaller(r)],
    ["Action", r.action],
    ["Reason", r.reason],
    ["Model", r.model],
    ["Provider", r.provider],
    ["Policy version", r.policyVersion],
    ["Protected", `${r.entityCount} (${entitySummary(r.entities)})`],
    ["Time", r.latencyMs === null ? null : `${r.latencyMs} ms`],
  ];

  return (
    <main className="mx-auto max-w-3xl space-y-4 p-6">
      {back}
      <header className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">Request {r.requestId}</h1>
        <Badge tone={STATUS_TONES[r.status]}>{r.status}</Badge>
      </header>
      <p className="text-muted">Counts and names only. The text that went through the gateway is never kept here.</p>
      <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 rounded-xl border border-line bg-card p-4">
        {rows
          .filter(([, value]) => value !== null && value !== "")
          .map(([label, value]) => (
            <div key={label} className="contents">
              <dt className="text-muted">{label}</dt>
              <dd className="break-all">{value}</dd>
            </div>
          ))}
      </dl>
    </main>
  );
}
