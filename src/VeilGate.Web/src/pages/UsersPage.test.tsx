import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { clearToastsForTests } from "../lib/toast";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, noContent, stubFetch } from "../test/stubFetch";
import UsersPage from "./UsersPage";

// The test token's "sub", so the page knows which row is the signed-in person.
const ME = "11111111-1111-1111-1111-111111111111";

const page = (items: unknown[]) => ({ items, page: 1, pageSize: 25, total: items.length });

const person = (over: Record<string, unknown> = {}) => ({
  id: "u2",
  name: "Ann Lee",
  email: "ann@acme.test",
  role: "Developer",
  status: "Active",
  hasMfa: true,
  lastLoginAt: null,
  ...over,
});

const me = () => person({ id: ME, name: "A. Rao", email: "rao@acme.test", role: "TenantAdmin" });

/** A fake server: the list, and a quiet success for every action. */
function fakeServer(items: unknown[] = [me(), person()]) {
  return stubFetch((url, init) => {
    if (init?.method === "POST" && url.endsWith("/api/v1/users")) {
      return json({ id: "u9", email: "new@acme.test", role: "Auditor", status: "Invited", inviteExpiresAt: "2026-11-04T00:00:00Z", emailSent: true }, 201);
    }
    if (init?.method === "POST" && url.endsWith("/invite/resend")) {
      return json({ emailSent: true, inviteExpiresAt: "2026-11-04T00:00:00Z" });
    }
    if (init?.method === "POST") return noContent();
    if (init?.method === "PATCH") return json(person());
    return json(page(items));
  });
}

afterEach(() => {
  resetSessionForTests();
  clearToastsForTests();
  vi.unstubAllGlobals();
});

describe("UsersPage", () => {
  it("lists people with their role, two-step state and status", async () => {
    fakeServer([me(), person(), person({ id: "u3", name: "Zed Invited", email: "zed@acme.test", status: "Invited", hasMfa: false })]);
    renderPage(<UsersPage />, { path: "/users" });

    const table = await screen.findByRole("table");
    expect(within(table).getByText("Ann Lee")).toBeInTheDocument();
    expect(within(table).getByText("ann@acme.test")).toBeInTheDocument();
    expect(within(table).getByText("Invited")).toBeInTheDocument();
    expect(within(table).getByText("Not set up")).toBeInTheDocument();
    expect(within(table).getAllByText("Never").length).toBeGreaterThan(0);
  });

  it("marks your own row and gives it no actions", async () => {
    fakeServer();
    renderPage(<UsersPage />, { path: "/users" });

    expect(await screen.findByText("(you)")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /A\. Rao/ })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Disable Ann Lee" })).toBeInTheDocument();
  });

  it("offers the actions that fit each person's status", async () => {
    fakeServer([
      me(),
      person(),
      person({ id: "u3", name: "Zed Invited", status: "Invited" }),
      person({ id: "u4", name: "Dee Gone", status: "Disabled" }),
    ]);
    renderPage(<UsersPage />, { path: "/users" });

    await screen.findByRole("table");
    expect(screen.getByRole("button", { name: "Change role Ann Lee" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reset two-step Ann Lee" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resend invite Zed Invited" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Enable Dee Gone" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Disable Dee Gone" })).not.toBeInTheDocument();
  });

  it("asks the server for the chosen role", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });
    await screen.findByRole("table");

    await user.selectOptions(screen.getByLabelText("Role"), "Auditor");

    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => url.includes("role=Auditor"))).toBe(true));
  });

  it("says so when nobody matches", async () => {
    fakeServer([]);
    renderPage(<UsersPage />, { path: "/users?q=nobody", route: "/users" });

    expect(await screen.findByText("No people match these filters.")).toBeInTheDocument();
  });

  it("invites someone with an email and a role", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Invite user" }));
    const dialog = screen.getByRole("dialog");
    await user.type(within(dialog).getByLabelText("Email"), "new@acme.test");
    await user.selectOptions(within(dialog).getByLabelText("Role"), "Auditor");
    await user.click(within(dialog).getByRole("button", { name: "Send invitation" }));

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/users", "POST")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/api/v1/users", "POST")[0][1])).toEqual({ email: "new@acme.test", role: "Auditor" });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("keeps Send invitation off until there is an email", async () => {
    fakeServer();
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Invite user" }));

    expect(screen.getByRole("button", { name: "Send invitation" })).toBeDisabled();
  });

  it("shows the reason when an invitation is refused", async () => {
    stubFetch((_url, init) =>
      init?.method === "POST"
        ? json({ code: "user.email_taken", title: "That email address already has an account." }, 409)
        : json(page([me()])),
    );
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Invite user" }));
    await user.type(within(screen.getByRole("dialog")).getByLabelText("Email"), "taken@acme.test");
    await user.click(screen.getByRole("button", { name: "Send invitation" }));

    expect(await screen.findByText("That email address already has an account.")).toBeInTheDocument();
  });

  it("changes a role only after a different one is chosen", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Change role Ann Lee" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByRole("button", { name: "Change role" })).toBeDisabled();

    await user.selectOptions(within(dialog).getByLabelText("Role"), "Auditor");
    await user.click(within(dialog).getByRole("button", { name: "Change role" }));

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/users/u2", "PATCH")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/api/v1/users/u2", "PATCH")[0][1])).toEqual({ role: "Auditor" });
  });

  it("disables only after confirmation", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Disable Ann Lee" }));
    expect(callsTo(fetchMock, "/api/v1/users/u2/disable")).toHaveLength(0);

    await user.click(screen.getByRole("button", { name: "Disable account" }));

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/users/u2/disable", "POST")).toHaveLength(1));
  });

  it("does nothing when the confirmation is cancelled", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Disable Ann Lee" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/users/u2/disable")).toHaveLength(0);
  });

  it("enables a disabled person", async () => {
    const fetchMock = fakeServer([me(), person({ status: "Disabled" })]);
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Enable Ann Lee" }));
    await user.click(screen.getByRole("button", { name: "Enable account" }));

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/users/u2/enable", "POST")).toHaveLength(1));
  });

  it("resets two-step sign-in after confirmation", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Reset two-step Ann Lee" }));
    await user.click(screen.getByRole("button", { name: "Reset two-step sign-in" }));

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/users/u2/mfa/reset", "POST")).toHaveLength(1));
  });

  it("resends an invitation", async () => {
    const fetchMock = fakeServer([me(), person({ status: "Invited", hasMfa: false })]);
    const user = userEvent.setup();
    renderPage(<UsersPage />, { path: "/users" });

    await user.click(await screen.findByRole("button", { name: "Resend invite Ann Lee" }));
    await user.click(screen.getByRole("button", { name: "Send new invitation" }));

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/users/u2/invite/resend", "POST")).toHaveLength(1));
  });
});
