import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import ErrorBoundary from "./ErrorBoundary";

function Boom(): never {
  throw new Error("boom");
}

afterEach(() => vi.restoreAllMocks());

describe("ErrorBoundary", () => {
  it("shows a friendly page when a screen crashes", () => {
    vi.spyOn(console, "error").mockImplementation(() => {}); // React logs the crash; keep the test output clean
    render(
      <ErrorBoundary>
        <Boom />
      </ErrorBoundary>,
    );
    expect(
      screen.getByRole("heading", { name: "Something went wrong" }),
    ).toBeInTheDocument();
  });

  it("renders its children when nothing is wrong", () => {
    render(
      <ErrorBoundary>
        <p>all good</p>
      </ErrorBoundary>,
    );
    expect(screen.getByText("all good")).toBeInTheDocument();
  });
});
