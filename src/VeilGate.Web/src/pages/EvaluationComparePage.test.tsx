import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { resetSessionForTests } from "../lib/session";
import { renderPage } from "../test/renderPage";
import { callsTo, json, stubFetch } from "../test/stubFetch";
import EvaluationComparePage from "./EvaluationComparePage";

const change = (a: number, b: number) => ({ a, b, delta: b - a });

const comparison = {
  a: "r1",
  b: "r2",
  datasetId: "d1",
  recall: change(0.9, 0.95),
  precision: change(0.95, 0.9),
  f1: change(0.92, 0.92),
  leakRate: change(0.05, 0.02),
  leaksA: 10,
  leaksB: 4,
  gateA: false,
  gateB: true,
  perType: {
    PHONE: { recall: change(0.8, 0.95), precision: change(1, 1) },
  },
};

function fakeServer() {
  return stubFetch((url) => {
    if (url.includes("/compare")) return json(comparison);
    if (url.startsWith("/api/v1/evaluations")) {
      return json({
        items: [
          { id: "r1", policyId: "p1", policyVersion: 2 },
          { id: "r2", policyId: "p1", policyVersion: 3 },
        ],
        page: 1,
        pageSize: 20,
      });
    }
    if (url.startsWith("/api/v1/policies")) {
      return json({
        items: [{ id: "p1", name: "Strict", status: "Published", currentVersion: 3, hasDraft: false, createdAt: "2026-01-01T00:00:00Z" }],
        page: 1,
        pageSize: 100,
        total: 1,
      });
    }
    return json({}, 404);
  });
}

afterEach(() => {
  resetSessionForTests();
  vi.unstubAllGlobals();
});

describe("EvaluationComparePage", () => {
  it("judges each change by whether higher or lower is good", async () => {
    const fetchMock = fakeServer();
    renderPage(<EvaluationComparePage />, { path: "/evaluations/compare?a=r1&b=r2", route: "/evaluations/compare" });

    const overall = await screen.findByRole("table", { name: "Overall comparison" });
    // A row starts with its label and then a number, which keeps "Recall" apart from "Recall target".
    const row = (name: string) =>
      within(overall).getByRole("row", { name: new RegExp(`^${name}\\s+\\d`) });

    expect(row("Recall")).toHaveTextContent("+5.0 pts");
    expect(row("Recall")).toHaveTextContent("Better");
    expect(row("Precision")).toHaveTextContent("Worse");
    expect(row("F1 score")).toHaveTextContent("Same");
    expect(row("Leak rate")).toHaveTextContent("Better"); // a lower leak rate is the good direction
    expect(row("Leaks")).toHaveTextContent("-6");
    expect(callsTo(fetchMock, "/api/v1/evaluations/compare?a=r1&b=r2")).toHaveLength(1);
  });

  it("names the runs by policy and version", async () => {
    fakeServer();
    renderPage(<EvaluationComparePage />, { path: "/evaluations/compare?a=r1&b=r2", route: "/evaluations/compare" });

    expect(await screen.findByText(/Strict v3 against Strict v2/)).toBeInTheDocument();
  });

  it("shows the change for each type", async () => {
    fakeServer();
    renderPage(<EvaluationComparePage />, { path: "/evaluations/compare?a=r1&b=r2", route: "/evaluations/compare" });

    const byType = await screen.findByRole("table", { name: "Comparison by type of sensitive item" });
    expect(byType).toHaveTextContent("PHONE");
    expect(byType).toHaveTextContent("+15.0 pts");
  });

  it("asks for two runs when none are given", () => {
    fakeServer();
    renderPage(<EvaluationComparePage />, { path: "/evaluations/compare", route: "/evaluations/compare" });

    expect(screen.getByText(/Pick two finished runs/)).toBeInTheDocument();
  });
});
