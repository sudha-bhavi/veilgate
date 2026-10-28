import { useState, type FormEvent } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import Button from "../components/Button";
import PlaygroundResult from "../components/PlaygroundResult";
import { describeApiError } from "../lib/apiErrorText";
import { listPolicies, runPlayground, type Mode } from "../lib/playgroundApi";

const MAX_CHARS = 32_768;
const SAMPLE =
  "Patient Jane Roe, born 03/14/1962, MRN 4481920, can be reached at 555-201-8890. She was seen at Riverside Clinic on 2026-09-02 for a follow-up.";

const MODES: { value: Mode; label: string; help: string }[] = [
  {
    value: "detect",
    label: "Detect",
    help: "Find sensitive items without changing the text.",
  },
  {
    value: "deidentify",
    label: "De-identify",
    help: "Mask the text the way the gateway would.",
  },
  {
    value: "chat",
    label: "Chat",
    help: "Mask, ask a language model, and restore the answer.",
  },
];

const field =
  "w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

export default function PlaygroundPage() {
  const [mode, setMode] = useState<Mode>("deidentify");
  const [text, setText] = useState("");
  const [model, setModel] = useState("");
  const [policyId, setPolicyId] = useState("");

  const policies = useQuery({
    queryKey: ["policies", "options"],
    queryFn: listPolicies,
  });
  const run = useMutation({ mutationFn: runPlayground });

  const tooLong = text.length > MAX_CHARS;
  const canRun =
    text.trim().length > 0 &&
    !tooLong &&
    (mode !== "chat" || model.trim().length > 0);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (canRun) run.mutate({ mode, text, model: model.trim(), policyId });
  }

  return (
    <main className="mx-auto max-w-4xl space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold">Playground</h1>
        <p className="mt-1 text-muted">
          Try VeilGate on text and see every stage. Use made-up text: runs are
          audited like real calls.
        </p>
      </header>

      <form
        onSubmit={submit}
        className="space-y-4 rounded-xl border border-line bg-card p-4"
      >
        <fieldset>
          <legend className="mb-2 text-xs uppercase tracking-wide text-muted">
            Mode
          </legend>
          <div className="flex flex-wrap gap-4">
            {MODES.map((option) => (
              <label key={option.value} className="flex items-center gap-2">
                <input
                  type="radio"
                  name="mode"
                  value={option.value}
                  checked={mode === option.value}
                  onChange={() => {
                    setMode(option.value);
                    run.reset();
                  }}
                />
                {option.label}
              </label>
            ))}
          </div>
          <p className="mt-2 text-muted">
            {MODES.find((m) => m.value === mode)?.help}
          </p>
        </fieldset>

        <div>
          <label htmlFor="pg-text" className="mb-1 block font-medium">
            Text
          </label>
          <textarea
            id="pg-text"
            rows={7}
            value={text}
            onChange={(event) => setText(event.target.value)}
            className={`${field} font-mono text-[13px]`}
          />
          <p
            className={`mt-1 text-xs ${tooLong ? "text-danger" : "text-muted"}`}
          >
            {text.length.toLocaleString()} / {MAX_CHARS.toLocaleString()}{" "}
            characters
          </p>
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          {mode !== "detect" && (
            <div>
              <label htmlFor="pg-policy" className="mb-1 block font-medium">
                Policy
              </label>
              <select
                id="pg-policy"
                value={policyId}
                onChange={(event) => setPolicyId(event.target.value)}
                disabled={policies.isPending}
                className={field}
              >
                <option value="">Tenant default</option>
                {policies.data?.map((policy) => (
                  <option key={policy.id} value={policy.id}>
                    {policy.name}
                  </option>
                ))}
              </select>
              {policies.isError && (
                <p className="mt-1 text-xs text-muted">
                  Could not load policies; the tenant default will be used.
                </p>
              )}
            </div>
          )}

          {mode === "chat" && (
            <div>
              <label htmlFor="pg-model" className="mb-1 block font-medium">
                Model
              </label>
              <input
                id="pg-model"
                value={model}
                onChange={(event) => setModel(event.target.value)}
                className={field}
              />
              <p className="mt-1 text-xs text-muted">
                A model one of your providers serves. A tenant admin can see
                them under Providers.
              </p>
            </div>
          )}
        </div>

        <div className="flex flex-wrap gap-3">
          <Button type="submit" loading={run.isPending} disabled={!canRun}>
            Run
          </Button>
          <Button variant="secondary" onClick={() => setText(SAMPLE)}>
            Use sample text
          </Button>
          <Button
            variant="ghost"
            onClick={() => {
              setText("");
              run.reset();
            }}
          >
            Clear
          </Button>
        </div>
      </form>

      <div aria-live="polite">
        {run.isError && (
          <p
            role="alert"
            className="rounded-xl border border-danger p-4 text-danger"
          >
            {describeApiError(run.error)}
          </p>
        )}
        {run.isSuccess && (
          <div className="rounded-xl border border-line bg-card p-4">
            <PlaygroundResult
              outcome={run.data}
              originalText={run.variables.text}
            />
          </div>
        )}
      </div>
    </main>
  );
}
