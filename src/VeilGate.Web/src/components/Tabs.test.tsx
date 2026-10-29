import { useState } from "react";
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Tabs from "./Tabs";

const TABS = [
  { id: "one", label: "One" },
  { id: "two", label: "Two" },
  { id: "three", label: "Three" },
] as const;

function Harness() {
  const [active, setActive] = useState<(typeof TABS)[number]["id"]>("one");
  return (
    <Tabs label="Sections" tabs={TABS} active={active} onChange={setActive} panelId="panel" />
  );
}

const tab = (name: string) => screen.getByRole("tab", { name });

describe("Tabs", () => {
  it("puts only the active tab in the Tab order", () => {
    render(<Harness />);
    expect(tab("One")).toHaveAttribute("tabindex", "0");
    expect(tab("Two")).toHaveAttribute("tabindex", "-1");
  });

  it("moves with the arrow keys, wrapping around at both ends", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    tab("One").focus();

    await user.keyboard("{ArrowRight}");
    expect(tab("Two")).toHaveAttribute("aria-selected", "true");
    expect(tab("Two")).toHaveFocus();

    await user.keyboard("{ArrowLeft}{ArrowLeft}");
    expect(tab("Three")).toHaveAttribute("aria-selected", "true");
    expect(tab("Three")).toHaveFocus();
  });

  it("jumps to the first and last tab with Home and End", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    tab("One").focus();

    await user.keyboard("{End}");
    expect(tab("Three")).toHaveFocus();
    await user.keyboard("{Home}");
    expect(tab("One")).toHaveFocus();
  });

  it("still works with the mouse", async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.click(tab("Three"));
    expect(tab("Three")).toHaveAttribute("aria-selected", "true");
  });
});
