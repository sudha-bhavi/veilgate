import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { fakeToken } from "../test/fakeToken";
import { resetSessionForTests, setAccessToken } from "../lib/session";
import PolicyDetailPage from "./PolicyDetailPage";

vi.mock("../components/PolicyEditor", async () => {
  const { createElement } = await import("react");
  return {
    default: (props: {
      value: string;
      onChange: (value: string) => void;
      readOnly: boolean;
    }) =>
      createElement("textarea", {
        "aria-label": "Policy YAML",
        value: props.value,
        readOnly: props.readOnly,
        onChange: (event: { target: { value: string } }) =>
          props.onChange(event.target.value),
      }),
  };
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status });

const detail = (over: Record<string, unknown> = {}) => ({
  id: "p1",
  name: "Strict",
  description: "Hides people",
  status: "Published",
  current: {
    id: "v1",
    versionNo: 1,
    state: "Published",
    yaml: "version: 1\nname: strict\n",
    summary: null,
    publishedAt: "2026-01-01T00:00:00Z",
  },
  draft: null,
  etag: '"abc"',
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

type Handlers = { put?: () => Response; validate?: () => Response };

function stubApi(handlers: Handlers = {}) {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method === "PUT") {
      return (
        handlers.put ?? (() => json({ etag: '"def"', versionNo: 2 }))
      )();
    }
    if (url.endsWith("/validate")) {
      return (
        handlers.validate ??
        (() => json({ valid: true, errors: [], warnings: [] }))
      )();
    }
    return json(detail());
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderPage(role: string) {
  setAccessToken(fakeToken({ role }));
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/policies/p1"]}>
        <Routes>
          <Route path="/policies/:id" element={<PolicyDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("PolicyDetailPage", () => {
  it("shows the policy and its published text", async () => {
    stubApi();
    renderPage("SecurityOfficer");

    expect(
      await screen.findByRole("heading", { name: "Strict" }),
    ).toBeInTheDocument();
    expect(await screen.findByLabelText("Policy YAML")).toHaveValue(
      "version: 1\nname: strict\n",
    );
  });

  it("saves a draft with the ETag it started from", async () => {
    const fetchMock = stubApi();
    const user = userEvent.setup();
    renderPage("SecurityOfficer");

    const editor = await screen.findByLabelText("Policy YAML");
    expect(screen.getByRole("button", { name: "Save draft" })).toBeDisabled();

    await user.type(editor, "# note");
    await user.click(screen.getByRole("button", { name: "Save draft" }));

    await screen.findByRole("button", { name: "Save draft" });
    const put =
      fetchMock.mock.calls.find(([, init]) => init?.method === "PUT") ?? [];
    const [putUrl, putInit] = put;
    expect(putUrl).toBe("/api/v1/policies/p1/draft");
    const headers = (putInit?.headers ?? {}) as Record<string, string>;
    expect(headers["If-Match"]).toBe('"abc"');
    expect(JSON.parse(String(putInit?.body)).yaml).toContain("# note");
  });

  it("is read only for a role that cannot write policies", async () => {
    stubApi();
    renderPage("Developer");

    expect(await screen.findByLabelText("Policy YAML")).toHaveAttribute(
      "readonly",
    );
    expect(
      screen.queryByRole("button", { name: "Save draft" }),
    ).not.toBeInTheDocument();
  });

  it("offers a reload when someone else changed the policy first", async () => {
    stubApi({
      put: () =>
        json(
          {
            title: "The policy was changed by someone else.",
            code: "policy.etag_mismatch",
          },
          412,
        ),
    });
    const user = userEvent.setup();
    renderPage("SecurityOfficer");

    await user.type(await screen.findByLabelText("Policy YAML"), "x");
    await user.click(screen.getByRole("button", { name: "Save draft" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "The policy was changed by someone else.",
    );
    expect(
      screen.getByRole("button", { name: "Discard my changes and reload" }),
    ).toBeInTheDocument();
  });

  it("lists each problem with its line and column", async () => {
    stubApi({
      validate: () =>
        json({
          valid: false,
          errors: [{ line: 2, column: 3, message: "Unknown action" }],
          warnings: ["No entities listed"],
        }),
    });
    const user = userEvent.setup();
    renderPage("SecurityOfficer");

    await screen.findByLabelText("Policy YAML");
    await user.click(screen.getByRole("button", { name: "Validate" }));

    expect(
      await screen.findByText("Line 2, column 3: Unknown action"),
    ).toBeInTheDocument();
    expect(screen.getByText(/1\s+problem\s+found/)).toBeInTheDocument();
    expect(screen.getByText("No entities listed")).toBeInTheDocument();
  });

  it("says when the policy is valid", async () => {
    stubApi();
    const user = userEvent.setup();
    renderPage("SecurityOfficer");

    await screen.findByLabelText("Policy YAML");
    await user.click(screen.getByRole("button", { name: "Validate" }));

    expect(await screen.findByText("The policy is valid.")).toBeInTheDocument();
  });

  it("will not validate text that has not been saved", async () => {
    stubApi();
    const user = userEvent.setup();
    renderPage("SecurityOfficer");

    await user.type(await screen.findByLabelText("Policy YAML"), "x");

    expect(screen.getByRole("button", { name: "Validate" })).toBeDisabled();
    expect(screen.getByText(/Unsaved changes/)).toBeInTheDocument();
  });
});
