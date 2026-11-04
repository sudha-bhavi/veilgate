import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import RequestsPage from "./RequestsPage";

const request = (over: Record<string, unknown> = {}) => ({
  id: "m2",
  requestId: "req_b",
  occurredAt: "2026-11-03T14:46:38Z",
  actorType: "client",
  clientId: "c1",
  clientName: "ehr-bridge",
  action: "gateway.chat",
  status: "success",
  reason: null,
  model: "gpt-4o-mini",
  provider: "mock",
  policyVersion: "7",
  entityCount: 3,
  entities: { PHONE: 2, SSN: 1 },
  latencyMs: 120,
  ...over,
});

const page = (items: unknown[]) => ({ items, page: 1, pageSize: 50, total: items.length });

const two = [
  request(),
  request({ id: "m1", requestId: "req_a", clientName: null, status: "denied", action: "gateway.deidentify", entityCount: 0, entities: {}, latencyMs: null }),
];

function fakeServer(items: unknown[] = two) {
  return stubFetch((url) => {
    if (url.endsWith("/requests/actions")) return json({ items: ["gateway.chat", "gateway.deidentify"] });
    return json(page(items));
  });
}

const listCalls = (fetchMock: ReturnType<typeof stubFetch>) =>
  fetchMock.mock.calls.map(([url]) => String(url)).filter((url) => url.startsWith("/api/v1/requests?"));

const options = { path: "/requests", route: "/requests", role: "Developer" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("RequestsPage", () => {
  it("lists each call with who sent it, the result and what was protected", async () => {
    fakeServer();
    renderPage(<RequestsPage />, options);

    const rows = within(await screen.findByRole("table")).getAllByRole("row").slice(1);
    expect(within(rows[0]).getByText("ehr-bridge")).toBeInTheDocument();
    expect(within(rows[0]).getByText("success")).toBeInTheDocument();
    expect(within(rows[0]).getByText("(PHONE 2, SSN 1)")).toBeInTheDocument();
    expect(within(rows[0]).getByText("120 ms")).toBeInTheDocument();
    expect(within(rows[1]).getByText("API client c1 (no longer exists)")).toBeInTheDocument();
    expect(within(rows[1]).getByText("denied")).toBeInTheDocument();
    expect(screen.getByText(/^2 requests/)).toBeInTheDocument();
  });

  it("links each row to its own page", async () => {
    fakeServer();
    renderPage(<RequestsPage />, options);

    const rows = within(await screen.findByRole("table")).getAllByRole("row").slice(1);
    expect(within(rows[0]).getByRole("link")).toHaveAttribute("href", "/requests/m2");
  });

  it("sends a chosen result to the server and starts again at the first page", async () => {
    const fetchMock = fakeServer();
    renderPage(<RequestsPage />, options);
    await screen.findByRole("table");

    await userEvent.selectOptions(screen.getByLabelText("Result"), "denied");

    await waitFor(() => expect(listCalls(fetchMock).some((url) => url.includes("status=denied") && url.includes("page=1"))).toBe(true));
  });

  it("offers the actions that occur as a filter", async () => {
    fakeServer();
    renderPage(<RequestsPage />, options);

    expect(await screen.findByRole("option", { name: "gateway.deidentify" })).toBeInTheDocument();
  });

  it("says so when there are no requests yet", async () => {
    fakeServer([]);
    renderPage(<RequestsPage />, options);

    expect(await screen.findByText("No requests yet.")).toBeInTheDocument();
  });

  it("says so when the filters match nothing, and offers to clear them", async () => {
    fakeServer([]);
    renderPage(<RequestsPage />, { path: "/requests?status=denied", route: "/requests", role: "Developer" });

    expect(await screen.findByText("No requests match these filters.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear filters" })).toBeInTheDocument();
  });

  it("shows the error and a retry when the server fails", async () => {
    stubFetch(() => json({ code: "server.error", title: "Something went wrong." }, 500));
    renderPage(<RequestsPage />, options);

    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  });
});
