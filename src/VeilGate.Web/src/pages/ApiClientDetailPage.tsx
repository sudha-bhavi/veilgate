import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useStepUpMutation } from "../auth/useStepUpMutation";
import ApiClientFields from "../components/ApiClientFields";
import Badge from "../components/Badge";
import Button from "../components/Button";
import ConfirmDialog from "../components/ConfirmDialog";
import CopyButton from "../components/CopyButton";
import SecretReveal from "../components/SecretReveal";
import {
  clientFormErrors,
  formFromClient,
  toUpdateBody,
  type ApiClientFormState,
} from "../lib/apiClientForm";
import {
  GRACE_OPTIONS,
  getApiClient,
  revokeApiClient,
  rotateSecret,
  updateApiClient,
  type UpdateApiClientBody,
} from "../lib/apiClientsApi";
import { describeApiError } from "../lib/apiErrorText";
import { listPolicies } from "../lib/playgroundApi";
import { showToast } from "../lib/toast";

type Rotated = { clientSecret: string; previousSecretsEndAt: string | null };

export default function ApiClientDetailPage() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const client = useQuery({
    queryKey: ["api-client", id],
    queryFn: () => getApiClient(id),
  });
  const policies = useQuery({
    queryKey: ["policies", "options"],
    queryFn: listPolicies,
  });

  const [edited, setEdited] = useState<ApiClientFormState | null>(null); // null = nothing changed yet
  const [grace, setGrace] = useState<number>(24);
  const [rotated, setRotated] = useState<Rotated | null>(null);
  const [dialog, setDialog] = useState<"rotate" | "revoke" | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const refreshList = () =>
    queryClient.invalidateQueries({ queryKey: ["api-clients"] });

  const save = useMutation({
    mutationFn: (body: UpdateApiClientBody) => updateApiClient(id, body),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["api-client", id] });
      setEdited(null);
      void refreshList();
      showToast("Changes saved.", "success");
    },
  });

  const revoke = useMutation({
    mutationFn: () => revokeApiClient(id),
    onSuccess: () => {
      void refreshList();
      showToast("Client revoked.", "success");
      navigate("/clients");
    },
    onError: (error) => setFailure(describeApiError(error)),
  });

  // Rotating a secret hands out a new credential, so the server may ask the person to confirm their identity first.
  const { mutation: rotate, stepUpDialog } = useStepUpMutation({
    mutationFn: () => rotateSecret(id, grace),
    onSuccess: (result) => {
      setRotated(result);
      setDialog(null);
      setFailure(null);
    },
    onFailure: (error) => setFailure(describeApiError(error)),
  });

  if (client.isPending) {
    return (
      <main className="mx-auto max-w-3xl p-6">
        <div className="h-64 animate-pulse rounded-xl bg-line" />
      </main>
    );
  }

  if (client.isError) {
    return (
      <main className="mx-auto max-w-3xl space-y-3 p-6">
        <p role="alert" className="text-danger">
          {describeApiError(client.error)}
        </p>
        <Link to="/clients" className="text-accent underline">
          Back to API clients
        </Link>
      </main>
    );
  }

  const current = client.data;
  const active = current.status === "Active";
  const form = edited ?? formFromClient(current);
  const body = toUpdateBody(form, current);
  const errors = clientFormErrors(form, "edit");
  const dirty = Object.keys(body).length > 0;
  const graceLabel = GRACE_OPTIONS.find((option) => option.hours === grace)?.label;

  return (
    <main className="mx-auto max-w-3xl space-y-6 p-6">
      <Link to="/clients" className="text-muted hover:text-fg">
        ← API clients
      </Link>

      <header className="space-y-2">
        <div className="flex items-center gap-3">
          <h1 className="text-xl font-semibold">{current.name}</h1>
          <Badge tone={active ? "success" : "danger"}>{current.status}</Badge>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-muted">Client ID</span>
          <code className="rounded-lg border border-line bg-bg px-2 py-1 font-mono text-[13px]">
            {current.clientId}
          </code>
          <CopyButton text={current.clientId} label="Copy client ID" />
        </div>
        <p className="text-xs text-muted">
          Created {new Date(current.createdAt).toLocaleDateString()}
        </p>
      </header>

      {rotated && (
        <SecretReveal
          title="Save the new secret now"
          intro={
            rotated.previousSecretsEndAt
              ? `It is shown only this once. The old secret keeps working until ${new Date(rotated.previousSecretsEndAt).toLocaleString()}.`
              : "It is shown only this once."
          }
          rows={[{ label: "Client secret", value: rotated.clientSecret }]}
          onDone={() => setRotated(null)}
        />
      )}

      {!active && (
        <p className="rounded-lg border border-line p-3 text-muted">
          This client was revoked. It can no longer call the gateway or be changed.
        </p>
      )}

      {active && (
        <section className="space-y-4 rounded-xl border border-line bg-card p-4">
          <h2 className="text-base font-semibold">Settings</h2>
          <ApiClientFields
            mode="edit"
            form={form}
            errors={errors}
            policies={policies.data}
            onChange={setEdited}
          />

          {save.isError && (
            <p role="alert" className="text-danger">
              {describeApiError(save.error)}
            </p>
          )}

          <div className="flex gap-3">
            <Button
              loading={save.isPending}
              disabled={!dirty || Object.keys(errors).length > 0}
              onClick={() => save.mutate(body)}
            >
              Save changes
            </Button>
            {dirty && (
              <Button variant="ghost" onClick={() => setEdited(null)}>
                Revert
              </Button>
            )}
          </div>
        </section>
      )}

      {active && (
        <section className="space-y-3 rounded-xl border border-line bg-card p-4">
          <h2 className="text-base font-semibold">Client secret</h2>
          <p className="text-muted">
            The secret cannot be shown again. Rotate it to get a new one: the old
            secret keeps working for the time you choose, so you can update your
            application without downtime.
          </p>
          <div className="flex flex-wrap items-end gap-3">
            <div>
              <label htmlFor="grace" className="mb-1 block text-muted">
                Keep the old secret working for
              </label>
              <select
                id="grace"
                value={grace}
                onChange={(event) => setGrace(Number(event.target.value))}
                className="rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent"
              >
                {GRACE_OPTIONS.map((option) => (
                  <option key={option.hours} value={option.hours}>
                    {option.label}
                  </option>
                ))}
              </select>
            </div>
            <Button
              variant="secondary"
              onClick={() => {
                setFailure(null);
                setDialog("rotate");
              }}
            >
              Rotate secret
            </Button>
          </div>
        </section>
      )}

      {active && (
        <section className="space-y-3 rounded-xl border border-danger p-4">
          <h2 className="text-base font-semibold">Revoke</h2>
          <p className="text-muted">
            A revoked client stops working at once and cannot be restored.
          </p>
          <Button
            variant="danger"
            onClick={() => {
              setFailure(null);
              setDialog("revoke");
            }}
          >
            Revoke client
          </Button>
        </section>
      )}

      {dialog === "rotate" && (
        <ConfirmDialog
          title="Rotate the secret?"
          confirmLabel="Rotate now"
          loading={rotate.isPending}
          error={failure}
          onConfirm={() => {
            setFailure(null);
            rotate.mutate();
          }}
          onCancel={() => setDialog(null)}
        >
          <p className="text-muted">
            A new secret is created now. The old one keeps working for {graceLabel}.
            You may be asked to confirm your identity first.
          </p>
        </ConfirmDialog>
      )}

      {dialog === "revoke" && (
        <ConfirmDialog
          title={`Revoke ${current.name}?`}
          confirmLabel="Revoke"
          danger
          loading={revoke.isPending}
          error={failure}
          onConfirm={() => revoke.mutate()}
          onCancel={() => setDialog(null)}
        >
          <p className="text-muted">
            Anything using this client will start failing right away.
          </p>
        </ConfirmDialog>
      )}

      {stepUpDialog}
    </main>
  );
}
