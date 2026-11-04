import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import RequestDetailPage from "./RequestDetailPage";

const request = {
  id: "m1",
  requestId: "req_abc",
  occurredAt: "2026-11-03T14:46:38Z",
  actorType: "client",
  clientId: "c1",
  clientName: "ehr-bridge",
  action: "gateway.chat",
  status: "denied",
  reason: "policy.blocked",
  model: null,
  provider: null,
  policyVersion: "7",
  entityCount: 2,
  entities: { PHONE: 2 },
  latencyMs: 40,
};

const options = { path: "/requests/m1", route: "/requests/:id", role: "Auditor" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("RequestDetailPage", () => {
  it("shows the call in full, and only what is known", async () => {
    stubFetch(() => json(request));
    renderPage(<RequestDetailPage />, options);

    expect(await screen.findByRole("heading", { name: "Request req_abc" })).toBeInTheDocument();
    expect(screen.getByText("denied")).toBeInTheDocument();
    expect(screen.getByText("ehr-bridge")).toBeInTheDocument();
    expect(screen.getByText("policy.blocked")).toBeInTheDocument();
    expect(screen.getByText("2 (PHONE 2)")).toBeInTheDocument();
    expect(screen.getByText("40 ms")).toBeInTheDocument();
    expect(screen.queryByText("Model")).not.toBeInTheDocument(); // empty fields are left out, not shown blank
  });

  it("says plainly that the request was not found", async () => {
    stubFetch(() => json({ code: "request.not_found", title: "Request not found." }, 404));
    renderPage(<RequestDetailPage />, options);

    expect(await screen.findByText("This request was not found.")).toBeInTheDocument();
  });

  it("links back to the list", async () => {
    stubFetch(() => json(request));
    renderPage(<RequestDetailPage />, options);

    expect(await screen.findByRole("link", { name: "← Requests" })).toHaveAttribute("href", "/requests");
  });
});
