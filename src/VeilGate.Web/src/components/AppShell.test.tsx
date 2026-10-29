import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { fakeToken } from "../test/fakeToken";
import Where from "../test/Where";
import { resetSessionForTests, setAccessToken } from "../lib/session";
import AppShell from "./AppShell";

function renderShell(role: string) {
  setAccessToken(fakeToken({ role }));
  return render(
    <MemoryRouter initialEntries={["/dashboard"]}>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/dashboard" element={<Where label="dashboard" />} />
        </Route>
        <Route path="/login" element={<Where label="login" />} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("AppShell", () => {
  it("shows only the links the role may use", () => {
    renderShell("Developer");
    const nav = screen.getByRole("navigation", { name: "Main" });
    expect(nav).toHaveTextContent("API clients");
    expect(nav).not.toHaveTextContent("Users");
  });

  it("shows the role in the topbar", () => {
    renderShell("SecurityOfficer");
    expect(screen.getByText("Security Officer")).toBeInTheDocument();
  });

  it("signs out and goes to the login page", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    renderShell("TenantAdmin");

    await user.click(screen.getByRole("button", { name: "Sign out" }));

    expect(await screen.findByText("login")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/auth/logout",
      expect.objectContaining({ method: "POST" }),
    );
  });

  it("lets keyboard users skip the navigation", () => {
    renderShell("Developer");
    expect(
      screen.getByRole("link", { name: "Skip to main content" }),
    ).toHaveAttribute("href", "#main-content");
    expect(document.getElementById("main-content")).not.toBeNull();
  });
});
