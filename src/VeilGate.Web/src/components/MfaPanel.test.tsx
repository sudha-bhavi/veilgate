import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { clearToastsForTests } from "../lib/toast";
import { resetSessionForTests } from "../lib/session";
import SecurityPage from "../pages/SecurityPage";
import { renderPage } from "../test/renderPage";
import { callsTo, json, stubFetch } from "../test/stubFetch";
import { fakeToken } from "../test/fakeToken";

const status = (over: Record<string, unknown> = {}) => ({
  authenticator: { addedAt: "2026-10-02T09:00:00Z" },
  recoveryCodesLeft: 7,
  recoveryCodesTotal: 10,
  ...over,
});

const NEW_CODES = Array.from({ length: 10 }, (_, i) => `NEW${i}X-ABCDE`);

/** A fake server: the sessions list (the first tab), the MFA status, and a quiet success for regenerating. */
function fakeServer(current = status()) {
  let state = current;
  return stubFetch((url, init) => {
    if (url.endsWith("/recovery-codes/regenerate") && init?.method === "POST") {
      state = status({ recoveryCodesLeft: 10 });
      return json({ recoveryCodes: NEW_CODES });
    }
    if (url.endsWith("/mfa/factors")) return json(state);
    return json([]);
  });
}

async function openMfaTab(role = "Developer") {
  renderPage(<SecurityPage />, { path: "/security", role });
  await userEvent.click(await screen.findByRole("tab", { name: "MFA" }));
}

afterEach(() => {
  resetSessionForTests();
  clearToastsForTests();
  vi.unstubAllGlobals();
});

describe("MFA tab", () => {
  it("shows the authenticator, that passkeys are not here yet, and how many recovery codes are left", async () => {
    fakeServer();
    await openMfaTab();

    expect(await screen.findByText("7 of 10 left")).toBeInTheDocument();
    expect(screen.getByText(/^Added /)).toBeInTheDocument();
    expect(screen.getByText("Not available yet.")).toBeInTheDocument();
    expect(screen.queryByText(/Running low/)).not.toBeInTheDocument();
  });

  it("works for every role", async () => {
    for (const role of ["TenantAdmin", "SecurityOfficer", "Developer", "Auditor"]) {
      fakeServer();
      const { unmount } = renderPage(<SecurityPage />, { path: "/security", role });
      await userEvent.click(await screen.findByRole("tab", { name: "MFA" }));
      expect(await screen.findByText("7 of 10 left")).toBeInTheDocument();
      unmount();
      resetSessionForTests();
    }
  });

  it("warns when only a few codes are left", async () => {
    fakeServer(status({ recoveryCodesLeft: 2 }));
    await openMfaTab();

    expect(await screen.findByText("2 of 10 left")).toBeInTheDocument();
    expect(screen.getByText(/Running low/)).toBeInTheDocument();
  });

  it("asks before replacing the codes, and changes nothing if cancelled", async () => {
    const fetchMock = fakeServer();
    await openMfaTab();

    await userEvent.click(await screen.findByRole("button", { name: "Regenerate codes" }));
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent(/old code stops working/);
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(callsTo(fetchMock, "/recovery-codes/regenerate", "POST")).toHaveLength(0);
  });

  it("shows the ten new codes once, and only lets you finish after you say they are saved", async () => {
    const fetchMock = fakeServer();
    await openMfaTab();

    await userEvent.click(await screen.findByRole("button", { name: "Regenerate codes" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Replace codes" }));

    const region = await screen.findByRole("region", { name: "New recovery codes" });
    expect(callsTo(fetchMock, "/recovery-codes/regenerate", "POST")).toHaveLength(1);
    expect(within(region).getAllByRole("listitem")).toHaveLength(10);
    expect(within(region).getByText("NEW0X-ABCDE")).toBeInTheDocument();
    expect(await screen.findByText("10 of 10 left")).toBeInTheDocument();

    const done = within(region).getByRole("button", { name: "Done" });
    expect(done).toBeDisabled();
    await userEvent.click(within(region).getByLabelText("I have saved these codes"));
    await userEvent.click(done);

    expect(screen.queryByRole("region", { name: "New recovery codes" })).not.toBeInTheDocument();
    expect(screen.queryByText("NEW0X-ABCDE")).not.toBeInTheDocument();
  });

  it("asks the person to confirm their identity first when the server wants a fresh check", async () => {
    stubFetch((url, init) => {
      if (url.endsWith("/recovery-codes/regenerate") && init?.method === "POST")
        return json({ status: 401, title: "Confirm", code: "auth.insufficient_user_authentication" }, 401);
      if (url.endsWith("/auth/refresh")) return json({ accessToken: fakeToken() });
      if (url.endsWith("/mfa/factors")) return json(status());
      return json([]);
    });
    await openMfaTab();

    await userEvent.click(await screen.findByRole("button", { name: "Regenerate codes" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Replace codes" }));

    expect(await screen.findByRole("heading", { name: "Confirm your identity" })).toBeInTheDocument();
  });

  it("says why when the server refuses, for example in the demo", async () => {
    stubFetch((url, init) => {
      if (init?.method === "POST") return json({ status: 403, title: "The demo is read-only.", code: "demo.read_only" }, 403);
      if (url.endsWith("/mfa/factors")) return json(status());
      return json([]);
    });
    await openMfaTab();

    await userEvent.click(await screen.findByRole("button", { name: "Regenerate codes" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Replace codes" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/demo is read-only/i);
  });

  it("offers a retry when the status cannot be loaded", async () => {
    stubFetch((url) =>
      url.endsWith("/mfa/factors") ? json({ status: 500, title: "Boom", code: "server.error" }, 500) : json([]),
    );
    await openMfaTab();

    expect(await screen.findByRole("button", { name: "Retry" })).toBeInTheDocument();
  });

  it("does not offer to regenerate when there is no authenticator", async () => {
    fakeServer(status({ authenticator: null, recoveryCodesLeft: 0, recoveryCodesTotal: 0 }));
    await openMfaTab();

    expect(await screen.findByText("Not set up")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Regenerate codes" })).toBeDisabled());
  });
});
