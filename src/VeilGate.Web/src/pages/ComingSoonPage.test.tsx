import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import ComingSoonPage from "./ComingSoonPage";

describe("ComingSoonPage", () => {
  it("says plainly that the page is not built yet and lists what is planned", () => {
    render(
      <ComingSoonPage
        title="Audit log"
        summary="A read-only record."
        planned={["Sign-ins", "Policy changes"]}
      />,
    );

    expect(screen.getByRole("heading", { name: "Audit log" })).toBeInTheDocument();
    expect(screen.getByText("Not built yet")).toBeInTheDocument();
    expect(screen.getByText("Sign-ins")).toBeInTheDocument();
    expect(screen.getByText("Policy changes")).toBeInTheDocument();
  });
});
