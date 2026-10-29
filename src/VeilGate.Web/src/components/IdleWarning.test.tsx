import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import IdleWarning from "./IdleWarning";

describe("IdleWarning", () => {
  it("lets the person stay signed in", async () => {
    const onStay = vi.fn();
    render(<IdleWarning onStay={onStay} />);
    expect(screen.getByRole("alertdialog")).toBeInTheDocument();

    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: "Stay signed in" }));
    expect(onStay).toHaveBeenCalledTimes(1);
  });
});
