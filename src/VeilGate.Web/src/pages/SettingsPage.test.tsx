import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import SettingsPage from "./SettingsPage";

const settings = (over: Record<string, unknown> = {}) => ({
  organisation: { name: "Acme Health", status: "Active" },
  rules: {
    accessTokenMinutes: 10,
    sessionIdleHours: 8,
    sessionMaxDays: 7,
    lockoutAttempts: 5,
    lockoutMinutes: 15,
    mfaRequired: true,
    ...over,
  },
});

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("SettingsPage", () => {
  it("shows the organisation and the rules the server reports", async () => {
    stubFetch(() => json(settings()));
    renderPage(<SettingsPage />, { path: "/settings" });

    expect(await screen.findByText("Acme Health")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
    expect(screen.getByText("Required for everyone")).toBeInTheDocument();
    expect(screen.getByText("5 wrong tries, then 15 minutes")).toBeInTheDocument();
    expect(screen.getByText("8 hours")).toBeInTheDocument();
    expect(screen.getByText("7 days")).toBeInTheDocument();
    expect(screen.getByText("10 minutes")).toBeInTheDocument();
  });

  it("offers no way to change anything", async () => {
    stubFetch(() => json(settings()));
    renderPage(<SettingsPage />, { path: "/settings" });

    await screen.findByText("Acme Health");
    expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /save/i })).not.toBeInTheDocument();
  });

  it("says so, with a retry, when the server cannot be reached", async () => {
    stubFetch(() => json({ title: "Server error", code: "server.error" }, 500));
    renderPage(<SettingsPage />, { path: "/settings" });

    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  });
});
