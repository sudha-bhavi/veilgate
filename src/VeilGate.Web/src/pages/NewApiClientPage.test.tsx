import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, stubFetch } from "../test/stubFetch";
import NewApiClientPage from "./NewApiClientPage";

const policies = {
  items: [
    { id: "p1", name: "Strict", status: "Published", currentVersion: 2, hasDraft: false, createdAt: "2026-01-01T00:00:00Z" },
  ],
  page: 1,
  pageSize: 50,
  total: 1,
};

const created = {
  id: "c9",
  clientId: "vg_new",
  clientSecret: "s3cret-value",
  name: "Billing app",
  scopes: ["gateway:invoke"],
  status: "Active",
  defaultPolicyId: null,
  ipAllowlist: [],
  rateLimitPerMinute: 600,
};

function fakeServer(createResponse: () => Response = () => json(created, 201)) {
  return stubFetch((url, init) => {
    if (url.startsWith("/api/v1/policies")) return json(policies);
    if (url === "/api/v1/api-clients" && init?.method === "POST") return createResponse();
    return json({}, 404);
  });
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("NewApiClientPage", () => {
  it("creates the client, shows the secret once, then opens the client", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<NewApiClientPage />, { path: "/clients/new" });

    await user.type(screen.getByLabelText("Name"), "Billing app");
    await user.click(screen.getByRole("button", { name: "Create client" }));

    expect(await screen.findByText("s3cret-value")).toBeInTheDocument();
    expect(screen.getByText("vg_new")).toBeInTheDocument();
    expect(bodyOf(callsTo(fetchMock, "/api/v1/api-clients", "POST")[0][1])).toEqual({
      name: "Billing app",
      scopes: ["gateway:invoke"],
    });

    await user.click(screen.getByLabelText(/I have stored the secret/));
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("elsewhere")).toBeInTheDocument();
  });

  it("will not create a client without a name", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<NewApiClientPage />, { path: "/clients/new" });

    await user.click(screen.getByRole("button", { name: "Create client" }));

    expect(await screen.findByText("Name is required.")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/api-clients", "POST")).toHaveLength(0);
  });

  it("asks for at least one permission", async () => {
    fakeServer();
    const user = userEvent.setup();
    renderPage(<NewApiClientPage />, { path: "/clients/new" });

    await user.click(screen.getByLabelText("Invoke"));

    expect(screen.getByText("Pick at least one permission.")).toBeInTheDocument();
  });

  it("shows the server's reason when it refuses", async () => {
    fakeServer(() =>
      json({ title: "A client with this name already exists.", code: "resource.conflict" }, 409),
    );
    const user = userEvent.setup();
    renderPage(<NewApiClientPage />, { path: "/clients/new" });

    await user.type(screen.getByLabelText("Name"), "Billing app");
    await user.click(screen.getByRole("button", { name: "Create client" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("A client with this name already exists.");
  });
});
