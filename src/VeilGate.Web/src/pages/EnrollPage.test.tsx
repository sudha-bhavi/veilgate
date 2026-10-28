import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { fakeToken } from "../test/fakeToken";
import Where from "../test/Where";
import { clearMfaFlow, getMfaFlow, startMfaFlow } from "../lib/mfaFlow";
import EnrollPage from "./EnrollPage";

const RECOVERY_CODES = Array.from({ length: 10 }, (_, i) => `CODE${i}-ABCDE`);

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status });

/** A fake server for the two setup calls. A new Response each time, because a body can be read only once. */
function server(
  options: { start?: () => Response; confirm?: () => Response } = {},
) {
  const start =
    options.start ??
    (() =>
      json(200, {
        otpAuthUri:
          "otpauth://totp/VeilGate:a%40b.test?secret=ABCDEFGHIJKLMNOP&issuer=VeilGate",
        manualEntryKey: "ABCD EFGH IJKL MNOP",
      }));
  const confirm =
    options.confirm ??
    (() => json(200, { mfaConfirmed: true, recoveryCodes: RECOVERY_CODES }));
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string) => {
      if (path.endsWith("/totp/start")) return start();
      if (path.endsWith("/totp/confirm")) return confirm();
      throw new Error(`unexpected call ${path}`);
    }),
  );
}

function renderEnroll(path = "/mfa/enroll") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/mfa/enroll" element={<EnrollPage />} />
          <Route path="/login" element={<Where label="login" />} />
          <Route path="/login/mfa" element={<Where label="mfa" />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const inFiveMinutes = () =>
  fakeToken({ exp: Math.floor(Date.now() / 1000) + 300 });

beforeEach(() => {
  clearMfaFlow();
  startMfaFlow(inFiveMinutes(), "enroll");
});
afterEach(() => vi.unstubAllGlobals());

describe("EnrollPage", () => {
  it("shows the QR code and the manual setup key from the server", async () => {
    server();
    const { container } = renderEnroll();

    expect(await screen.findByText("ABCD EFGH IJKL MNOP")).toBeInTheDocument();
    expect(container.querySelector("svg")).not.toBeNull();
  });

  it("walks through scan, verify and recovery codes, then goes to the code screen with the return address kept", async () => {
    const user = userEvent.setup();
    server();
    renderEnroll("/mfa/enroll?returnTo=%2Fpolicies");

    await user.click(await screen.findByRole("button", { name: "Next" }));
    await user.keyboard("123456");

    expect(await screen.findByText("CODE0-ABCDE")).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(10);

    const finish = screen.getByRole("button", {
      name: "Finish and open VeilGate",
    });
    expect(finish).toBeDisabled();
    await user.click(screen.getByLabelText("I have saved these codes"));
    expect(finish).toBeEnabled();
    await user.click(finish);

    expect(
      await screen.findByText("mfa?returnTo=%2Fpolicies&reason=enrolled"),
    ).toBeInTheDocument();
    expect(getMfaFlow()?.purpose).toBe("verify");
  });

  it("stays on the code step and says so after a wrong code", async () => {
    const user = userEvent.setup();
    server({
      confirm: () =>
        json(401, {
          code: "mfa.code_invalid",
          title: "The code is incorrect or expired.",
        }),
    });
    renderEnroll();

    await user.click(await screen.findByRole("button", { name: "Next" }));
    await user.keyboard("000000");

    expect(
      await screen.findByText("Code is incorrect or expired"),
    ).toBeInTheDocument();
    expect(screen.queryByText("CODE0-ABCDE")).not.toBeInTheDocument();
  });

  it("goes back to sign-in with a reason when the temporary token is no longer good", async () => {
    server({
      start: () =>
        json(401, { code: "auth.token_invalid", title: "Sign in again." }),
    });
    renderEnroll();
    expect(await screen.findByText("login?reason=expired")).toBeInTheDocument();
  });

  it("goes back to sign-in when there is no sign-in in progress", () => {
    clearMfaFlow();
    server();
    renderEnroll();
    expect(screen.getByText("login")).toBeInTheDocument();
  });

  it("hands someone who already has MFA to the code screen", () => {
    startMfaFlow(inFiveMinutes(), "verify");
    server();
    renderEnroll();
    expect(screen.getByText("mfa")).toBeInTheDocument();
  });
});
