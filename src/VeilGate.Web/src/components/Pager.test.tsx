import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Pager from "./Pager";

describe("Pager", () => {
  it("says where you are and moves to the next page", async () => {
    const onPage = vi.fn();
    render(<Pager page={2} pages={3} summary="60 clients" onPage={onPage} />);

    expect(screen.getByText("60 clients · page 2 of 3")).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole("button", { name: "Next" }));
    expect(onPage).toHaveBeenCalledWith(3);
  });

  it("stops at both ends", () => {
    const { rerender } = render(
      <Pager page={1} pages={3} summary="x" onPage={vi.fn()} />,
    );
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();

    rerender(<Pager page={3} pages={3} summary="x" onPage={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Next" })).toBeDisabled();
  });
});
