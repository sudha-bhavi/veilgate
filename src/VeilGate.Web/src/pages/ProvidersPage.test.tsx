import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, noContent, stubFetch } from "../test/stubFetch";
import ProvidersPage from "./ProvidersPage";

const provider = (over: Record<string, unknown> = {}) => ({
  id: "pr1",
  name: "Main",
  baseUrl: "https://api.example.com/v1",
  keyLast4: "abcd",
  models: ["model-a"],
  timeoutSeconds: 60,
  priority: 0,
  enabled: true,
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

const two = [
  provider(),
  provider({ id: "pr2", name: "Backup", priority: 1, baseUrl: "https://backup.example.com" }),
];

function fakeServer(items: unknown[] = two, testResult: () => Response = () => json({ ok: true, latencyMs: 230, errorCategory: null })) {
  return stubFetch((url, init) => {
    if (url.endsWith("/order")) return noContent();
    if (url.endsWith("/test")) return testResult();
    if (init?.method === "POST" || init?.method === "PUT") return json(provider(), 200);
    if (init?.method === "DELETE") return noContent();
    return json({ items });
  });
}

const options = { path: "/providers" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("ProvidersPage", () => {
  it("lists providers in the order they are tried, with only the last four key characters", async () => {
    fakeServer();
    renderPage(<ProvidersPage />, options);

    expect(await screen.findByRole("heading", { name: "Main" })).toBeInTheDocument();
    const names = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(names).toEqual(["Main", "Backup"]);
    expect(screen.getAllByText("•••• abcd")).toHaveLength(2);
  });

  it("moves a provider down by sending the new order", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<ProvidersPage />, options);

    await user.click(await screen.findByRole("button", { name: "Move Main down" }));

    await waitFor(() => expect(callsTo(fetchMock, "/order", "PUT")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/order", "PUT")[0][1])).toEqual({ ids: ["pr2", "pr1"] });
  });

  it("cannot move the first one up or the last one down", async () => {
    fakeServer();
    renderPage(<ProvidersPage />, options);

    expect(await screen.findByRole("button", { name: "Move Main up" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Move Backup down" })).toBeDisabled();
  });

  it("shows the result of a connection test", async () => {
    fakeServer();
    const user = userEvent.setup();
    renderPage(<ProvidersPage />, options);

    const [first] = await screen.findAllByRole("button", { name: "Test connection" });
    await user.click(first);

    expect(await screen.findByText("Working · 230 ms")).toBeInTheDocument();
  });

  it("reports a failed connection test by category", async () => {
    fakeServer(two, () => json({ ok: false, latencyMs: 0, errorCategory: "auth" }));
    const user = userEvent.setup();
    renderPage(<ProvidersPage />, options);

    const [first] = await screen.findAllByRole("button", { name: "Test connection" });
    await user.click(first);

    expect(await screen.findByText("Failed: auth")).toBeInTheDocument();
  });

  it("adds a provider with the key only in the request", async () => {
    const fetchMock = fakeServer([]);
    const user = userEvent.setup();
    renderPage(<ProvidersPage />, options);

    await user.click(await screen.findByRole("button", { name: "Add provider" }));
    await user.type(screen.getByLabelText("Name"), "Main");
    const address = screen.getByLabelText("Address");
    await user.clear(address);
    await user.type(address, "https://api.example.com/v1");
    await user.type(screen.getByLabelText("API key"), "key-12345678");
    await user.type(screen.getByLabelText("Models"), "model-a");
    await user.click(screen.getAllByRole("button", { name: "Add provider" })[0]);

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/llm-providers", "POST")).toHaveLength(1));
    expect(bodyOf(callsTo(fetchMock, "/api/v1/llm-providers", "POST")[0][1])).toMatchObject({
      name: "Main",
      baseUrl: "https://api.example.com/v1",
      apiKey: "key-12345678",
      models: ["model-a"],
    });
  });

  it("checks the form before sending anything", async () => {
    const fetchMock = fakeServer([]);
    const user = userEvent.setup();
    renderPage(<ProvidersPage />, options);

    await user.click(await screen.findByRole("button", { name: "Add provider" }));
    await user.click(screen.getAllByRole("button", { name: "Add provider" })[0]);

    expect(await screen.findByText("Name is required.")).toBeInTheDocument();
    expect(screen.getByText("API key is required.")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/llm-providers", "POST")).toHaveLength(0);
  });

  it("deletes only after confirmation", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<ProvidersPage />, options);

    const [firstDelete] = await screen.findAllByRole("button", { name: "Delete" });
    await user.click(firstDelete);
    expect(callsTo(fetchMock, "/api/v1/llm-providers/pr1", "DELETE")).toHaveLength(0);

    const dialog = await screen.findByRole("dialog");
    await user.click(
      Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Delete") as HTMLElement,
    );

    await waitFor(() => expect(callsTo(fetchMock, "/api/v1/llm-providers/pr1", "DELETE")).toHaveLength(1));
  });
});
