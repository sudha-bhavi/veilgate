import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fakeToken } from "../test/fakeToken";
import { resetSessionForTests, setAccessToken } from "../lib/session";
import type { PolicyDetail } from "../lib/policiesApi";
import PolicyWorkflowBar from "./PolicyWorkflowBar";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status });

const policy = (
  draftState: string,
  over: Partial<PolicyDetail> = {},
): PolicyDetail => ({
  id: "p1",
  name: "Strict",
  description: null,
  status: draftState === "PendingApproval" ? "PendingApproval" : "Draft",
  current: null,
  draft: {
    id: "v1",
    versionNo: 3,
    state: draftState,
    yaml: "",
    summary: "Tighten phones",
    publishedAt: null,
  },
  etag: '"a"',
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

function renderBar(detail: PolicyDetail, role: string, dirty = false) {
  setAccessToken(fakeToken({ role }));
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <PolicyWorkflowBar policy={detail} role={role} dirty={dirty} />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("PolicyWorkflowBar", () => {
  it("submits a draft with a written summary", async () => {
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      void url;
      void init;
      return json({ status: "PendingApproval" }, 202);
    });
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    renderBar(policy("Draft"), "TenantAdmin");

    await user.click(
      screen.getByRole("button", { name: "Submit for approval" }),
    );
    expect(screen.getByRole("button", { name: "Submit" })).toBeDisabled();

    await user.type(
      screen.getByLabelText(/What changed/),
      "Tighten the phone rules",
    );
    await user.click(screen.getByRole("button", { name: "Submit" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/policies/p1/submit");
    expect(JSON.parse(String(init?.body))).toEqual({
      summary: "Tighten the phone rules",
    });
  });

  it("will not submit while there are unsaved edits", () => {
    renderBar(policy("Draft"), "TenantAdmin", true);
    expect(
      screen.getByRole("button", { name: "Submit for approval" }),
    ).toBeDisabled();
    expect(
      screen.getByText("Save your draft before submitting it."),
    ).toBeInTheDocument();
  });

  it("asks the reviewer to confirm their identity, then publishes", async () => {
    let steppedUp = false;
    let publishCalls = 0;
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      void init;
      if (url.endsWith("/auth/refresh")) {
        return json({ accessToken: fakeToken({ role: "SecurityOfficer" }) });
      }
      if (url.endsWith("/auth/step-up")) {
        steppedUp = true;
        return json({
          accessToken: fakeToken({
            role: "SecurityOfficer",
            auth_time: 1_900_000_000,
          }),
        });
      }
      publishCalls++;
      return steppedUp
        ? json({ versionNumber: 3 })
        : json(
            {
              title: "Confirm your identity again to continue.",
              code: "auth.insufficient_user_authentication",
            },
            401,
          );
    });
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    renderBar(policy("PendingApproval"), "SecurityOfficer");

    await user.click(screen.getByRole("button", { name: "Publish" }));
    await user.click(screen.getByRole("button", { name: "Publish now" }));

    await user.type(await screen.findByLabelText("Code"), "123456");
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    await waitFor(() => expect(steppedUp).toBe(true));
    await waitFor(() => expect(publishCalls).toBeGreaterThan(2)); // refused, refused after a refresh, then accepted
  });

  it("tells an author that a submitted draft is waiting", () => {
    renderBar(policy("PendingApproval"), "TenantAdmin");
    expect(
      screen.getByText(/waiting for approval by a Security Officer/),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Publish" }),
    ).not.toBeInTheDocument();
  });

  it("shows nothing to a role that can only read", () => {
    renderBar(policy("Draft"), "Developer");
    expect(
      screen.queryByRole("region", { name: "Approval workflow" }),
    ).not.toBeInTheDocument();
  });
});
