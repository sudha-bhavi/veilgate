import { Link, useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Badge, { type BadgeTone } from "../components/Badge";
import Button from "../components/Button";
import Pager from "../components/Pager";
import { describeApiError } from "../lib/apiErrorText";
import { pageCount, readPage } from "../lib/paging";
import {
  entitySummary,
  listRequestActions,
  listRequests,
  requestCaller,
  type RequestStatus,
} from "../lib/requestsApi";

const field =
  "rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

const STATUS_TONES: Record<RequestStatus, BadgeTone> = {
  success: "success",
  failure: "danger",
  denied: "warning",
};

// The filters live in the address bar, so a filtered view can be bookmarked or sent to a colleague.
const FILTERS = ["action", "status", "from", "to", "requestId"] as const;

/**
 * What went through the gateway, newest first. Each row holds counts and names, never the text. A call appears here a moment
 * after it was made, because it travels through the event stream first.
 */
export default function RequestsPage() {
  const [params, setParams] = useSearchParams();
  const filters = Object.fromEntries(FILTERS.map((key) => [key, params.get(key) ?? ""])) as Record<
    (typeof FILTERS)[number],
    string
  >;
  const page = readPage(params.get("page"));

  const requests = useQuery({
    queryKey: ["requests", { ...filters, page }],
    queryFn: () =>
      listRequests({
        action: filters.action,
        status: filters.status,
        requestId: filters.requestId,
        fromDay: filters.from,
        toDay: filters.to,
        page,
      }),
    placeholderData: keepPreviousData,
  });
  const actions = useQuery({ queryKey: ["request-actions"], queryFn: listRequestActions });

  function update(next: Record<string, string>) {
    const merged = new URLSearchParams(params);
    for (const [key, value] of Object.entries(next)) {
      if (value) merged.set(key, value);
      else merged.delete(key);
    }
    merged.delete("page"); // a new filter starts at the first page
    setParams(merged);
  }

  const filtered = FILTERS.some((key) => filters[key] !== "");
  const data = requests.data;
  const pages = data ? pageCount(data.total, data.pageSize) : 1;

  return (
    <main className="mx-auto max-w-6xl space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold">Requests</h1>
        <p className="mt-1 text-muted">
          What went through the gateway: when, who sent it, and how many sensitive items were
          protected. It never holds the text itself.
        </p>
      </header>

      <div className="flex flex-wrap items-end gap-3">
        <div>
          <label htmlFor="request-action" className="mb-1 block text-muted">
            Action
          </label>
          <select
            id="request-action"
            value={filters.action}
            onChange={(event) => update({ action: event.target.value })}
            className={field}
          >
            <option value="">All actions</option>
            {(actions.data ?? []).map((action) => (
              <option key={action} value={action}>
                {action}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="request-status" className="mb-1 block text-muted">
            Result
          </label>
          <select
            id="request-status"
            value={filters.status}
            onChange={(event) => update({ status: event.target.value })}
            className={field}
          >
            <option value="">Any result</option>
            <option value="success">Success</option>
            <option value="failure">Failure</option>
            <option value="denied">Denied</option>
          </select>
        </div>
        <div>
          <label htmlFor="request-from" className="mb-1 block text-muted">
            From
          </label>
          <input
            id="request-from"
            type="date"
            value={filters.from}
            max={filters.to || undefined}
            onChange={(event) => update({ from: event.target.value })}
            className={field}
          />
        </div>
        <div>
          <label htmlFor="request-to" className="mb-1 block text-muted">
            To
          </label>
          <input
            id="request-to"
            type="date"
            value={filters.to}
            min={filters.from || undefined}
            onChange={(event) => update({ to: event.target.value })}
            className={field}
          />
        </div>
        {filtered && (
          <Button variant="ghost" onClick={() => setParams(new URLSearchParams())}>
            Clear filters
          </Button>
        )}
      </div>

      {requests.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(requests.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => requests.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {requests.isPending && (
        <div className="space-y-2" aria-label="Loading the requests">
          {[0, 1, 2].map((n) => (
            <div key={n} className="h-12 animate-pulse rounded-lg bg-line" />
          ))}
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="rounded-xl border border-line bg-card p-6 text-center">
          <p>{filtered ? "No requests match these filters." : "No requests yet."}</p>
        </div>
      )}

      {data && data.items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">Gateway requests, newest first</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">When</th>
                <th className="px-4 py-2 font-medium">Sent by</th>
                <th className="px-4 py-2 font-medium">Action</th>
                <th className="px-4 py-2 font-medium">Result</th>
                <th className="px-4 py-2 font-medium">Protected</th>
                <th className="px-4 py-2 font-medium">Time</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((request) => (
                <tr key={request.id} className="border-t border-line align-top">
                  <td className="whitespace-nowrap px-4 py-3 text-muted">
                    <Link to={`/requests/${request.id}`} className="text-accent hover:underline">
                      <time dateTime={request.occurredAt}>
                        {new Date(request.occurredAt).toLocaleString()}
                      </time>
                    </Link>
                  </td>
                  <td className="px-4 py-3">{requestCaller(request)}</td>
                  <td className="px-4 py-3 font-mono text-[13px]">{request.action}</td>
                  <td className="px-4 py-3">
                    <Badge tone={STATUS_TONES[request.status]}>{request.status}</Badge>
                  </td>
                  <td className="px-4 py-3">
                    {request.entityCount} <span className="text-muted">({entitySummary(request.entities)})</span>
                  </td>
                  <td className="whitespace-nowrap px-4 py-3 text-muted">
                    {request.latencyMs === null ? "–" : `${request.latencyMs} ms`}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.total > 0 && (
        <Pager
          page={page}
          pages={pages}
          summary={`${data.total.toLocaleString()} ${data.total === 1 ? "request" : "requests"}`}
          onPage={(next) => {
            const merged = new URLSearchParams(params);
            if (next > 1) merged.set("page", String(next));
            else merged.delete("page");
            setParams(merged);
          }}
        />
      )}
    </main>
  );
}
