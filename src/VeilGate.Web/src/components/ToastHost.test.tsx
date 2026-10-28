import { afterEach, describe, expect, it } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { clearToastsForTests, showToast } from "../lib/toast";
import ToastHost from "./ToastHost";

afterEach(() => act(() => clearToastsForTests()));

describe("ToastHost", () => {
  it("shows a toast and lets the person dismiss it", async () => {
    const user = userEvent.setup();
    render(<ToastHost />);

    act(() => showToast("Policy saved", "success"));
    expect(screen.getByText("Policy saved")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByText("Policy saved")).not.toBeInTheDocument();
  });

  it("announces an error as an alert", () => {
    render(<ToastHost />);
    act(() => showToast("Could not save", "error"));
    expect(screen.getByRole("alert")).toHaveTextContent("Could not save");
  });
});
