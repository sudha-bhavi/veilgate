import { useState, type FormEvent } from "react";
import {
  emptyProviderForm,
  formFromProvider,
  providerFormErrors,
  toProviderBody,
  type ProviderFormState,
} from "../lib/providerForm";
import type { LlmProvider, ProviderBody } from "../lib/providersApi";
import Button from "./Button";
import TextField from "./TextField";

type Props = {
  /** The provider being edited, or nothing when adding a new one. */
  provider?: LlmProvider;
  loading: boolean;
  error: string | null;
  onSubmit: (body: ProviderBody) => void;
  onCancel: () => void;
};

const field =
  "w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

export default function ProviderForm({ provider, loading, error, onSubmit, onCancel }: Props) {
  const mode = provider ? "edit" : "create";
  const [form, setForm] = useState<ProviderFormState>(() =>
    provider ? formFromProvider(provider) : emptyProviderForm(),
  );
  const [attempted, setAttempted] = useState(false);

  const errors = providerFormErrors(form, mode);
  const shown = attempted ? errors : {};

  function set<K extends keyof ProviderFormState>(key: K, value: ProviderFormState[K]) {
    setForm({ ...form, [key]: value });
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    setAttempted(true);
    if (Object.keys(errors).length === 0) onSubmit(toProviderBody(form));
  }

  return (
    <form onSubmit={submit} className="space-y-5 rounded-xl border border-line bg-card p-4">
      <h2 className="text-base font-semibold">
        {provider ? `Edit ${provider.name}` : "Add a provider"}
      </h2>

      <TextField
        label="Name"
        value={form.name}
        maxLength={80}
        error={shown.name}
        onChange={(event) => set("name", event.target.value)}
      />

      <div>
        <TextField
          label="Address"
          placeholder="https://api.example.com"
          value={form.baseUrl}
          error={shown.baseUrl}
          onChange={(event) => set("baseUrl", event.target.value)}
        />
        <p className="mt-1 text-xs text-muted">
          The base address of an OpenAI-compatible service, without /v1 (VeilGate
          adds /v1/chat/completions). It is checked again every time the gateway
          connects.
        </p>
      </div>

      <div>
        <TextField
          label="API key"
          type="password"
          autoComplete="off"
          value={form.apiKey}
          error={shown.apiKey}
          onChange={(event) => set("apiKey", event.target.value)}
        />
        <p className="mt-1 text-xs text-muted">
          {provider
            ? `The stored key ends in ${provider.keyLast4}. Leave this empty to keep it. If you change the address, enter the key again.`
            : "It is stored encrypted and never shown again. Only its last four characters are displayed."}
        </p>
      </div>

      <div>
        <label htmlFor="provider-models" className="mb-1 block text-muted">
          Models
        </label>
        <textarea
          id="provider-models"
          rows={3}
          value={form.models}
          onChange={(event) => set("models", event.target.value)}
          className={`${field} font-mono text-[13px]`}
        />
        <p className="mt-1 text-xs text-muted">One model name per line.</p>
        {shown.models && <p className="mt-1 text-xs text-danger">{shown.models}</p>}
      </div>

      <TextField
        label="Timeout in seconds (optional)"
        inputMode="numeric"
        placeholder="60"
        value={form.timeoutSeconds}
        error={shown.timeoutSeconds}
        onChange={(event) => set("timeoutSeconds", event.target.value)}
      />

      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          checked={form.enabled}
          onChange={(event) => set("enabled", event.target.checked)}
        />
        Enabled
      </label>

      {error && (
        <p role="alert" className="text-danger">
          {error}
        </p>
      )}

      <div className="flex gap-3">
        <Button type="submit" loading={loading}>
          {provider ? "Save provider" : "Add provider"}
        </Button>
        <Button variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}
