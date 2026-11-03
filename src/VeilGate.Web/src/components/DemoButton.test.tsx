import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { getSession, resetSessionForTests } from "../lib/session";
import { fakeToken } from "../test/fakeToken";
import { callsTo, json, stubFetch } from "../test/stubFetch";
import Where from "../test/Where";
import DemoButton from "./DemoButton";

function renderButton() {
  return render(
    <MemoryRouter initialEntries={["/login"]}>
      <Routes>
        <Route path="/login" element={<DemoButton />} />
        <Route path="*" element={<Where label="elsewhere" />} />
      </Routes>
    </MemoryRouter>,
  );
}

const demoToken = () => fakeToken({ role: "SecurityOfficer", amr: ["demo"] });

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("DemoButton", () => {
  it("appears when the server says the demo is on", async () => {
    stubFetch(() => json({ demoEnabled: true }));
    renderButton();

    expect(await screen.findByRole("button", { name: "Try the demo" })).toBeInTheDocument();
  });

  it("shows nothing when the server says the demo is off", async () => {
    const fetchMock = stubFetch(() => json({ demoEnabled: false }));
    renderButton();

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    await new Promise((resolve) => setTimeout(resolve, 0)); // let the answer be handled
    expect(screen.queryByRole("button", { name: "Try the demo" })).not.toBeInTheDocument();
  });

  it("shows nothing when the server cannot say", async () => {
    const fetchMock = stubFetch(() => json({}, 500));
    renderButton();

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByRole("button", { name: "Try the demo" })).not.toBeInTheDocument();
  });

  it("signs in as the demo user and moves on", async () => {
    const fetchMock = stubFetch((_url, init) =>
      init?.method === "POST"
        ? json({ accessToken: demoToken(), tokenType: "Bearer", expiresIn: 600 })
        : json({ demoEnabled: true }),
    );
    const user = userEvent.setup();
    renderButton();

    await user.click(await screen.findByRole("button", { name: "Try the demo" }));

    expect(await screen.findByText("elsewhere")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/auth/demo", "POST")).toHaveLength(1);
    const session = getSession();
    expect(session.status).toBe("signedIn");
    if (session.status === "signedIn") {
      expect(session.claims.role).toBe("SecurityOfficer");
      expect(session.claims.amr).toContain("demo");
    }
  });

  it("says why when the demo cannot start, and lets the visitor try again", async () => {
    stubFetch((_url, init) =>
      init?.method === "POST"
        ? json({ code: "demo.unavailable", title: "The demo is not available." }, 404)
        : json({ demoEnabled: true }),
    );
    const user = userEvent.setup();
    renderButton();

    await user.click(await screen.findByRole("button", { name: "Try the demo" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The demo is not available.");
    expect(screen.getByRole("button", { name: "Try the demo" })).toBeEnabled();
    expect(getSession().status).not.toBe("signedIn");
  });
});
