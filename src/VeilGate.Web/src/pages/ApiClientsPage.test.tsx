import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import ApiClientsPage from "./ApiClientsPage";

const page = (items: unknown[]) => ({
  items,
  page: 1,
  pageSize: 25,
  total: items.length,
});

const client = (over: Record<string, unknown> = {}) => ({
  id: "c1",
  clientId: "vg_abc",
  name: "Billing app",
  scopes: ["gateway:invoke", "gateway:detect"],
  status: "Active",
  defaultPolicyId: null,
  ipAllowlist: [],
  rateLimitPerMinute: 600,
  ownerUserId: "u1",
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("ApiClientsPage", () => {
  it("lists clients with a link to each", async () => {
    stubFetch(() => json(page([client(), client({ id: "c2", clientId: "vg_old", name: "Old app", status: "Revoked" })])));
    renderPage(<ApiClientsPage />, { path: "/clients" });

    const table = await screen.findByRole("table");
    expect(within(table).getByRole("link", { name: "Billing app" })).toHaveAttribute(
      "href",
      "/clients/c1",
    );
    expect(within(table).getByText("vg_abc")).toBeInTheDocument();
    expect(within(table).getByText("Revoked")).toBeInTheDocument();
  });

  it("offers a way to create a client", async () => {
    stubFetch(() => json(page([client()])));
    renderPage(<ApiClientsPage />, { path: "/clients" });

    expect(await screen.findByRole("link", { name: "New API client" })).toHaveAttribute(
      "href",
      "/clients/new",
    );
  });

  it("asks the server for the chosen status", async () => {
    const fetchMock = stubFetch(() => json(page([client()])));
    const user = userEvent.setup();
    renderPage(<ApiClientsPage />, { path: "/clients" });
    await screen.findByRole("table");

    await user.selectOptions(screen.getByLabelText("Status"), "Revoked");

    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => url.includes("status=Revoked"))).toBe(true),
    );
  });

  it("says so when there are none", async () => {
    stubFetch(() => json(page([])));
    renderPage(<ApiClientsPage />, { path: "/clients" });

    expect(await screen.findByText("No API clients yet.")).toBeInTheDocument();
  });
});
