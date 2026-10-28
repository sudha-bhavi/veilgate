import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import PlaygroundPage from "./PlaygroundPage";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status });

const policies = {
  items: [
    {
      id: "p1",
      name: "Strict",
      status: "Active",
      currentVersion: 2,
      hasDraft: false,
      createdAt: "2026-01-01T00:00:00Z",
    },
    {
      id: "p2",
      name: "Unpublished",
      status: "Draft",
      currentVersion: null,
      hasDraft: true,
      createdAt: "2026-01-01T00:00:00Z",
    },
  ],
  page: 1,
  pageSize: 50,
  total: 2,
};

function stubApi(run: () => Response) {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    void init;
    return url.startsWith("/api/v1/policies") ? json(200, policies) : run();
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <PlaygroundPage />
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("PlaygroundPage", () => {
  it("masks the text and shows what would leave the gateway", async () => {
    const fetchMock = stubApi(() =>
      json(200, {
        requestId: "req-1",
        entities: [{ type: "PERSON", start: 8, end: 16, action: "REDACT" }],
        maskedText: "Patient [PERSON] was seen",
      }),
    );
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText("Text"), "Patient Jane Roe was seen");
    await user.click(screen.getByRole("button", { name: "Run" }));

    expect(
      await screen.findByText("Patient [PERSON] was seen"),
    ).toBeInTheDocument();

    const call = fetchMock.mock.calls.find(
      ([url]) => url === "/api/v1/playground/run",
    );
    const body = JSON.parse(String(call?.[1]?.body));
    expect(body).toEqual({
      mode: "deidentify",
      text: "Patient Jane Roe was seen",
    });
  });

  it("asks for a model before it runs a chat", async () => {
    stubApi(() => json(200, {}));
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByLabelText("Chat"));
    await user.type(screen.getByLabelText("Text"), "hello");
    expect(screen.getByRole("button", { name: "Run" })).toBeDisabled();

    await user.type(screen.getByLabelText("Model"), "model-1");
    expect(screen.getByRole("button", { name: "Run" })).toBeEnabled();
  });

  it("shows the server's message when the run is refused", async () => {
    stubApi(() =>
      json(422, {
        title: "Validation failed",
        code: "validation.failed",
        errors: { Text: ["Maximum 32768 characters"] },
      }),
    );
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText("Text"), "x");
    await user.click(screen.getByRole("button", { name: "Run" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Maximum 32768 characters",
    );
  });

  it("offers only policies that have a published version", async () => {
    stubApi(() => json(200, {}));
    renderPage();

    expect(
      await screen.findByRole("option", { name: "Strict" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("option", { name: "Unpublished" }),
    ).not.toBeInTheDocument();
  });
});
