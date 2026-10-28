import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { fakeToken } from "../test/fakeToken";
import { resetSessionForTests, setAccessToken } from "../lib/session";
import PoliciesPage from "./PoliciesPage";

const page = (items: unknown[]) => ({
  items,
  page: 1,
  pageSize: 25,
  total: items.length,
});

const row = (over: Record<string, unknown> = {}) => ({
  id: "p1",
  name: "Strict",
  status: "Published",
  currentVersion: 2,
  hasDraft: false,
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

function stub(body: unknown) {
  const fetchMock = vi.fn(async (url: string) => {
    void url;
    return new Response(JSON.stringify(body), { status: 200 });
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderPage(role: string) {
  setAccessToken(fakeToken({ role }));
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/policies"]}>
        <PoliciesPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("PoliciesPage", () => {
  it("lists policies with readable statuses and links to each", async () => {
    stub(
      page([
        row(),
        row({
          id: "p2",
          name: "Draft one",
          status: "PendingApproval",
          currentVersion: null,
          hasDraft: true,
        }),
      ]),
    );
    renderPage("Developer");

    const table = await screen.findByRole("table");
    expect(within(table).getByRole("link", { name: "Strict" })).toHaveAttribute(
      "href",
      "/policies/p1",
    );
    expect(within(table).getByText("Pending approval")).toBeInTheDocument();
    expect(within(table).getByText("has a draft")).toBeInTheDocument();
  });

  it("offers New policy only to people who may write policies", async () => {
    stub(page([row()]));
    const { unmount } = renderPage("Developer");
    await screen.findByRole("table");
    expect(
      screen.queryByRole("link", { name: "New policy" }),
    ).not.toBeInTheDocument();
    unmount();

    renderPage("SecurityOfficer");
    expect(
      await screen.findByRole("link", { name: "New policy" }),
    ).toBeInTheDocument();
  });

  it("asks the server for the chosen status", async () => {
    const fetchMock = stub(page([row()]));
    const user = userEvent.setup();
    renderPage("Auditor");
    await screen.findByRole("table");

    await user.selectOptions(screen.getByLabelText("Status"), "Published");

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(([url]) => url.includes("status=Published")),
      ).toBe(true),
    );
  });

  it("says so when there are no policies", async () => {
    stub(page([]));
    renderPage("Developer");
    expect(await screen.findByText("No policies yet.")).toBeInTheDocument();
  });
});
