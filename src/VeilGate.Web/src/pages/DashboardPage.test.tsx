import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import DashboardPage from "./DashboardPage";

const summary = (over: Record<string, unknown> = {}) => ({
  range: "24h",
  from: "2026-11-02T11:00:00Z",
  to: "2026-11-03T11:00:00Z",
  requests: 1234,
  entitiesProtected: 5678,
  blockedRequests: 12,
  failedRequests: 3,
  p50LatencyMs: 50,
  p95LatencyMs: 250,
  latestLeakRate: 0.02,
  entitiesByType: { PHONE: 400, SSN: 100 },
  topClients: [
    { clientId: "c1", name: "ehr-bridge", requests: 900 },
    { clientId: "0192a000-1111", name: null, requests: 334 },
  ],
  ...over,
});

const points = [
  { t: "2026-11-03T09:00:00Z", value: 4 },
  { t: "2026-11-03T10:00:00Z", value: null },
];

function fakeServer(summaryBody: unknown = summary()) {
  return stubFetch((url) => {
    if (url.includes("/dashboard/summary")) return json(summaryBody);
    const metric = new URL(url, "http://x").searchParams.get("metric");
    return json({ range: "24h", metric, points });
  });
}

const callsTo = (fetchMock: ReturnType<typeof stubFetch>, part: string) =>
  fetchMock.mock.calls.map(([url]) => String(url)).filter((url) => url.includes(part));

const options = { path: "/dashboard", route: "/dashboard", role: "Auditor" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("DashboardPage", () => {
  it("shows the headline numbers, with latencies as 'up to'", async () => {
    fakeServer();
    renderPage(<DashboardPage />, options);

    expect(await screen.findByText("1,234")).toBeInTheDocument();
    expect(screen.getByText("5,678")).toBeInTheDocument();
    expect(screen.getByText("12")).toBeInTheDocument();
    expect(screen.getByText("3 failed")).toBeInTheDocument();
    expect(screen.getByText("up to 250 ms")).toBeInTheDocument();
    expect(screen.getByText("Typical: up to 50 ms")).toBeInTheDocument();
    expect(screen.getByText("2.0%")).toBeInTheDocument();
  });

  it("says when there has been no evaluation and no latency yet", async () => {
    fakeServer(summary({ requests: 0, p50LatencyMs: null, p95LatencyMs: null, latestLeakRate: null, entitiesByType: {}, topClients: [] }));
    renderPage(<DashboardPage />, options);

    expect(await screen.findByText("No evaluation yet")).toBeInTheDocument();
    expect(screen.getByText("No requests in this period yet.")).toBeInTheDocument();
    expect(screen.getByText("Nothing was protected in this period.")).toBeInTheDocument();
    expect(screen.getByText("No calls in this period.")).toBeInTheDocument();
  });

  it("lists what was protected and the busiest callers, naming one that no longer exists", async () => {
    fakeServer();
    renderPage(<DashboardPage />, options);

    const types = within(await screen.findByRole("region", { name: "What was protected" }));
    expect(types.getByText("PHONE")).toBeInTheDocument();
    expect(types.getByText("400")).toBeInTheDocument();

    const callers = within(screen.getByRole("region", { name: "Busiest callers" }));
    expect(callers.getByText("ehr-bridge")).toBeInTheDocument();
    expect(callers.getByText("Caller 0192a000 (no longer exists)")).toBeInTheDocument();
  });

  it("charts requests first and switches the chart when another thing is chosen", async () => {
    const fetchMock = fakeServer();
    renderPage(<DashboardPage />, options);
    await screen.findByText("1,234");
    await waitFor(() => expect(callsTo(fetchMock, "metric=requests").length).toBeGreaterThan(0));

    await userEvent.click(screen.getByRole("button", { name: "Slow calls (p95)" }));

    await waitFor(() => expect(callsTo(fetchMock, "metric=latency").length).toBeGreaterThan(0));
    expect(screen.getByRole("button", { name: "Slow calls (p95)" })).toHaveAttribute("aria-pressed", "true");
  });

  it("offers the numbers behind the chart as a table", async () => {
    fakeServer();
    renderPage(<DashboardPage />, options);

    expect(await screen.findByText("Show as a table")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /Requests: 2 points/ })).toBeInTheDocument();
  });

  it("asks for another range when one is chosen", async () => {
    const fetchMock = fakeServer();
    renderPage(<DashboardPage />, options);
    await screen.findByText("1,234");

    await userEvent.click(screen.getByRole("button", { name: "Last 7 days" }));

    await waitFor(() => expect(callsTo(fetchMock, "summary?range=7d").length).toBe(1));
    expect(await screen.findByText("Days are UTC days.")).toBeInTheDocument();
  });

  it("shows the error and a retry when the server fails", async () => {
    stubFetch(() => json({ code: "server.error", title: "Something went wrong." }, 500));
    renderPage(<DashboardPage />, options);

    expect((await screen.findAllByRole("alert")).length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  });
});
