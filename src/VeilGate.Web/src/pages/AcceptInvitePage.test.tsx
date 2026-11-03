import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { clearMfaFlow, getMfaFlow } from "../lib/mfaFlow";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, stubFetch } from "../test/stubFetch";
import AcceptInvitePage from "./AcceptInvitePage";

const invitation = {
  tenantName: "Acme Health",
  email: "new@acme.test",
  role: "Developer",
  expiresAt: "2026-11-04T00:00:00Z",
};

const GOOD_PASSWORD = "correct horse battery staple";

const open = (path = "/accept-invite?token=abc") =>
  renderPage(<AcceptInvitePage />, { path, route: "/accept-invite" });

type Fields = { name?: string; password?: string; confirm?: string };

async function fill(user: ReturnType<typeof userEvent.setup>, fields: Fields = {}) {
  const name = fields.name ?? "Jordan Reyes";
  const password = fields.password ?? GOOD_PASSWORD;
  const confirm = fields.confirm ?? password; // the same as the password unless a test says otherwise
  await user.type(await screen.findByLabelText("Full name"), name);
  await user.type(screen.getByLabelText("Password"), password);
  await user.type(screen.getByLabelText("Confirm password"), confirm);
  await user.click(screen.getByRole("button", { name: "Create account" }));
}

beforeEach(() => clearMfaFlow());
afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("AcceptInvitePage", () => {
  it("shows who invited you, the email and the role", async () => {
    const fetchMock = stubFetch(() => json(invitation));
    open();

    expect(await screen.findByText("Acme Health")).toBeInTheDocument();
    expect(screen.getByText(/as Developer/)).toBeInTheDocument();
    expect(screen.getByLabelText("Email")).toHaveValue("new@acme.test");
    expect(screen.getByLabelText("Email")).toHaveAttribute("readonly");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/v1/auth/invitations?token=abc");
  });

  it("says the link is no longer valid when the server has never heard of it", async () => {
    stubFetch(() => json({ code: "invitation.invalid", title: "This link is no longer valid." }, 404));
    open();

    expect(await screen.findByRole("heading", { name: "This link is no longer valid" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Go to sign in" })).toHaveAttribute("href", "/login");
  });

  it("says the same without asking the server when the address has no token", async () => {
    const fetchMock = stubFetch(() => json(invitation));
    open("/accept-invite");

    expect(await screen.findByRole("heading", { name: "This link is no longer valid" })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("does not send anything when the two passwords differ", async () => {
    const fetchMock = stubFetch(() => json(invitation));
    const user = userEvent.setup();
    open();

    await fill(user, { confirm: "something else entirely" });

    expect(await screen.findByText("The passwords do not match")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/users/accept-invite")).toHaveLength(0);
  });

  it("does not send a password that is too short", async () => {
    const fetchMock = stubFetch(() => json(invitation));
    const user = userEvent.setup();
    open();

    await fill(user, { password: "short" });

    expect(await screen.findByText("Use at least 12 characters")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/users/accept-invite")).toHaveLength(0);
  });

  it("creates the account and carries on into two-step setup", async () => {
    const fetchMock = stubFetch((_url, init) =>
      init?.method === "POST"
        ? json({ mfaRequired: false, mfaEnrollmentRequired: true, mfaToken: "mfa-token-xyz" })
        : json(invitation),
    );
    const user = userEvent.setup();
    open();

    await fill(user);

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/users/accept-invite", "POST")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/api/v1/users/accept-invite", "POST")[0][1])).toEqual({
      token: "abc",
      fullName: "Jordan Reyes",
      password: GOOD_PASSWORD,
    });
    // The short-lived token is kept in memory for the setup page, and the person is sent there.
    expect(getMfaFlow()).toMatchObject({ token: "mfa-token-xyz", purpose: "enroll" });
    expect(await screen.findByText("elsewhere")).toBeInTheDocument();
  });

  it("shows the server's reason and stays on the page when the password is refused", async () => {
    stubFetch((_url, init) =>
      init?.method === "POST"
        ? json({ code: "password.weak", title: "That password is too common. Choose something less guessable." }, 400)
        : json(invitation),
    );
    const user = userEvent.setup();
    open();

    await fill(user);

    expect(await screen.findByRole("alert")).toHaveTextContent("That password is too common");
    expect(getMfaFlow()).toBeNull();
    expect(screen.getByRole("button", { name: "Create account" })).toBeInTheDocument();
  });
});
