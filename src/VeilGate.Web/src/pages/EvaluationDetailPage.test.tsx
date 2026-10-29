import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { json, stubFetch } from "../test/stubFetch";
import EvaluationDetailPage from "./EvaluationDetailPage";

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
    f1: 0.985,
    leakRate: 0.004,
    leaks: 2,
    gatePassed: true,
    perType: {
      SSN: { tp: 40, fp: 0, fn: 0, precision: 1, recall: 1 },
      PHONE: { tp: 38, fp: 2, fn: 2, precision: 0.95, recall: 0.9 },
    },
  },
  ...over,
});

function fakeServer(current: Record<string, unknown>) {
  return stubFetch((url) => {
    if (url.includes("/leaks")) {
      return json({
        items: [
          { note: 12, type: "PHONE", start: 40, end: 52, reason: "Written with spaces", value: "555 201 8890" },
          { note: 31, type: "PHONE", start: 8, end: 20, reason: "Written with dots", value: "555.201.8891" },
        ],
        page: 1,
        pageSize: 50,
      });
    }
    if (url.startsWith("/api/v1/datasets")) {
      return json({ items: [{ id: "d1", name: "Notes A", caseCount: 200, createdAt: "2026-01-01T00:00:00Z" }] });
    }
    if (url.startsWith("/api/v1/policies")) {
      return json({
        items: [{ id: "p1", name: "Strict", status: "Published", currentVersion: 2, hasDraft: false, createdAt: "2026-01-01T00:00:00Z" }],
        page: 1,
        pageSize: 100,
        total: 1,
      });
    }
    return json(current);
  });
}

const page = { path: "/evaluations/r1", route: "/evaluations/:id" };

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("EvaluationDetailPage", () => {
  it("shows progress while a run is working", async () => {
    fakeServer(run({ state: "Running", result: null, progress: { done: 50, total: 200 } }));
    renderPage(<EvaluationDetailPage />, page);

    expect(await screen.findByText("Checking note 50 of 200…")).toBeInTheDocument();
    expect(screen.getByRole("progressbar", { name: "Evaluation progress" })).toHaveAttribute(
      "aria-valuenow",
      "25",
    );
  });

  it("shows the results, the types and the leaks of a finished run", async () => {
    fakeServer(run());
    renderPage(<EvaluationDetailPage />, page);

    expect(await screen.findByRole("heading", { name: "Strict v2" })).toBeInTheDocument();
    const results = screen.getByRole("region", { name: "Results" });
    expect(results).toHaveTextContent("99.1%");
    expect(results).toHaveTextContent("Passed");

    expect(await screen.findByText("Written with spaces")).toBeInTheDocument();
    expect(screen.getByText("555.201.8891")).toBeInTheDocument();

    const types = screen.getByRole("table", { name: "Results by type of sensitive item" });
    expect(types).toHaveTextContent("SSN");
    expect(types).toHaveTextContent("PHONE");
  });

  it("says so when nothing leaked", async () => {
    fakeServer(
      run({
        result: {
          recall: 1,
          precision: 1,
          f1: 1,
          leakRate: 0,
          leaks: 0,
          gatePassed: true,
          perType: {},
        },
      }),
    );
    renderPage(<EvaluationDetailPage />, page);

    expect(await screen.findByText("No sensitive value got through.")).toBeInTheDocument();
  });

  it("explains a failed run", async () => {
    fakeServer(run({ state: "Failed", result: null, errorCode: "worker.timeout" }));
    renderPage(<EvaluationDetailPage />, page);

    expect(await screen.findByRole("alert")).toHaveTextContent("worker.timeout");
  });
});
