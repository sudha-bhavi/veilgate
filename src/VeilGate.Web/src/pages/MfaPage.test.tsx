import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { fakeToken } from "../test/fakeToken";
import Where from "../test/Where";
import { clearMfaFlow, getMfaFlow, startMfaFlow } from "../lib/mfaFlow";
import { getSession, resetSessionForTests } from "../lib/session";
import MfaPage from "./MfaPage";

const inFiveMinutes = () =>
  fakeToken({ exp: Math.floor(Date.now() / 1000) + 300 });

function renderMfa(path = "/login/mfa") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/login/mfa" element={<MfaPage />} />
        <Route path="/login" element={<Where label="login" />} />
        <Route path="/mfa/enroll" element={<Where label="enroll" />} />
        <Route path="/policies" element={<Where label="policies" />} />
        <Route path="/dashboard" element={<Where label="dashboard" />} />
      </Routes>
    </MemoryRouter>,
  );
}

const reply = (status: number, body: unknown) => {
  const fetchMock = vi
    .fn()
    .mockResolvedValue(new Response(JSON.stringify(body), { status }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
};

const signedInReply = () => ({
  accessToken: fakeToken(),
  tokenType: "Bearer",
  expiresIn: 600,
});

beforeEach(() => {
  resetSessionForTests();
  clearMfaFlow();
});
afterEach(() => vi.unstubAllGlobals());

describe("MfaPage", () => {
  it("goes back to sign-in when there is no sign-in in progress", () => {
    renderMfa();
    expect(screen.getByText("login")).toBeInTheDocument();
  });

  it("hands someone who still has to set up MFA to the setup wizard", () => {
    startMfaFlow(inFiveMinutes(), "enroll");
    renderMfa();
    expect(screen.getByText("enroll")).toBeInTheDocument();
  });

  it("signs in as soon as the sixth digit is typed, using the temporary token, and goes where the person was heading", async () => {
    const user = userEvent.setup();
    const token = inFiveMinutes();
    startMfaFlow(token, "verify");
    const fetchMock = reply(200, signedInReply());
    renderMfa("/login/mfa?returnTo=%2Fpolicies");

    await user.keyboard("123456");

    expect(await screen.findByText("policies")).toBeInTheDocument();
    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/api/v1/auth/mfa/verify");
    expect(init.body).toBe('{"code":"123456"}');
    expect((init.headers as Record<string, string>).Authorization).toBe(
      `Bearer ${token}`,
    );
    expect(getSession().status).toBe("signedIn");
    expect(getMfaFlow()).toBeNull();
  });

  it("empties the boxes and says so after a wrong code, keeping the sign-in alive", async () => {
    const user = userEvent.setup();
    startMfaFlow(inFiveMinutes(), "verify");
    reply(401, {
      code: "mfa.code_invalid",
      title: "The code is incorrect or expired.",
    });
    renderMfa();

    await user.keyboard("000000");

    expect(
      await screen.findByText("Code is incorrect or expired"),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Verification code digit 1")).toHaveValue("");
    expect(getMfaFlow()).not.toBeNull();
    expect(getSession().status).toBe("loading");
  });

  it("returns to sign-in with a reason when the temporary token has expired", async () => {
    const user = userEvent.setup();
    startMfaFlow(inFiveMinutes(), "verify");
    reply(401, { code: "auth.token_invalid", title: "Sign in again." });
    renderMfa();

    await user.keyboard("123456");

    expect(await screen.findByText("login?reason=expired")).toBeInTheDocument();
    expect(getMfaFlow()).toBeNull();
  });

  it("returns to sign-in with a lock message when the account got locked", async () => {
    const user = userEvent.setup();
    startMfaFlow(inFiveMinutes(), "verify");
    reply(423, { code: "auth.account_locked", title: "locked" });
    renderMfa();

    await user.keyboard("123456");

    expect(await screen.findByText("login?reason=locked")).toBeInTheDocument();
  });

  it("accepts a recovery code on the recovery tab", async () => {
    const user = userEvent.setup();
    startMfaFlow(inFiveMinutes(), "verify");
    const fetchMock = reply(200, signedInReply());
    renderMfa();

    await user.click(screen.getByRole("tab", { name: "Recovery" }));
    await user.type(screen.getByLabelText("Recovery code"), "ABCDE-12345");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(await screen.findByText("dashboard")).toBeInTheDocument();
    expect((fetchMock.mock.calls[0] as [string, RequestInit])[1].body).toBe(
      '{"code":"ABCDE-12345"}',
    );
  });

  it("shows a disabled passkey tab, since passkeys are not available yet", () => {
    startMfaFlow(inFiveMinutes(), "verify");
    renderMfa();
    expect(screen.getByRole("tab", { name: "Passkey" })).toBeDisabled();
  });

  it("says the session expired once the countdown is over and lets the person start again", async () => {
    const user = userEvent.setup();
    startMfaFlow(fakeToken({ exp: 1 }), "verify"); // long past
    renderMfa();

    expect(screen.getByText("Session expired")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Start over" }));

    expect(screen.getByText("login")).toBeInTheDocument();
    expect(getMfaFlow()).toBeNull();
  });

  it("explains what to do after MFA has just been set up", () => {
    startMfaFlow(inFiveMinutes(), "verify");
    renderMfa("/login/mfa?reason=enrolled");
    expect(screen.getByRole("status")).toHaveTextContent(
      "Enter the next code from your app",
    );
  });
});
