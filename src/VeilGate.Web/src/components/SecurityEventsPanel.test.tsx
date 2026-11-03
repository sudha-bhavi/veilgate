import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import SecurityPage from "../pages/SecurityPage";

const event = (over: Record<string, unknown> = {}) => ({
  id: "e1",
  occurredAt: "2026-11-04T12:00:00Z",
  severity: "Medium",
  type: "auth.account_locked",
  userId: "u2",
  userName: "Ann Lee (ann@acme.test)",
  clientId: null,
  clientName: null,
  ip: "203.0.113.9",
  details: { reason: "password", count: "5" },
  ...over,
});

const page = (items: unknown[]) => ({ items, page: 1, pageSize: 25, total: items.length });

const PEOPLE = {
  items: [
    { id: "u2", name: "Ann Lee", email: "ann@acme.test" },
    { id: "u3", name: "Bo Chan", email: "bo@acme.test" },
  ],
};

/** A fake server: the sessions tab's list (opened first), the people list and the events. */
function fakeServer(events: unknown[] = [event(), event({ id: "e2", severity: "High", type: "leak.gate_tripped", userId: null, userName: null, clientId: "c1", clientName: "ehr-bridge" })]) {
  return stubFetch((url) => {
    if (url.includes("/security-events/people")) return json(PEOPLE);
    if (url.includes("/security-events")) return json(page(events));
    return json([]); // /auth/sessions
  });
}

async function openEventsTab(role: string) {
  renderPage(<SecurityPage />, { path: "/security", role });
  await userEvent.click(await screen.findByRole("tab", { name: "Security events" }));
}

const eventCalls = (fetchMock: ReturnType<typeof fakeServer>) =>
  fetchMock.mock.calls.map(([url]) => url).filter((url) => url.includes("/api/v1/security-events?"));

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("Security events tab", () => {
  it("shows the whole organisation to a tenant admin, with who each event is about", async () => {
    fakeServer();
    await openEventsTab("TenantAdmin");

    const table = await screen.findByRole("table", { name: "Security events" });
    expect(within(table).getByText("Person")).toBeInTheDocument();
    expect(within(table).getByText("Ann Lee (ann@acme.test)")).toBeInTheDocument();
    expect(within(table).getByText("API client ehr-bridge")).toBeInTheDocument();
    expect(within(table).getByText("Medium")).toBeInTheDocument();
    expect(within(table).getByText("High")).toBeInTheDocument();
    expect(screen.getByLabelText("User")).toBeInTheDocument();
  });

  it("offers the people of the organisation in the user filter and asks the server for that person only", async () => {
    const fetchMock = fakeServer();
    await openEventsTab("SecurityOfficer");

    await screen.findByRole("option", { name: "Ann Lee (ann@acme.test)" });
    await userEvent.selectOptions(screen.getByLabelText("User"), "u2");

    await waitFor(() => expect(eventCalls(fetchMock).some((url) => url.includes("userId=u2"))).toBe(true));
  });

  it("shows a developer only their own events, with no user filter and no request for the people list", async () => {
    const fetchMock = fakeServer([event()]);
    await openEventsTab("Developer");

    const table = await screen.findByRole("table", { name: "Security events" });
    expect(within(table).queryByText("Person")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("User")).not.toBeInTheDocument();
    expect(screen.getByText("Security events about your own account.")).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => url.includes("/people"))).toBe(false);
  });

  it("filters by severity", async () => {
    const fetchMock = fakeServer();
    await openEventsTab("TenantAdmin");

    await userEvent.selectOptions(await screen.findByLabelText("Severity"), "High");

    await waitFor(() => expect(eventCalls(fetchMock).some((url) => url.includes("severity=High"))).toBe(true));
  });

  it("shows the details of an event when its title is clicked, and hides them again", async () => {
    fakeServer();
    await openEventsTab("TenantAdmin");

    const toggle = await screen.findByRole("button", { name: /Account locked after repeated failed sign-ins, show details/ });
    await userEvent.click(toggle);

    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("203.0.113.9")).toBeInTheDocument();
    expect(screen.getByText("password")).toBeInTheDocument();

    await userEvent.click(toggle);
    expect(screen.queryByText("203.0.113.9")).not.toBeInTheDocument();
  });

  it("says so when there is nothing, and when the filters hide everything", async () => {
    fakeServer([]);
    await openEventsTab("TenantAdmin");

    expect(await screen.findByText("No security events. That is good news.")).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText("Severity"), "Low");
    expect(await screen.findByText("No events match these filters.")).toBeInTheDocument();
  });

  it("offers a retry when the events cannot be loaded", async () => {
    stubFetch((url) =>
      url.includes("/security-events?") ? json({ status: 500, title: "Boom", code: "server.error" }, 500) : json(url.includes("/people") ? PEOPLE : []),
    );
    await openEventsTab("TenantAdmin");

    expect(await screen.findByRole("button", { name: "Retry" })).toBeInTheDocument();
  });
});
