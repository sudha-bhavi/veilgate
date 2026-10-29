import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import PolicyTestPanel from "./PolicyTestPanel";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status });

function stubApi(body: unknown) {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    void url;
    void init;
    return json(body);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderPanel(dirty = false) {
  const client = new QueryClient({
    defaultOptions: { mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <PolicyTestPanel policyId="p1" dirty={dirty} />
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("PolicyTestPanel", () => {
  it("shows what was found and the masked result", async () => {
    const fetchMock = stubApi({
      entities: [
        {
          type: "PERSON",
          start: 8,
          end: 16,
          confidence: 0.93,
          action: "TOKENIZE",
        },
      ],
      maskedText: "Patient [PERSON_1] was seen",
      blocked: false,
      blockedTypes: [],
    });
    const user = userEvent.setup();
    renderPanel();

    await user.type(
      screen.getByLabelText("Sample text"),
      "Patient Jane Roe was seen",
    );
    await user.click(screen.getByRole("button", { name: "Run test" }));

    expect(
      await screen.findByText("Patient [PERSON_1] was seen"),
    ).toBeInTheDocument();
    expect(screen.getByText("93%")).toBeInTheDocument();
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
      text: "Patient Jane Roe was seen",
    });
  });

  it("explains when the gateway would block the text", async () => {
    stubApi({
      entities: [],
      maskedText: null,
      blocked: true,
      blockedTypes: ["SSN"],
    });
    const user = userEvent.setup();
    renderPanel();

    await user.type(screen.getByLabelText("Sample text"), "x");
    await user.click(screen.getByRole("button", { name: "Run test" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("BLOCK for: SSN");
  });

  it("warns that unsaved edits are not part of the test", () => {
    renderPanel(true);
    expect(screen.getByText(/unsaved changes in the editor/)).toBeInTheDocument();
  });

  it("will not run on empty text", () => {
    renderPanel();
    expect(screen.getByRole("button", { name: "Run test" })).toBeDisabled();
  });
});
