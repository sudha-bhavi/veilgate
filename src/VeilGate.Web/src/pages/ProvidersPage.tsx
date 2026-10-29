import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, Plus } from "lucide-react";
import Badge from "../components/Badge";
import Button from "../components/Button";
import ConfirmDialog from "../components/ConfirmDialog";
import ProviderForm from "../components/ProviderForm";
import { describeApiError } from "../lib/apiErrorText";
import { bodyFromProvider } from "../lib/providerForm";
import {
  createProvider,
  deleteProvider,
  listProviders,
  reorderProviders,
  testProvider,
  updateProvider,
  type LlmProvider,
  type ProviderBody,
  type ProviderTestResult,
} from "../lib/providersApi";
import { showToast } from "../lib/toast";

type Panel = "new" | LlmProvider | null;

export default function ProvidersPage() {
  const queryClient = useQueryClient();
  const providers = useQuery({ queryKey: ["llm-providers"], queryFn: listProviders });

  const [panel, setPanel] = useState<Panel>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<LlmProvider | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [results, setResults] = useState<Record<string, ProviderTestResult | string>>({});

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["llm-providers"] });

  const save = useMutation({
    mutationFn: (variables: { id?: string; body: ProviderBody }) =>
      variables.id
        ? updateProvider(variables.id, variables.body)
        : createProvider(variables.body),
    onSuccess: async () => {
      await refresh();
      setPanel(null);
      setFormError(null);
      showToast("Provider saved.", "success");
    },
    onError: (error) => setFormError(describeApiError(error)),
  });

  const reorder = useMutation({
    mutationFn: reorderProviders,
    onSuccess: () => refresh(),
    onError: (error) => showToast(describeApiError(error), "error"),
  });

  const toggle = useMutation({
    mutationFn: (provider: LlmProvider) =>
      updateProvider(provider.id, bodyFromProvider(provider, { enabled: !provider.enabled })),
    onSuccess: () => refresh(),
    onError: (error) => showToast(describeApiError(error), "error"),
  });

  const test = useMutation({
    mutationFn: testProvider,
    onSuccess: (result, id) => setResults((all) => ({ ...all, [id]: result })),
    onError: (error, id) =>
      setResults((all) => ({ ...all, [id]: describeApiError(error) })),
  });

  const remove = useMutation({
    mutationFn: (provider: LlmProvider) => deleteProvider(provider.id),
    onSuccess: async () => {
      await refresh();
      setDeleting(null);
      setDeleteError(null);
      showToast("Provider deleted.", "success");
    },
    onError: (error) => setDeleteError(describeApiError(error)),
  });

  const sorted = [...(providers.data ?? [])].sort(
    (a, b) => a.priority - b.priority || a.name.localeCompare(b.name),
  );

  function move(index: number, direction: -1 | 1) {
    const ids = sorted.map((provider) => provider.id);
    const target = index + direction;
    if (target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    reorder.mutate(ids);
  }

  return (
    <main className="mx-auto max-w-4xl space-y-6 p-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Providers</h1>
          <p className="mt-1 text-muted">
            The language model services the gateway can use. If the first one
            fails, the next one that serves the model is tried.
          </p>
        </div>
        {panel === null && (
          <Button
            onClick={() => {
              setFormError(null);
              setPanel("new");
            }}
          >
            <Plus className="size-4" aria-hidden="true" />
            Add provider
          </Button>
        )}
      </header>

      {panel !== null && (
        <ProviderForm
          key={panel === "new" ? "new" : panel.id}
          provider={panel === "new" ? undefined : panel}
          loading={save.isPending}
          error={formError}
          onSubmit={(body) =>
            save.mutate({ id: panel === "new" ? undefined : panel.id, body })
          }
          onCancel={() => setPanel(null)}
        />
      )}

      {providers.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(providers.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => providers.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {providers.isPending && (
        <div className="space-y-2" aria-label="Loading providers">
          {[0, 1].map((n) => (
            <div key={n} className="h-24 animate-pulse rounded-xl bg-line" />
          ))}
        </div>
      )}

      {providers.isSuccess && sorted.length === 0 && (
        <div className="rounded-xl border border-line bg-card p-6 text-center">
          <p>No providers yet. Add one so the gateway can reach a language model.</p>
        </div>
      )}

      {sorted.length > 0 && (
        <ol className="space-y-3">
          {sorted.map((provider, index) => {
            const result = results[provider.id];
            return (
              <li
                key={provider.id}
                className="space-y-3 rounded-xl border border-line bg-card p-4"
              >
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-3">
                      <span className="text-xs text-muted">Tried #{index + 1}</span>
                      <h2 className="text-base font-semibold">{provider.name}</h2>
                      <Badge tone={provider.enabled ? "success" : "muted"}>
                        {provider.enabled ? "Enabled" : "Disabled"}
                      </Badge>
                    </div>
                    <p className="mt-1 break-all font-mono text-[13px] text-muted">
                      {provider.baseUrl}
                    </p>
                  </div>
                  <div className="flex gap-1">
                    <Button
                      variant="ghost"
                      aria-label={`Move ${provider.name} up`}
                      disabled={index === 0 || reorder.isPending}
                      onClick={() => move(index, -1)}
                    >
                      <ArrowUp className="size-4" aria-hidden="true" />
                    </Button>
                    <Button
                      variant="ghost"
                      aria-label={`Move ${provider.name} down`}
                      disabled={index === sorted.length - 1 || reorder.isPending}
                      onClick={() => move(index, 1)}
                    >
                      <ArrowDown className="size-4" aria-hidden="true" />
                    </Button>
                  </div>
                </div>

                <dl className="grid gap-x-6 gap-y-1 text-muted sm:grid-cols-[auto_1fr]">
                  <dt>Models</dt>
                  <dd className="text-fg">{provider.models.join(", ")}</dd>
                  <dt>Key</dt>
                  <dd className="font-mono text-[13px] text-fg">•••• {provider.keyLast4}</dd>
                  <dt>Timeout</dt>
                  <dd className="text-fg">{provider.timeoutSeconds} seconds</dd>
                </dl>

                <div className="flex flex-wrap items-center gap-3">
                  <Button
                    variant="secondary"
                    loading={test.isPending && test.variables === provider.id}
                    onClick={() => test.mutate(provider.id)}
                  >
                    Test connection
                  </Button>
                  <Button
                    variant="secondary"
                    onClick={() => {
                      setFormError(null);
                      setPanel(provider);
                    }}
                  >
                    Edit
                  </Button>
                  <Button
                    variant="secondary"
                    loading={toggle.isPending && toggle.variables?.id === provider.id}
                    onClick={() => toggle.mutate(provider)}
                  >
                    {provider.enabled ? "Disable" : "Enable"}
                  </Button>
                  <Button
                    variant="danger"
                    onClick={() => {
                      setDeleteError(null);
                      setDeleting(provider);
                    }}
                  >
                    Delete
                  </Button>
                  {result !== undefined && (
                    <span role="status" className="text-sm">
                      {typeof result === "string" ? (
                        <span className="text-danger">{result}</span>
                      ) : result.ok ? (
                        <span className="text-success">Working · {result.latencyMs} ms</span>
                      ) : (
                        <span className="text-danger">
                          Failed: {result.errorCategory ?? "unknown error"}
                        </span>
                      )}
                    </span>
                  )}
                </div>
              </li>
            );
          })}
        </ol>
      )}

      {deleting && (
        <ConfirmDialog
          title={`Delete ${deleting.name}?`}
          confirmLabel="Delete"
          danger
          loading={remove.isPending}
          error={deleteError}
          onConfirm={() => remove.mutate(deleting)}
          onCancel={() => setDeleting(null)}
        >
          <p className="text-muted">
            Requests for its models will go to the next provider that serves
            them, or fail if there is none.
          </p>
        </ConfirmDialog>
      )}
    </main>
  );
}
