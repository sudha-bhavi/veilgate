import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { clearToastsForTests } from "../lib/toast";
import { resetSessionForTests } from "../lib/session";
import SecurityPage from "../pages/SecurityPage";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, noContent, stubFetch } from "../test/stubFetch";

const CURRENT = "my current passphrase";
const FRESH = "a brand new passphrase";

/** A fake server: an empty sessions list for the first tab, and whatever the test wants for the password change. */
function fakeServer(change: () => Response = noContent) {
  return stubFetch((url, init) =>
    url.endsWith("/password/change") && init?.method === "POST" ? change() : json([]),
  );
}

async function openPasswordTab(role = "Developer") {
  renderPage(<SecurityPage />, { path: "/security", role });
  await userEvent.click(await screen.findByRole("tab", { name: "Password" }));
}

async function fill(current: string, fresh: string, confirm = fresh) {
  await userEvent.type(await screen.findByLabelText("Current password"), current);
  await userEvent.type(screen.getByLabelText("New password"), fresh);
  await userEvent.type(screen.getByLabelText("Confirm new password"), confirm);
}

const submit = () => userEvent.click(screen.getByRole("button", { name: "Change password" }));

afterEach(() => {
  resetSessionForTests();
  clearToastsForTests();
  vi.unstubAllGlobals();
});

describe("Password tab", () => {
  it("sends the current and new password, then clears the form", async () => {
    const fetchMock = fakeServer();
    await openPasswordTab();

    await fill(CURRENT, FRESH);
    await submit();

    await waitFor(() => expect(callsTo(fetchMock, "/password/change", "POST")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/password/change", "POST")[0][1])).toEqual({ currentPassword: CURRENT, newPassword: FRESH });
    await waitFor(() => expect(screen.getByLabelText("Current password")).toHaveValue(""));
    expect(screen.getByLabelText("New password")).toHaveValue("");
  });

  it("works for every role", async () => {
    for (const role of ["TenantAdmin", "SecurityOfficer", "Developer", "Auditor"]) {
      fakeServer();
      const { unmount } = renderPage(<SecurityPage />, { path: "/security", role });
      await userEvent.click(await screen.findByRole("tab", { name: "Password" }));
      expect(await screen.findByLabelText("Current password")).toBeInTheDocument();
      unmount();
      resetSessionForTests();
    }
  });

  it("does not send anything when the new password is too short", async () => {
    const fetchMock = fakeServer();
    await openPasswordTab();

    await fill(CURRENT, "short");
    await submit();

    expect(await screen.findByText("Use at least 12 characters")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/password/change", "POST")).toHaveLength(0);
  });

  it("does not send anything when the two new passwords differ", async () => {
    const fetchMock = fakeServer();
    await openPasswordTab();

    await fill(CURRENT, FRESH, "something else entirely");
    await submit();

    expect(await screen.findByText("The passwords do not match")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/password/change", "POST")).toHaveLength(0);
  });

  it("does not send anything when the new password is the current one", async () => {
    const fetchMock = fakeServer();
    await openPasswordTab();

    await fill(CURRENT, CURRENT);
    await submit();

    expect(await screen.findByText("Choose a password you are not using now")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/password/change", "POST")).toHaveLength(0);
  });

  it("shows a wrong current password on its own field", async () => {
    fakeServer(() =>
      json({ status: 400, title: "Your current password is incorrect.", code: "password.current_incorrect" }, 400),
    );
    await openPasswordTab();

    await fill("not my password", FRESH);
    await submit();

    expect(await screen.findByText("Your current password is incorrect.")).toBeInTheDocument();
    expect(screen.getByLabelText("Current password")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("New password")).toHaveValue(FRESH);
  });

  it("shows the server's objection to the new password on that field", async () => {
    fakeServer(() => json({ status: 400, title: "That password is too common. Choose something less guessable.", code: "password.weak" }, 400));
    await openPasswordTab();

    await fill(CURRENT, FRESH);
    await submit();

    expect(await screen.findByText(/too common/)).toBeInTheDocument();
    expect(screen.getByLabelText("New password")).toHaveAttribute("aria-invalid", "true");
  });

  it("says why in the demo, where nothing can be changed", async () => {
    fakeServer(() => json({ status: 403, title: "The demo is read-only.", code: "demo.read_only" }, 403));
    await openPasswordTab();

    await fill(CURRENT, FRESH);
    await submit();

    expect(await screen.findByRole("alert")).toHaveTextContent(/demo is read-only/i);
  });

  it("shows how strong the new password looks as it is typed", async () => {
    fakeServer();
    await openPasswordTab();

    await userEvent.type(await screen.findByLabelText("New password"), "abc");
    expect(screen.getByText("Strength: Too short")).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText("New password"), "defghijkl");
    expect(screen.getByText("Strength: Fair")).toBeInTheDocument();
  });
});
