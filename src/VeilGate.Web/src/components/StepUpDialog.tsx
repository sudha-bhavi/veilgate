import { useState, type FormEvent, type KeyboardEvent } from "react";
import { useMutation } from "@tanstack/react-query";
import { stepUp } from "../auth/stepUp";
import { describeApiError } from "../lib/apiErrorText";
import Button from "./Button";

export default function StepUpDialog({
  onConfirmed,
  onCancel,
}: {
  onConfirmed: () => void;
  onCancel: () => void;
}) {
  const [code, setCode] = useState("");
  const confirm = useMutation({ mutationFn: stepUp, onSuccess: onConfirmed });

  function submit(event: FormEvent) {
    event.preventDefault();
    if (code.trim()) confirm.mutate(code.trim());
  }

  function onKeyDown(event: KeyboardEvent) {
    if (event.key === "Escape") onCancel();
  }

  return (
    <div
      className="fixed inset-0 z-40 flex items-center justify-center bg-black/50 p-4"
      onKeyDown={onKeyDown}
    >
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="stepup-title"
        aria-describedby="stepup-body"
        onSubmit={submit}
        className="w-full max-w-sm space-y-4 rounded-xl border border-line bg-surface p-5 shadow-lg"
      >
        <div>
          <h2 id="stepup-title" className="text-base font-semibold">
            Confirm your identity
          </h2>
          <p id="stepup-body" className="mt-2 text-muted">
            This action needs a fresh check. Enter the code from your
            authenticator app, or a recovery code. If you just used a code to
            sign in, wait for the next one.
          </p>
        </div>

        <div>
          <label htmlFor="stepup-code" className="mb-1 block text-muted">
            Code
          </label>
          <input
            id="stepup-code"
            autoFocus
            autoComplete="one-time-code"
            maxLength={12}
            value={code}
            onChange={(event) => setCode(event.target.value)}
            className="w-full rounded-lg border border-line bg-bg px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent"
          />
        </div>

        {confirm.isError && (
          <p role="alert" className="text-danger">
            {describeApiError(confirm.error)}
          </p>
        )}

        <div className="flex justify-end gap-3">
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button
            type="submit"
            loading={confirm.isPending}
            disabled={!code.trim()}
          >
            Confirm
          </Button>
        </div>
      </form>
    </div>
  );
}
