import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { fakeToken } from "../test/fakeToken";
import { resetSessionForTests, setAccessToken } from "../lib/session";
import { clearMfaFlow, getMfaFlow } from "../lib/mfaFlow";
import LoginPage from "./LoginPage";

/** Shows where the router ended up, including the query string. */
function Where({ label }: { label: string }) {
  return <p>{`${label}${useLocation().search}`}</p>;
}

function renderLogin(path = "/login") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/login/mfa" element={<Where label="mfa" />} />
        <Route path="/mfa/enroll" element={<Where label="enroll" />} />
        <Route path="/dashboard" element={<Where label="dashboard" />} />
        <Route path="/policies" element={<Where label="policies" />} />
      </Routes>
    </MemoryRouter>,
  );
}

const reply = (
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
) =>
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(
        new Response(JSON.stringify(body), { status, headers }),
      ),
  );

async function fillAndSubmit(
  user: ReturnType<typeof userEvent.setup>,
  email = "a@b.test",
  password = "secret-password",
) {
  await user.type(screen.getByLabelText("Email"), email);
  await user.type(screen.getByLabelText("Password"), password);
  await user.click(screen.getByRole("button", { name: "Continue" }));
}

beforeEach(() => {
  resetSessionForTests();
  clearMfaFlow();
});
afterEach(() => vi.unstubAllGlobals());

describe("LoginPage", () => {
  it("keeps Continue disabled until both fields have something in them", async () => {
    const user = userEvent.setup();
    renderLogin();
    const button = screen.getByRole("button", { name: "Continue" });
    expect(button).toBeDisabled();

    await user.type(screen.getByLabelText("Email"), "a@b.test");
    expect(button).toBeDisabled();
    await user.type(screen.getByLabelText("Password"), "x");
    expect(button).toBeEnabled();
  });

  it("warns about a badly formed email when the field is left", async () => {
    const user = userEvent.setup();
    renderLogin();
    await user.type(screen.getByLabelText("Email"), "nope");
    await user.tab();
    expect(await screen.findByText("Enter a valid email")).toBeInTheDocument();
  });

  it("goes to the code step and keeps the token in memory", async () => {
    const user = userEvent.setup();
    reply(200, {
      mfaRequired: true,
      mfaEnrollmentRequired: false,
      mfaToken: "tok",
    });
    renderLogin();

    await fillAndSubmit(user);

    expect(await screen.findByText("mfa")).toBeInTheDocument();
    expect(getMfaFlow()).toMatchObject({ token: "tok", purpose: "verify" });
  });

  it("goes to enrollment when the account has no second factor yet", async () => {
    const user = userEvent.setup();
    reply(200, {
      mfaRequired: false,
      mfaEnrollmentRequired: true,
      mfaToken: "tok",
    });
    renderLogin();

    await fillAndSubmit(user);

    expect(await screen.findByText("enroll")).toBeInTheDocument();
    expect(getMfaFlow()?.purpose).toBe("enroll");
  });

  it("carries the page the person was heading to into the next step", async () => {
    const user = userEvent.setup();
    reply(200, {
      mfaRequired: true,
      mfaEnrollmentRequired: false,
      mfaToken: "tok",
    });
    renderLogin("/login?returnTo=%2Fpolicies");

    await fillAndSubmit(user);

    expect(
      await screen.findByText("mfa?returnTo=%2Fpolicies"),
    ).toBeInTheDocument();
  });

  it("shows the generic message for a wrong password and clears the password but not the email", async () => {
    const user = userEvent.setup();
    reply(401, {
      code: "auth.invalid_credentials",
      title: "Email or password is incorrect",
    });
    renderLogin();

    await fillAndSubmit(user);

    expect(
      await screen.findByText("Email or password is incorrect"),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(screen.getByLabelText("Email")).toHaveValue("a@b.test");
  });

  it("tells a locked account how long to wait", async () => {
    const user = userEvent.setup();
    reply(423, { code: "auth.account_locked", title: "locked" });
    renderLogin();
    await fillAndSubmit(user);
    expect(
      await screen.findByText(
        "Account temporarily locked. Try again in 15 minutes",
      ),
    ).toBeInTheDocument();
  });

  it("tells a rate-limited person how many seconds to wait", async () => {
    const user = userEvent.setup();
    reply(
      429,
      { code: "rate_limit.exceeded", title: "slow down" },
      { "Retry-After": "30" },
    );
    renderLogin();
    await fillAndSubmit(user);
    expect(
      await screen.findByText("Too many attempts. Wait 30 seconds"),
    ).toBeInTheDocument();
  });

  it("explains why the person is here after an expired or idle session", () => {
    renderLogin("/login?reason=expired");
    expect(screen.getByRole("status")).toHaveTextContent(
      "Your session expired",
    );
  });

  it("sends someone who is already signed in on to where they were going, but only inside the app", () => {
    setAccessToken(fakeToken());
    renderLogin("/login?returnTo=%2Fpolicies");
    expect(screen.getByText("policies")).toBeInTheDocument();
  });

  it("ignores a return address that points at another site", () => {
    setAccessToken(fakeToken());
    renderLogin("/login?returnTo=%2F%2Fevil.example");
    expect(screen.getByText("dashboard")).toBeInTheDocument();
  });
});
