import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, noContent, stubFetch } from "../test/stubFetch";
import ApiClientDetailPage from "./ApiClientDetailPage";

const client = (over: Record<string, unknown> = {}) => ({
  id: "c1",
  clientId: "vg_abc",
  name: "Billing app",
  scopes: ["gateway:invoke"],
  status: "Active",
  defaultPolicyId: null,
  ipAllowlist: [],
  rateLimitPerMinute: 600,
  ownerUserId: "u1",
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

const noPolicies = { items: [], page: 1, pageSize: 50, total: 0 };

function fakeServer(current = client()) {
  return stubFetch((url, init) => {
    if (url.startsWith("/api/v1/policies")) return json(noPolicies);
    if (url.endsWith("/rotate-secret")) {
      return json({ clientSecret: "brand-new-secret", previousSecretsEndAt: "2026-02-01T00:00:00Z" });
    }
    if (init?.method === "PATCH") return json(current);
    if (init?.method === "DELETE") return noContent();
    return json(current);
  });
}

const page = { path: "/clients/c1", route: "/clients/:id" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("ApiClientDetailPage", () => {
  it("shows the client and keeps Save off until something changes", async () => {
    fakeServer();
    renderPage(<ApiClientDetailPage />, page);

    expect(await screen.findByRole("heading", { name: "Billing app" })).toBeInTheDocument();
    expect(screen.getByText("vg_abc")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save changes" })).toBeDisabled();
  });

  it("sends only the field that changed", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<ApiClientDetailPage />, page);

    const rate = await screen.findByLabelText("Requests per minute (optional)");
    await user.clear(rate);
    await user.type(rate, "1200");
    await user.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/api-clients/c1", "PATCH")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/api/v1/api-clients/c1", "PATCH")[0][1])).toEqual({
      rateLimitPerMinute: 1200,
    });
  });

  it("revokes after confirmation and goes back to the list", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<ApiClientDetailPage />, page);

    await user.click(await screen.findByRole("button", { name: "Revoke client" }));
    await user.click(screen.getByRole("button", { name: "Revoke" }));

    expect(await screen.findByText("elsewhere")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/api-clients/c1", "DELETE")).toHaveLength(1);
  });

  it("shows the new secret once after rotating", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<ApiClientDetailPage />, page);

    await user.click(await screen.findByRole("button", { name: "Rotate secret" }));
    await user.click(screen.getByRole("button", { name: "Rotate now" }));

    expect(await screen.findByText("brand-new-secret")).toBeInTheDocument();
    expect(bodyOf(callsTo(fetchMock, "/rotate-secret", "POST")[0][1])).toEqual({ graceHours: 24 });
  });

  it("has no settings, rotation or revoke for a revoked client", async () => {
    fakeServer(client({ status: "Revoked" }));
    renderPage(<ApiClientDetailPage />, page);

    expect(await screen.findByText(/This client was revoked/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Rotate secret" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Revoke client" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save changes" })).not.toBeInTheDocument();
  });
});
