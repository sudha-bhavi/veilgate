import { useSearchParams } from "react-router-dom";
import { keepPreviousData, useMutation, useQuery } from "@tanstack/react-query";
import Badge, { type BadgeTone } from "../components/Badge";
import Button from "../components/Button";
import Pager from "../components/Pager";
import { describeApiError } from "../lib/apiErrorText";
import {
  checkAuditChain,
  listAuditActions,
  listAuditEvents,
  type AuditEvent,
  type AuditResult,
} from "../lib/auditApi";
import { pageCount, readPage } from "../lib/paging";

const field =
  "rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

const RESULT_TONES: Record<AuditResult, BadgeTone> = {
  success: "success",
  failure: "danger",
  denied: "warning",
};

// The filters live in the address bar, so a filtered view can be bookmarked or sent to a colleague.
const FILTERS = ["action", "actorType", "result", "from", "to", "resourceId"] as const;

function who(event: AuditEvent): string {
  if (event.actorType === "system") return "VeilGate (system)";
  const kind = event.actorType === "client" ? "API client" : "Person";
  return event.actorName ?? `${kind} ${event.actorId ?? ""} (no longer exists)`;
}

/**
 * The tenant's audit log: who did what and when, newest first. Read-only: nothing on this page can change an event.
 * "Check for tampering" asks the server to recompute the whole hash chain.
 */
export default function AuditLogPage() {
  const [params, setParams] = useSearchParams();
  const filters = Object.fromEntries(FILTERS.map((key) => [key, params.get(key) ?? ""])) as Record<
    (typeof FILTERS)[number],
    string
  >;
  const page = readPage(params.get("page"));

  const events = useQuery({
    queryKey: ["audit-events", { ...filters, page }],
    queryFn: () =>
      listAuditEvents({
        action: filters.action,
        actorType: filters.actorType,
        result: filters.result,
        resourceId: filters.resourceId,
        fromDay: filters.from,
        toDay: filters.to,
        page,
      }),
    placeholderData: keepPreviousData,
  });
  const actions = useQuery({ queryKey: ["audit-actions"], queryFn: listAuditActions });
  const check = useMutation({ mutationFn: checkAuditChain });

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
  const data = events.data;
  const pages = data ? pageCount(data.total, data.pageSize) : 1;

  return (
    <main className="mx-auto max-w-6xl space-y-6 p-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Audit log</h1>
          <p className="mt-1 text-muted">
            A read-only record of who did what, and when. It never holds the text
            that went through the gateway.
          </p>
        </div>
        <Button variant="secondary" loading={check.isPending} onClick={() => check.mutate()}>
          Check for tampering
        </Button>
      </header>

      {check.isError && (
        <p role="alert" className="text-danger">
          {describeApiError(check.error)}
        </p>
      )}
      {check.data && (
        <div
          role="status"
          className={`rounded-xl border p-4 ${check.data.ok ? "border-success" : "border-danger"}`}
        >
          {check.data.ok ? (
            <p className="text-success">
              The log is intact: all {check.data.verified.toLocaleString()} events are in
              order and unchanged.
            </p>
          ) : (
            <p className="text-danger">
              The log has been tampered with: event #{check.data.brokenAtSeq} was deleted,
              changed or moved. The {check.data.verified.toLocaleString()} events before it
              are intact.
            </p>
          )}
        </div>
      )}

      <div className="flex flex-wrap items-end gap-3">
        <div>
          <label htmlFor="audit-action" className="mb-1 block text-muted">
            Action
          </label>
          <select
            id="audit-action"
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
          <label htmlFor="audit-actor" className="mb-1 block text-muted">
            Done by
          </label>
          <select
            id="audit-actor"
            value={filters.actorType}
            onChange={(event) => update({ actorType: event.target.value })}
            className={field}
          >
            <option value="">Anyone</option>
            <option value="user">People</option>
            <option value="client">API clients</option>
            <option value="system">VeilGate itself</option>
          </select>
        </div>
        <div>
          <label htmlFor="audit-result" className="mb-1 block text-muted">
            Result
          </label>
          <select
            id="audit-result"
            value={filters.result}
            onChange={(event) => update({ result: event.target.value })}
            className={field}
          >
            <option value="">Any result</option>
            <option value="success">Success</option>
            <option value="failure">Failure</option>
            <option value="denied">Denied</option>
          </select>
        </div>
        <div>
          <label htmlFor="audit-from" className="mb-1 block text-muted">
            From
          </label>
          <input
            id="audit-from"
            type="date"
            value={filters.from}
            max={filters.to || undefined}
            onChange={(event) => update({ from: event.target.value })}
            className={field}
          />
        </div>
        <div>
          <label htmlFor="audit-to" className="mb-1 block text-muted">
            To
          </label>
          <input
            id="audit-to"
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

      {events.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(events.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => events.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {events.isPending && (
        <div className="space-y-2" aria-label="Loading the audit log">
          {[0, 1, 2].map((n) => (
            <div key={n} className="h-12 animate-pulse rounded-lg bg-line" />
          ))}
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="rounded-xl border border-line bg-card p-6 text-center">
          <p>{filtered ? "No events match these filters." : "Nothing has been recorded yet."}</p>
        </div>
      )}

      {data && data.items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">Audit events, newest first</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">When</th>
                <th className="px-4 py-2 font-medium">Who</th>
                <th className="px-4 py-2 font-medium">Action</th>
                <th className="px-4 py-2 font-medium">Result</th>
                <th className="px-4 py-2 font-medium">Details</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((event) => (
                <tr key={event.id} className="border-t border-line align-top">
                  <td className="whitespace-nowrap px-4 py-3 text-muted">
                    <time dateTime={event.occurredAt}>
                      {new Date(event.occurredAt).toLocaleString()}
                    </time>
                  </td>
                  <td className="px-4 py-3">{who(event)}</td>
                  <td className="px-4 py-3 font-mono text-[13px]">{event.action}</td>
                  <td className="px-4 py-3">
                    <Badge tone={RESULT_TONES[event.result]}>{event.result}</Badge>
                  </td>
                  <td className="px-4 py-3">
                    <details>
                      <summary className="cursor-pointer text-accent">
                        Event #{event.seq}
                      </summary>
                      <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-muted">
                        {event.resourceType && (
                          <>
                            <dt>Resource</dt>
                            <dd className="break-all font-mono text-[13px] text-fg">
                              {event.resourceType} {event.resourceId}
                            </dd>
                          </>
                        )}
                        {event.ip && (
                          <>
                            <dt>Address</dt>
                            <dd className="font-mono text-[13px] text-fg">{event.ip}</dd>
                          </>
                        )}
                        {Object.entries(event.details).map(([key, value]) => (
                          <div key={key} className="contents">
                            <dt>{key}</dt>
                            <dd className="break-all text-fg">{value}</dd>
                          </div>
                        ))}
                        <dt>Hash</dt>
                        <dd className="break-all font-mono text-[12px]">{event.hash}</dd>
                      </dl>
                    </details>
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
          summary={`${data.total.toLocaleString()} ${data.total === 1 ? "event" : "events"}`}
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
