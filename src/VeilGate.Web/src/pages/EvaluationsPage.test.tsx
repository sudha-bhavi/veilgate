import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { bodyOf, callsTo, json, stubFetch } from "../test/stubFetch";
import EvaluationsPage from "./EvaluationsPage";

const dataset = { id: "d1", name: "Notes A", caseCount: 200, createdAt: "2026-01-01T00:00:00Z" };
const policy = {
  id: "p1",
  name: "Strict",
  status: "Published",
  currentVersion: 2,
  hasDraft: false,
  createdAt: "2026-01-01T00:00:00Z",
};

const run = (over: Record<string, unknown> = {}) => ({
  id: "r1",
  datasetId: "d1",
  policyId: "p1",
  policyVersion: 2,
  mode: "rules",
  recallThreshold: 0.98,
  state: "Succeeded",
  progress: { done: 200, total: 200 },
  attempts: 1,
  createdAt: "2026-01-02T00:00:00Z",
  startedAt: "2026-01-02T00:00:01Z",
  finishedAt: "2026-01-02T00:00:09Z",
  errorCode: null,
  result: {
    recall: 0.991,
    precision: 0.97,
    f1: 0.98,
    leakRate: 0.004,
    leaks: 3,
    gatePassed: true,
    perType: {},
  },
  ...over,
});

function fakeServer(runs: unknown[] = [run()]) {
  return stubFetch((url, init) => {
    if (url.startsWith("/api/v1/evaluations") && init?.method === "POST") {
      return json(run({ id: "r9", state: "Queued", result: null }), 202);
    }
    if (url.startsWith("/api/v1/evaluations")) {
      return json({ items: runs, page: 1, pageSize: 20 });
    }
    if (url.startsWith("/api/v1/datasets") && init?.method === "POST") {
      return json(dataset, 201);
    }
    if (url.startsWith("/api/v1/datasets")) return json({ items: [dataset] });
    if (url.startsWith("/api/v1/policies")) {
      return json({ items: [policy], page: 1, pageSize: 100, total: 1 });
    }
    return json({}, 404);
  });
}

const options = { path: "/evaluations" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("EvaluationsPage", () => {
  it("lists runs with their policy, result and target", async () => {
    fakeServer();
    renderPage(<EvaluationsPage />, options);

    const table = await screen.findByRole("table", { name: "Evaluation runs" });
    expect(await within(table).findByRole("link", { name: "Strict v2" })).toHaveAttribute(
      "href",
      "/evaluations/r1",
    );
    expect(within(table).getByText("99.1%")).toBeInTheDocument();
    expect(within(table).getByText("Passed")).toBeInTheDocument();
  });

  it("asks for a dataset and a policy before it starts", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<EvaluationsPage />, options);

    await user.click(await screen.findByRole("button", { name: "Start evaluation" }));

    expect(await screen.findByText("Choose a dataset.")).toBeInTheDocument();
    expect(screen.getByText("Choose a policy.")).toBeInTheDocument();
    expect(callsTo(fetchMock, "/api/v1/evaluations", "POST")).toHaveLength(0);
  });

  it("starts a run and opens it", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<EvaluationsPage />, options);

    await screen.findByRole("option", { name: "Notes A (200 notes)" });
    await screen.findByRole("option", { name: "Strict" });
    await user.selectOptions(screen.getByLabelText("Dataset"), "d1");
    await user.selectOptions(screen.getByLabelText("Policy"), "p1");
    await user.click(screen.getByRole("button", { name: "Start evaluation" }));

    expect(await screen.findByText("elsewhere")).toBeInTheDocument();
    expect(bodyOf(callsTo(fetchMock, "/api/v1/evaluations", "POST")[0][1])).toEqual({
      datasetId: "d1",
      policyId: "p1",
      mode: "rules",
      recallThreshold: 0.98,
    });
  });

  it("links to a comparison once two finished runs are ticked", async () => {
    fakeServer([run(), run({ id: "r2", policyVersion: 3 })]);
    const user = userEvent.setup();
    renderPage(<EvaluationsPage />, options);

    await user.click(await screen.findByLabelText("Compare Strict v2"));
    await user.click(screen.getByLabelText("Compare Strict v3"));

    expect(screen.getByRole("link", { name: "Compare selected runs" })).toHaveAttribute(
      "href",
      "/evaluations/compare?a=r1&b=r2",
    );
  });

  it("does not let an unfinished run be compared", async () => {
    fakeServer([run({ state: "Running", result: null, progress: { done: 50, total: 200 } })]);
    renderPage(<EvaluationsPage />, options);

    expect(await screen.findByLabelText("Compare Strict v2")).toBeDisabled();
    expect(screen.getByText("50/200")).toBeInTheDocument();
  });

  it("creates a dataset from a name", async () => {
    const fetchMock = fakeServer();
    const user = userEvent.setup();
    renderPage(<EvaluationsPage />, options);

    await user.type(await screen.findByLabelText("Dataset name"), "Notes B");
    await user.click(screen.getByRole("button", { name: "Create dataset" }));

    await waitFor(() =>
      expect(callsTo(fetchMock, "/api/v1/datasets/synthetic", "POST")).toHaveLength(1),
    );
    expect(bodyOf(callsTo(fetchMock, "/api/v1/datasets/synthetic", "POST")[0][1])).toEqual({
      name: "Notes B",
    });
  });
});
