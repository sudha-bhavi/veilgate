import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import AuditLogPage from "./AuditLogPage";

const event = (over: Record<string, unknown> = {}) => ({
  id: "e2",
  seq: 2,
  occurredAt: "2026-10-29T14:46:38Z",
  actorType: "user",
  actorId: "u1",
  actorName: "Alex Admin (admin@acme.test)",
  action: "policy.publish",
  resourceType: "policy",
  resourceId: "p1",
  result: "success",
  ip: "203.0.113.5",
  details: { version: "2" },
  hash: "a".repeat(64),
  ...over,
});

const page = (items: unknown[]) => ({ items, page: 1, pageSize: 50, total: items.length });

const twoEvents = [
  event(),
  event({ id: "e1", seq: 1, actorType: "system", actorId: null, actorName: null, action: "vault.purge", result: "denied", details: {} }),
];

function fakeServer(items: unknown[] = twoEvents, check?: () => Response) {
  return stubFetch((url) => {
    if (url.endsWith("/audit-events/actions")) return json({ items: ["policy.publish", "vault.purge"] });
    if (url.endsWith("/audit-events/verify"))
      return check?.() ?? json({ ok: true, verified: 2, brokenAtSeq: null, expectedHash: null, actualHash: null });
    return json(page(items));
  });
}

const listCalls = (fetchMock: ReturnType<typeof stubFetch>) =>
  fetchMock.mock.calls.map(([url]) => String(url)).filter((url) => url.startsWith("/api/v1/audit-events?"));

// route is given because path may carry a query string, which is not part of the route pattern.
const options = { path: "/audit", route: "/audit", role: "Auditor" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("AuditLogPage", () => {
  it("lists who did what, newest first, and says when VeilGate itself acted", async () => {
    fakeServer();
    renderPage(<AuditLogPage />, options);

    const rows = within(await screen.findByRole("table")).getAllByRole("row").slice(1);
    expect(within(rows[0]).getByText("Alex Admin (admin@acme.test)")).toBeInTheDocument();
    expect(within(rows[0]).getByText("policy.publish")).toBeInTheDocument();
    expect(within(rows[0]).getByText("success")).toBeInTheDocument();
    expect(within(rows[1]).getByText("VeilGate (system)")).toBeInTheDocument();
    expect(within(rows[1]).getByText("denied")).toBeInTheDocument();
    expect(screen.getByText(/^2 events/)).toBeInTheDocument();
  });

  it("names an actor that no longer exists instead of leaving the cell empty", async () => {
    fakeServer([event({ actorType: "client", actorId: "c9", actorName: null })]);
    renderPage(<AuditLogPage />, options);

    expect(await screen.findByText("API client c9 (no longer exists)")).toBeInTheDocument();
  });

  it("keeps each event's address, details and hash behind its own disclosure", async () => {
    fakeServer();
    renderPage(<AuditLogPage />, options);

    const disclosure = (await screen.findByText("Event #2")).closest("details") as HTMLElement;
    expect(within(disclosure).getByText("203.0.113.5")).toBeInTheDocument();
    expect(within(disclosure).getByText("version")).toBeInTheDocument();
    expect(within(disclosure).getByText("a".repeat(64))).toBeInTheDocument();
  });

  it("asks the server for the chosen action and result", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<AuditLogPage />, options);

    await screen.findByRole("option", { name: "vault.purge" });
    await user.selectOptions(screen.getByLabelText("Action"), "vault.purge");
    await user.selectOptions(screen.getByLabelText("Result"), "denied");

    await waitFor(() => {
      const last = new URL(listCalls(fetchMock).at(-1)!, "http://x");
      expect(last.searchParams.get("action")).toBe("vault.purge");
      expect(last.searchParams.get("result")).toBe("denied");
      expect(last.searchParams.get("page")).toBe("1");
    });
  });

  it("sends picked days as a UTC range", async () => {
    const fetchMock = fakeServer();
    renderPage(<AuditLogPage />, { ...options, path: "/audit?from=2026-10-01&to=2026-10-02" });

    await waitFor(() => expect(listCalls(fetchMock)).not.toHaveLength(0));
    const sent = new URL(listCalls(fetchMock)[0], "http://x");
    expect(sent.searchParams.get("from")).toBe(new Date("2026-10-01T00:00:00").toISOString());
    expect(sent.searchParams.get("to")).toBe(new Date("2026-10-02T23:59:59.999").toISOString());
  });

  it("says when nothing matches the filters", async () => {
    fakeServer([]);
    renderPage(<AuditLogPage />, { ...options, path: "/audit?result=failure" });

    expect(await screen.findByText("No events match these filters.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear filters" })).toBeInTheDocument();
  });

  it("reports an intact chain", async () => {
    fakeServer();
    const user = userEvent.setup();
    renderPage(<AuditLogPage />, options);

    await user.click(await screen.findByRole("button", { name: "Check for tampering" }));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "The log is intact: all 2 events are in order and unchanged.",
    );
  });

  it("names the first broken event when the chain does not add up", async () => {
    fakeServer(twoEvents, () =>
      json({ ok: false, verified: 1, brokenAtSeq: 2, expectedHash: "x", actualHash: "y" }),
    );
    const user = userEvent.setup();
    renderPage(<AuditLogPage />, options);

    await user.click(await screen.findByRole("button", { name: "Check for tampering" }));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "The log has been tampered with: event #2 was deleted, changed or moved.",
    );
  });
});
