import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fakeToken } from "../test/fakeToken";
import {
  getSession,
  resetSessionForTests,
  setAccessToken,
} from "../lib/session";
import StepUpDialog from "./StepUpDialog";

function stub(response: Response) {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    void url;
    void init;
    return response;
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderDialog() {
  const onConfirmed = vi.fn();
  const onCancel = vi.fn();
  const client = new QueryClient({
    defaultOptions: { mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <StepUpDialog onConfirmed={onConfirmed} onCancel={onCancel} />
    </QueryClientProvider>,
  );
  return { onConfirmed, onCancel };
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("StepUpDialog", () => {
  it("swaps in the fresh token and tells the caller", async () => {
    setAccessToken(fakeToken({ sid: "old" }));
    const fresh = fakeToken({ sid: "old", auth_time: 1_900_000_000 });
    const fetchMock = stub(
      new Response(
        JSON.stringify({ accessToken: fresh, tokenType: "Bearer" }),
        { status: 200 },
      ),
    );
    const { onConfirmed } = renderDialog();
    const user = userEvent.setup();

    await user.type(screen.getByLabelText("Code"), "123456");
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    await waitFor(() => expect(onConfirmed).toHaveBeenCalledTimes(1));
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/auth/step-up",
      expect.objectContaining({ method: "POST" }),
    );
    const session = getSession();
    expect(session.status).toBe("signedIn");
    if (session.status === "signedIn")
      expect(session.claims.authTime).toBe(1_900_000_000);
  });

  it("shows a wrong code once, without retrying it", async () => {
    setAccessToken(fakeToken());
    const fetchMock = stub(
      new Response(
        JSON.stringify({
          title: "The code is incorrect or expired.",
          code: "mfa.code_invalid",
        }),
        { status: 401 },
      ),
    );
    const { onConfirmed } = renderDialog();
    const user = userEvent.setup();

    await user.type(screen.getByLabelText("Code"), "000000");
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "The code is incorrect or expired.",
    );
    expect(onConfirmed).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1); // no refresh-and-retry: that would count as a second wrong attempt
  });

  it("can be cancelled", async () => {
    const { onCancel } = renderDialog();
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: "Cancel" }));
    expect(onCancel).toHaveBeenCalledTimes(1);
  });
});
