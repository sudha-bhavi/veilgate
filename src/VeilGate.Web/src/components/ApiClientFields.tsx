import type {
  ApiClientFormErrors,
  ApiClientFormState,
} from "../lib/apiClientForm";
import { API_CLIENT_SCOPES } from "../lib/apiClientsApi";
import type { PolicyOption } from "../lib/playgroundApi";
import TextField from "./TextField";

type Props = {
  mode: "create" | "edit";
  form: ApiClientFormState;
  errors: ApiClientFormErrors;
  policies: PolicyOption[] | undefined;
  onChange: (next: ApiClientFormState) => void;
};

const field =
  "w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

/** The fields that create and edit have in common. The name can only be set when creating. */
export default function ApiClientFields({
  mode,
  form,
  errors,
  policies,
  onChange,
}: Props) {
  function set<K extends keyof ApiClientFormState>(
    key: K,
    value: ApiClientFormState[K],
  ) {
    onChange({ ...form, [key]: value });
  }

  function toggleScope(scope: string) {
    set(
      "scopes",
      form.scopes.includes(scope)
        ? form.scopes.filter((s) => s !== scope)
        : [...form.scopes, scope],
    );
  }

  return (
    <div className="space-y-5">
      {mode === "create" && (
        <TextField
          label="Name"
          value={form.name}
          maxLength={80}
          error={errors.name}
          onChange={(event) => set("name", event.target.value)}
        />
      )}

      <fieldset>
        <legend className="mb-2 text-muted">Permissions</legend>
        <div className="space-y-2">
          {API_CLIENT_SCOPES.map((scope) => (
            <div key={scope.value} className="rounded-lg border border-line p-3">
              <label className="flex items-center gap-2 font-medium">
                <input
                  type="checkbox"
                  checked={form.scopes.includes(scope.value)}
                  onChange={() => toggleScope(scope.value)}
                />
                <span>{scope.label}</span>
              </label>
              <p className="mt-1 pl-6 text-muted">{scope.help}</p>
            </div>
          ))}
        </div>
        {errors.scopes && (
          <p className="mt-1 text-xs text-danger">{errors.scopes}</p>
        )}
      </fieldset>

      <TextField
        label="Requests per minute (optional)"
        inputMode="numeric"
        placeholder="600"
        value={form.rateLimit}
        error={errors.rateLimit}
        onChange={(event) => set("rateLimit", event.target.value)}
      />

      <div>
        <label htmlFor="client-networks" className="mb-1 block text-muted">
          Allowed networks (optional)
        </label>
        <textarea
          id="client-networks"
          rows={3}
          value={form.ipAllowlist}
          onChange={(event) => set("ipAllowlist", event.target.value)}
          className={`${field} font-mono text-[13px]`}
        />
        <p className="mt-1 text-xs text-muted">
          One address or network per line, for example 203.0.113.0/24. Leave
          empty to allow calls from anywhere.
        </p>
        {errors.ipAllowlist && (
          <p className="mt-1 text-xs text-danger">{errors.ipAllowlist}</p>
        )}
      </div>

      <div>
        <label htmlFor="client-policy" className="mb-1 block text-muted">
          Default policy
        </label>
        <select
          id="client-policy"
          value={form.defaultPolicyId}
          onChange={(event) => set("defaultPolicyId", event.target.value)}
          disabled={policies === undefined}
          className={field}
        >
          <option value="">{mode === "create" ? "None" : "Keep as it is"}</option>
          {policies?.map((policy) => (
            <option key={policy.id} value={policy.id}>
              {policy.name}
            </option>
          ))}
        </select>
        <p className="mt-1 text-xs text-muted">
          Used when a request from this client does not name a policy.
          {mode === "edit" && " A default policy can be changed but not removed."}
        </p>
      </div>
    </div>
  );
}
