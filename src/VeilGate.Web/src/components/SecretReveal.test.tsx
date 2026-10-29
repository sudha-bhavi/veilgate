import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import SecretReveal from "./SecretReveal";

describe("SecretReveal", () => {
  it("shows each value and makes the person confirm before continuing", async () => {
    const onDone = vi.fn();
    const user = userEvent.setup();
    render(
      <SecretReveal
        title="Save your secret"
        intro="It is shown once."
        rows={[
          { label: "Client ID", value: "vg_abc" },
          { label: "Client secret", value: "s3cret" },
        ]}
        onDone={onDone}
      />,
    );

    expect(screen.getByText("vg_abc")).toBeInTheDocument();
    expect(screen.getByText("s3cret")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue" })).toBeDisabled();

    await user.click(screen.getByLabelText(/I have stored the secret/));
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(onDone).toHaveBeenCalledTimes(1);
  });
});
