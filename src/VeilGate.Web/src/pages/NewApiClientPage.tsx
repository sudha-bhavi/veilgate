import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useStepUpMutation } from "../auth/useStepUpMutation";
import ApiClientFields from "../components/ApiClientFields";
import Button from "../components/Button";
import SecretReveal from "../components/SecretReveal";
import {
  clientFormErrors,
  emptyClientForm,
  toCreateBody,
} from "../lib/apiClientForm";
import {
  createApiClient,
  type CreateApiClientBody,
  type CreatedApiClient,
} from "../lib/apiClientsApi";
import { describeApiError } from "../lib/apiErrorText";
import { listPolicies } from "../lib/playgroundApi";

export default function NewApiClientPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [form, setForm] = useState(emptyClientForm);
  const [attempted, setAttempted] = useState(false);
  const [created, setCreated] = useState<CreatedApiClient | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const policies = useQuery({
    queryKey: ["policies", "options"],
    queryFn: listPolicies,
  });

  // Creating a client hands out credentials, so the server may ask the person to confirm their identity first.
  const { mutation: create, stepUpDialog } = useStepUpMutation({
    mutationFn: (body: CreateApiClientBody) => createApiClient(body),
    onSuccess: (client) => {
      setCreated(client);
      setFailure(null);
      void queryClient.invalidateQueries({ queryKey: ["api-clients"] });
    },
    onFailure: (error) => setFailure(describeApiError(error)),
  });

  const errors = clientFormErrors(form, "create");
  // Name problems wait until the first attempt; an empty permission list is flagged straight away.
  const shown = attempted ? errors : errors.scopes ? { scopes: errors.scopes } : {};

  function submit(event: FormEvent) {
    event.preventDefault();
    setAttempted(true);
    setFailure(null);
    if (Object.keys(errors).length === 0) create.mutate(toCreateBody(form));
  }

  if (created) {
    return (
      <main className="mx-auto max-w-2xl space-y-6 p-6">
        <h1 className="text-xl font-semibold">{created.name}</h1>
        <SecretReveal
          title="Save these credentials now"
          intro="The secret is shown only this once. VeilGate keeps just a fingerprint of it, so it cannot be shown again. Your application sends the client ID and secret to /oauth/token to get an access token."
          rows={[
            { label: "Client ID", value: created.clientId },
            { label: "Client secret", value: created.clientSecret },
          ]}
          onDone={() => navigate(`/clients/${created.id}`)}
        />
      </main>
    );
  }

  return (
    <main className="mx-auto max-w-2xl space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold">New API client</h1>
        <p className="mt-1 text-muted">
          Give an application its own credentials, limited to the permissions it
          needs. You may be asked to confirm your identity when you create it.
        </p>
      </header>

      <form
        onSubmit={submit}
        className="space-y-5 rounded-xl border border-line bg-card p-4"
      >
        <ApiClientFields
          mode="create"
          form={form}
          errors={shown}
          policies={policies.data}
          onChange={setForm}
        />

        {failure && (
          <p role="alert" className="text-danger">
            {failure}
          </p>
        )}

        <div className="flex gap-3">
          <Button type="submit" loading={create.isPending}>
            Create client
          </Button>
          <Link
            to="/clients"
            className="inline-flex items-center rounded-lg px-4 py-2 text-muted hover:text-fg"
          >
            Cancel
          </Link>
        </div>
      </form>

      {stepUpDialog}
    </main>
  );
}
