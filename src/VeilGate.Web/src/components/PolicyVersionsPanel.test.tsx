import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fakeToken } from "../test/fakeToken";
import { resetSessionForTests, setAccessToken } from "../lib/session";
import PolicyVersionsPanel from "./PolicyVersionsPanel";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status });

const version = (versionNo: number, state: string) => ({
  id: `v${versionNo}`,
  versionNo,
  state,
  createdBy: "u1",
  submittedBy: "u1",
  approvedBy: "u2",
  publishedAt: "2026-01-01T00:00:00Z",
  summary: `Change ${versionNo}`,
});

function stubApi() {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    void init;
    if (url.includes("/diff")) {
      return json({
        from: 1,
        to: 2,
        unifiedDiff:
          "--- v1\n+++ v2\n@@ -1 +1 @@\n-action: REDACT\n+action: MASK",
      });
    }
    return json({
      items: [version(1, "Archived"), version(2, "Published")],
      page: 1,
      pageSize: 100,
      total: 2,
    });
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderPanel(role: string) {
  setAccessToken(fakeToken({ role }));
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <PolicyVersionsPanel policyId="p1" role={role} />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("PolicyVersionsPanel", () => {
  it("lists versions newest first", async () => {
    stubApi();
    renderPanel("Developer");

    const table = await screen.findByRole("table");
    const rows = table.querySelectorAll("tbody tr");
    expect(rows[0]).toHaveTextContent("v2");
    expect(rows[1]).toHaveTextContent("v1");
  });

  it("shows the differences between two versions", async () => {
    const fetchMock = stubApi();
    const user = userEvent.setup();
    renderPanel("Developer");

    await user.click(
      await screen.findByRole("button", { name: "Show differences" }),
    );

    expect(await screen.findByText("+action: MASK")).toBeInTheDocument();
    expect(screen.getByText("-action: REDACT")).toBeInTheDocument();
    const diffCall = fetchMock.mock.calls.find(([url]) => url.includes("/diff"));
    expect(diffCall?.[0]).toBe("/api/v1/policies/p1/diff?from=1&to=2");
  });

  it("offers a rollback to a security officer, and only for an archived version", async () => {
    stubApi();
    renderPanel("SecurityOfficer");

    expect(
      await screen.findByRole("button", { name: "Roll back to v1" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Roll back to v2" }),
    ).not.toBeInTheDocument();
  });

  it("offers no rollback to anyone else", async () => {
    stubApi();
    renderPanel("TenantAdmin");

    await screen.findByRole("table");
    expect(
      screen.queryByRole("button", { name: /Roll back/ }),
    ).not.toBeInTheDocument();
  });
});
