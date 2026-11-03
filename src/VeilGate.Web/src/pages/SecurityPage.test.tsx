import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { clearToastsForTests } from "../lib/toast";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, noContent, stubFetch } from "../test/stubFetch";
import SecurityPage from "./SecurityPage";

const CHROME_MAC =
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
const SAFARI_IPHONE =
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

const NOW = new Date().toISOString();

const row = (over: Record<string, unknown> = {}) => ({
  id: "s1",
  userAgent: CHROME_MAC,
  ip: "203.0.113.7",
  createdAt: NOW,
  lastActiveAt: NOW,
  current: true,
  ...over,
});

const phone = () =>
  row({ id: "s2", userAgent: SAFARI_IPHONE, ip: "198.51.100.4", current: false, lastActiveAt: new Date(Date.now() - 3 * 3_600_000).toISOString() });

/** A fake server: the list, and a quiet success for the two changes. */
function fakeServer(items: unknown[] = [row(), phone()]) {
  return stubFetch((url, init) => {
    if (init?.method === "DELETE") return noContent();
    if (init?.method === "POST" && url.endsWith("/logout-all")) return json({ ended: 1 });
    return json(items);
  });
}

afterEach(() => {
  resetSessionForTests();
  clearToastsForTests();
  vi.unstubAllGlobals();
});

describe("SecurityPage", () => {
  it("lists the sessions with the current one marked and no way to revoke it", async () => {
    fakeServer();
    renderPage(<SecurityPage />, { path: "/security" });

    const table = await screen.findByRole("table");
    expect(within(table).getByText("Chrome on macOS")).toBeInTheDocument();
    expect(within(table).getByText("(current)")).toBeInTheDocument();
    expect(within(table).getByText("Safari on iPhone")).toBeInTheDocument();
    expect(within(table).getByText("198.51.100.4")).toBeInTheDocument();
    expect(within(table).getByText("3 h ago")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Revoke Chrome on macOS" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Revoke Safari on iPhone" })).toBeInTheDocument();
  });

  it("works for every tenant role", async () => {
    for (const role of ["TenantAdmin", "SecurityOfficer", "Developer", "Auditor"]) {
      fakeServer();
      const { unmount } = renderPage(<SecurityPage />, { path: "/security", role });
      expect(await screen.findByRole("table")).toBeInTheDocument();
      unmount();
      resetSessionForTests();
    }
  });

  it("asks before ending a session, then ends exactly that one", async () => {
    const fetchMock = fakeServer();
    renderPage(<SecurityPage />, { path: "/security" });

    await userEvent.click(await screen.findByRole("button", { name: "Revoke Safari on iPhone" }));
    const dialog = screen.getByRole("dialog");
    expect(callsTo(fetchMock, "/sessions/s2", "DELETE")).toHaveLength(0);   // nothing yet: it only asked
    await userEvent.click(within(dialog).getByRole("button", { name: "End session" }));

    await waitFor(() => expect(callsTo(fetchMock, "/sessions/s2", "DELETE")).toHaveLength(1));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("changes nothing when the confirmation is cancelled", async () => {
    const fetchMock = fakeServer();
    renderPage(<SecurityPage />, { path: "/security" });

    await userEvent.click(await screen.findByRole("button", { name: "Revoke Safari on iPhone" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(callsTo(fetchMock, "/sessions/s2", "DELETE")).toHaveLength(0);
  });

  it("signs out all other sessions and keeps this one", async () => {
    const fetchMock = fakeServer();
    renderPage(<SecurityPage />, { path: "/security" });

    await userEvent.click(await screen.findByRole("button", { name: "Sign out all other sessions" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Sign out other sessions" }));

    await waitFor(() => expect(callsTo(fetchMock, "/auth/logout-all", "POST")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/auth/logout-all", "POST")[0][1])).toEqual({ keepCurrent: true });
  });

  it("has nothing to sign out when this is the only session", async () => {
    fakeServer([row()]);
    renderPage(<SecurityPage />, { path: "/security" });

    await screen.findByRole("table");
    expect(screen.getByRole("button", { name: "Sign out all other sessions" })).toBeDisabled();
  });

  it("says so in the dialog when the server refuses, for example in the demo", async () => {
    stubFetch((_url, init) =>
      init?.method === "DELETE"
        ? json({ status: 403, title: "The demo is read-only.", code: "demo.read_only" }, 403)
        : json([row(), phone()]),
    );
    renderPage(<SecurityPage />, { path: "/security" });

    await userEvent.click(await screen.findByRole("button", { name: "Revoke Safari on iPhone" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "End session" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/demo is read-only/i);
  });

  it("offers a retry when the list cannot be loaded", async () => {
    stubFetch(() => json({ status: 500, title: "Boom", code: "server.error" }, 500));
    renderPage(<SecurityPage />, { path: "/security" });

    expect(await screen.findByRole("button", { name: "Retry" })).toBeInTheDocument();
  });
});
