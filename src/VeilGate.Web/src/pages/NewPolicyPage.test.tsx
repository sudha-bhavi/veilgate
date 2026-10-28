import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import Where from "../test/Where";
import NewPolicyPage from "./NewPolicyPage";

function stub(response: () => Response) {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    void url;
    void init;
    return response();
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderForm() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/policies/new"]}>
        <Routes>
          <Route path="/policies/new" element={<NewPolicyPage />} />
          <Route path="/policies/:id" element={<Where label="policy" />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("NewPolicyPage", () => {
  it("keeps Create disabled until there is a name", async () => {
    stub(() => new Response("{}", { status: 200 }));
    const user = userEvent.setup();
    renderForm();

    expect(
      screen.getByRole("button", { name: "Create policy" }),
    ).toBeDisabled();
    await user.type(screen.getByLabelText("Name"), "Strict");
    expect(screen.getByRole("button", { name: "Create policy" })).toBeEnabled();
  });

  it("creates the policy with the chosen template and opens it", async () => {
    const fetchMock = stub(
      () =>
        new Response(JSON.stringify({ id: "abc", etag: '"x"' }), {
          status: 201,
        }),
    );
    const user = userEvent.setup();
    renderForm();

    await user.type(screen.getByLabelText("Name"), "Strict");
    await user.click(screen.getByLabelText("Names only"));
    await user.click(screen.getByRole("button", { name: "Create policy" }));

    expect(await screen.findByText("policy")).toBeInTheDocument();
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body).toEqual({ name: "Strict", template: "names-only" });
  });

  it("shows the server's reason when it refuses", async () => {
    stub(
      () =>
        new Response(
          JSON.stringify({
            title: "Validation failed",
            code: "validation.failed",
            errors: { Name: ["Maximum 80 characters"] },
          }),
          { status: 422 },
        ),
    );
    const user = userEvent.setup();
    renderForm();

    await user.type(screen.getByLabelText("Name"), "x");
    await user.click(screen.getByRole("button", { name: "Create policy" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Maximum 80 characters",
    );
  });
});
