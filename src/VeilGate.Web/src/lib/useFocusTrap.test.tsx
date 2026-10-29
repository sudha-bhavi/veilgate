import { useRef } from "react";
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useFocusTrap } from "./useFocusTrap";

function Dialog() {
  const ref = useRef<HTMLDivElement>(null);
  useFocusTrap(ref);
  return (
    <div ref={ref} role="dialog">
      <button>First</button>
      <button>Last</button>
    </div>
  );
}

function Page({ open }: { open: boolean }) {
  return (
    <>
      <button>Outside</button>
      {open && <Dialog />}
    </>
  );
}

describe("useFocusTrap", () => {
  it("moves focus into the dialog when it opens", () => {
    render(<Page open />);
    expect(screen.getByRole("button", { name: "First" })).toHaveFocus();
  });

  it("keeps Tab and Shift+Tab inside the dialog", async () => {
    const user = userEvent.setup();
    render(<Page open />);

    await user.tab();
    expect(screen.getByRole("button", { name: "Last" })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole("button", { name: "First" })).toHaveFocus();
    await user.tab({ shift: true });
    expect(screen.getByRole("button", { name: "Last" })).toHaveFocus();
  });

  it("gives focus back when the dialog closes", () => {
    const { rerender } = render(<Page open={false} />);
    screen.getByRole("button", { name: "Outside" }).focus();

    rerender(<Page open />);
    expect(screen.getByRole("button", { name: "First" })).toHaveFocus();

    rerender(<Page open={false} />);
    expect(screen.getByRole("button", { name: "Outside" })).toHaveFocus();
  });
});
