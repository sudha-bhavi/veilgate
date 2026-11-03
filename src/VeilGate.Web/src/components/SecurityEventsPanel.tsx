import { Fragment, useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Info, ShieldAlert, TriangleAlert, type LucideIcon } from "lucide-react";
import { SECURITY_OVERSIGHT_ROLES } from "../auth/roles";
import { useSession } from "../auth/useSession";
import { describeApiError } from "../lib/apiErrorText";
import { pageCount } from "../lib/paging";
import { eventSubject, eventTitle, severityLook, type SeverityLook } from "../lib/securityEventText";
import {
  listSecurityEventPeople,
  listSecurityEvents,
  SEVERITIES,
  type SecurityEvent,
} from "../lib/securityEventsApi";
import Badge from "./Badge";
import Button from "./Button";
import Pager from "./Pager";

const field =
  "rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

const ICONS: Record<SeverityLook["level"], LucideIcon> = {
  info: Info,
  warning: TriangleAlert,
  critical: ShieldAlert,
};

/**
 * The Security events tab. Tenant Admins and Security Officers see the whole organization and can filter by person;
 * everyone else sees only events about their own account. The server decides what comes back: this only chooses what to offer.
 */
export default function SecurityEventsPanel() {
  const session = useSession();
  const oversight =
    session.status === "signedIn" && (SECURITY_OVERSIGHT_ROLES as readonly string[]).includes(session.claims.role);

  const [userId, setUserId] = useState("");
  const [severity, setSeverity] = useState("");
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | null>(null);

  const events = useQuery({
    queryKey: ["security-events", { userId, severity, page }],
    queryFn: () => listSecurityEvents({ userId, severity, page }),
    placeholderData: keepPreviousData,
  });
  const people = useQuery({
    queryKey: ["security-event-people"],
    queryFn: listSecurityEventPeople,
    enabled: oversight,
  });

  const data = events.data;
  const pages = data ? pageCount(data.total, data.pageSize) : 1;
  const filtered = userId !== "" || severity !== "";
  const columns = oversight ? 4 : 3;

  return (
    <section aria-labelledby="events-heading" className="space-y-4">
      <div>
        <h2 id="events-heading" className="text-lg font-semibold">
          Security events
        </h2>
        <p className="mt-1 text-muted">
          {oversight
            ? "Failed sign-ins, lockouts, two-step changes and blocked leaks across your organisation."
            : "Security events about your own account."}
        </p>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        {oversight && (
          <div>
            <label htmlFor="event-user" className="mb-1 block text-muted">
              User
            </label>
            <select
              id="event-user"
              value={userId}
              onChange={(event) => {
                setUserId(event.target.value);
                setPage(1);
              }}
              className={field}
            >
              <option value="">Everyone</option>
              {people.data?.map((person) => (
                <option key={person.id} value={person.id}>
                  {person.name} ({person.email})
                </option>
              ))}
            </select>
          </div>
        )}
        <div>
          <label htmlFor="event-severity" className="mb-1 block text-muted">
            Severity
          </label>
          <select
            id="event-severity"
            value={severity}
            onChange={(event) => {
              setSeverity(event.target.value);
              setPage(1);
            }}
            className={field}
          >
            <option value="">All severities</option>
            {SEVERITIES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </div>
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
        <div className="space-y-2" aria-label="Loading security events">
          {[0, 1, 2].map((n) => (
            <div key={n} className="h-12 animate-pulse rounded-lg bg-line" />
          ))}
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="rounded-xl border border-line bg-card p-6 text-center">
          <p>{filtered ? "No events match these filters." : "No security events. That is good news."}</p>
        </div>
      )}

      {data && data.items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">Security events</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">Severity</th>
                <th className="px-4 py-2 font-medium">Event</th>
                {oversight && <th className="px-4 py-2 font-medium">Person</th>}
                <th className="px-4 py-2 font-medium">When</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((event) => (
                <EventRow
                  key={event.id}
                  event={event}
                  oversight={oversight}
                  columns={columns}
                  expanded={open === event.id}
                  onToggle={() => setOpen(open === event.id ? null : event.id)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.total > 0 && (
        <Pager
          page={page}
          pages={pages}
          summary={`${data.total} ${data.total === 1 ? "event" : "events"}`}
          onPage={setPage}
        />
      )}
    </section>
  );
}

function EventRow({
  event,
  oversight,
  columns,
  expanded,
  onToggle,
}: {
  event: SecurityEvent;
  oversight: boolean;
  columns: number;
  expanded: boolean;
  onToggle: () => void;
}) {
  const look = severityLook(event.severity);
  const Icon = ICONS[look.level];
  const title = eventTitle(event.type);
  const details = Object.entries(event.details);

  return (
    <Fragment>
      <tr className="border-t border-line">
        <td className="px-4 py-3">
          <Badge tone={look.tone}>
            <span className="inline-flex items-center gap-1">
              <Icon className="size-3" aria-hidden="true" />
              {event.severity}
            </span>
          </Badge>
        </td>
        <td className="px-4 py-3 font-medium">
          <button
            type="button"
            aria-expanded={expanded}
            aria-label={`${title}, ${expanded ? "hide" : "show"} details`}
            onClick={onToggle}
            className="rounded text-left hover:underline focus-visible:outline-2 focus-visible:outline-accent"
          >
            {title}
          </button>
        </td>
        {oversight && <td className="px-4 py-3 text-muted">{eventSubject(event)}</td>}
        <td className="px-4 py-3 text-muted">{new Date(event.occurredAt).toLocaleString()}</td>
      </tr>
      {expanded && (
        <tr className="bg-surface">
          <td colSpan={columns} className="px-4 py-3">
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
              <dt className="text-muted">Time</dt>
              <dd>{new Date(event.occurredAt).toLocaleString()}</dd>
              <dt className="text-muted">Type</dt>
              <dd className="font-mono text-sm">{event.type}</dd>
              <dt className="text-muted">IP address</dt>
              <dd>{event.ip ?? "Not recorded"}</dd>
              {details.map(([key, value]) => (
                <Fragment key={key}>
                  <dt className="text-muted">{key}</dt>
                  <dd>{value}</dd>
                </Fragment>
              ))}
            </dl>
          </td>
        </tr>
      )}
    </Fragment>
  );
}
