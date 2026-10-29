import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Badge from "../components/Badge";
import Button from "../components/Button";
import EvalStateBadge from "../components/EvalStateBadge";
import TextField from "../components/TextField";
import { describeApiError } from "../lib/apiErrorText";
import {
  datasetFormErrors,
  emptyDatasetForm,
  emptyEvalForm,
  evalFormErrors,
  toDatasetBody,
  toEvaluationBody,
} from "../lib/evalForm";
import { isActive, percent } from "../lib/evalFormat";
import {
  createDataset,
  createEvaluation,
  listDatasets,
  listEvaluablePolicies,
  listEvaluations,
} from "../lib/evaluationsApi";
import { showToast } from "../lib/toast";

const field =
  "w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

export default function EvaluationsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const datasets = useQuery({ queryKey: ["datasets"], queryFn: listDatasets });
  const policies = useQuery({
    queryKey: ["policies", "evaluable"],
    queryFn: listEvaluablePolicies,
  });
  const runs = useQuery({
    queryKey: ["evaluations"],
    queryFn: () => listEvaluations(1, 20),
    // While anything is waiting or running, ask again every few seconds.
    refetchInterval: (query) =>
      query.state.data?.items.some((run) => isActive(run.state)) ? 3000 : false,
  });

  const [runForm, setRunForm] = useState(emptyEvalForm);
  const [runAttempted, setRunAttempted] = useState(false);
  const [datasetForm, setDatasetForm] = useState(emptyDatasetForm);
  const [datasetAttempted, setDatasetAttempted] = useState(false);
  const [picked, setPicked] = useState<string[]>([]);

  const start = useMutation({
    mutationFn: createEvaluation,
    onSuccess: (run) => {
      void queryClient.invalidateQueries({ queryKey: ["evaluations"] });
      navigate(`/evaluations/${run.id}`);
    },
  });

  const addDataset = useMutation({
    mutationFn: createDataset,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["datasets"] });
      setDatasetForm(emptyDatasetForm());
      setDatasetAttempted(false);
      showToast("Dataset created.", "success");
    },
  });

  const runErrors = evalFormErrors(runForm);
  const shownRunErrors = runAttempted ? runErrors : {};
  const datasetErrors = datasetFormErrors(datasetForm);
  const shownDatasetErrors = datasetAttempted ? datasetErrors : {};

  const policyNames = new Map((policies.data ?? []).map((p) => [p.id, p.name]));
  const datasetNames = new Map((datasets.data ?? []).map((d) => [d.id, d.name]));

  function submitRun(event: FormEvent) {
    event.preventDefault();
    setRunAttempted(true);
    if (Object.keys(runErrors).length === 0) {
      start.mutate(toEvaluationBody(runForm));
    }
  }

  function submitDataset(event: FormEvent) {
    event.preventDefault();
    setDatasetAttempted(true);
    if (Object.keys(datasetErrors).length === 0) {
      addDataset.mutate(toDatasetBody(datasetForm));
    }
  }

  // At most two runs can be picked for a comparison; picking a third drops the oldest pick.
  function togglePick(id: string) {
    setPicked((current) =>
      current.includes(id)
        ? current.filter((other) => other !== id)
        : current.length >= 2
          ? [current[1], id]
          : [...current, id],
    );
  }

  return (
    <main className="mx-auto max-w-5xl space-y-8 p-6">
      <header>
        <h1 className="text-xl font-semibold">Evaluations</h1>
        <p className="mt-1 text-muted">
          Measure how well a policy protects a dataset of made-up notes whose
          sensitive values are already known.
        </p>
      </header>

      <section className="space-y-4">
        <h2 className="text-base font-semibold">Run an evaluation</h2>
        <form
          onSubmit={submitRun}
          className="space-y-4 rounded-xl border border-line bg-card p-4"
        >
          <div className="grid gap-4 sm:grid-cols-2">
            <div>
              <label htmlFor="eval-dataset" className="mb-1 block text-muted">
                Dataset
              </label>
              <select
                id="eval-dataset"
                value={runForm.datasetId}
                onChange={(event) =>
                  setRunForm({ ...runForm, datasetId: event.target.value })
                }
                className={field}
              >
                <option value="">Choose a dataset</option>
                {datasets.data?.map((dataset) => (
                  <option key={dataset.id} value={dataset.id}>
                    {dataset.name} ({dataset.caseCount} notes)
                  </option>
                ))}
              </select>
              {shownRunErrors.datasetId && (
                <p className="mt-1 text-xs text-danger">{shownRunErrors.datasetId}</p>
              )}
            </div>

            <div>
              <label htmlFor="eval-policy" className="mb-1 block text-muted">
                Policy
              </label>
              <select
                id="eval-policy"
                value={runForm.policyId}
                onChange={(event) =>
                  setRunForm({ ...runForm, policyId: event.target.value })
                }
                className={field}
              >
                <option value="">Choose a policy</option>
                {policies.data?.map((policy) => (
                  <option key={policy.id} value={policy.id}>
                    {policy.name}
                  </option>
                ))}
              </select>
              {shownRunErrors.policyId && (
                <p className="mt-1 text-xs text-danger">{shownRunErrors.policyId}</p>
              )}
            </div>

            <div>
              <label htmlFor="eval-mode" className="mb-1 block text-muted">
                Detection mode
              </label>
              <select
                id="eval-mode"
                value={runForm.mode}
                onChange={(event) =>
                  setRunForm({
                    ...runForm,
                    mode: event.target.value as typeof runForm.mode,
                  })
                }
                className={field}
              >
                <option value="rules">Rules only</option>
                <option value="rules+ner">Rules and NER model</option>
              </select>
            </div>

            <TextField
              label="Recall target"
              inputMode="decimal"
              value={runForm.threshold}
              error={shownRunErrors.threshold}
              onChange={(event) =>
                setRunForm({ ...runForm, threshold: event.target.value })
              }
            />

            <TextField
              label="Policy version (optional)"
              inputMode="numeric"
              value={runForm.version}
              error={shownRunErrors.version}
              onChange={(event) =>
                setRunForm({ ...runForm, version: event.target.value })
              }
            />
          </div>
          <p className="text-xs text-muted">
            The run passes if it finds at least the recall target of the
            sensitive items. Leave the version empty to use the published
            version, or the draft if nothing is published yet.
          </p>

          {start.isError && (
            <p role="alert" className="text-danger">
              {describeApiError(start.error)}
            </p>
          )}

          <Button type="submit" loading={start.isPending}>
            Start evaluation
          </Button>
        </form>
      </section>

      <section className="space-y-4">
        <h2 className="text-base font-semibold">Datasets</h2>
        <form
          onSubmit={submitDataset}
          className="space-y-4 rounded-xl border border-line bg-card p-4"
        >
          <p className="text-muted">
            A dataset is a set of made-up notes with known answers, so no real
            patient data is ever stored. The same seed always gives the same notes.
          </p>
          <div className="grid gap-4 sm:grid-cols-3">
            <TextField
              label="Dataset name"
              value={datasetForm.name}
              maxLength={100}
              error={shownDatasetErrors.name}
              onChange={(event) =>
                setDatasetForm({ ...datasetForm, name: event.target.value })
              }
            />
            <TextField
              label="Number of notes (optional)"
              inputMode="numeric"
              placeholder="200"
              value={datasetForm.count}
              error={shownDatasetErrors.count}
              onChange={(event) =>
                setDatasetForm({ ...datasetForm, count: event.target.value })
              }
            />
            <TextField
              label="Seed (optional)"
              inputMode="numeric"
              placeholder="1337"
              value={datasetForm.seed}
              error={shownDatasetErrors.seed}
              onChange={(event) =>
                setDatasetForm({ ...datasetForm, seed: event.target.value })
              }
            />
          </div>
          {addDataset.isError && (
            <p role="alert" className="text-danger">
              {describeApiError(addDataset.error)}
            </p>
          )}
          <Button type="submit" variant="secondary" loading={addDataset.isPending}>
            Create dataset
          </Button>
        </form>

        {datasets.data && datasets.data.length > 0 && (
          <ul className="divide-y divide-line rounded-xl border border-line bg-card">
            {datasets.data.map((dataset) => (
              <li key={dataset.id} className="flex items-center justify-between px-4 py-3">
                <span className="font-medium">{dataset.name}</span>
                <span className="text-muted">
                  {dataset.caseCount} notes · created{" "}
                  {new Date(dataset.createdAt).toLocaleDateString()}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-base font-semibold">Recent runs</h2>
          {picked.length === 2 && (
            <Link
              to={`/evaluations/compare?a=${picked[0]}&b=${picked[1]}`}
              className="rounded-lg border border-line bg-surface px-4 py-2 font-medium hover:bg-line"
            >
              Compare selected runs
            </Link>
          )}
        </div>
        <p className="text-xs text-muted">
          Tick two finished runs of the same dataset to compare them. The second
          one you tick is compared against the first.
        </p>

        {runs.isError && (
          <div role="alert" className="rounded-xl border border-danger p-4">
            <p className="text-danger">{describeApiError(runs.error)}</p>
            <Button className="mt-3" variant="secondary" onClick={() => runs.refetch()}>
              Retry
            </Button>
          </div>
        )}

        {runs.isPending && <div className="h-24 animate-pulse rounded-xl bg-line" />}

        {runs.data && runs.data.items.length === 0 && (
          <div className="rounded-xl border border-line bg-card p-6 text-center">
            <p>No evaluations yet. Start one above.</p>
          </div>
        )}

        {runs.data && runs.data.items.length > 0 && (
          <div className="overflow-x-auto rounded-xl border border-line bg-card">
            <table className="w-full text-left">
              <caption className="sr-only">Evaluation runs</caption>
              <thead className="text-xs uppercase tracking-wide text-muted">
                <tr>
                  <th className="px-4 py-2 font-medium" />
                  <th className="px-4 py-2 font-medium">Policy</th>
                  <th className="px-4 py-2 font-medium">Dataset</th>
                  <th className="px-4 py-2 font-medium">State</th>
                  <th className="px-4 py-2 font-medium">Recall</th>
                  <th className="px-4 py-2 font-medium">Leaks</th>
                  <th className="px-4 py-2 font-medium">Target</th>
                  <th className="px-4 py-2 font-medium">Started</th>
                </tr>
              </thead>
              <tbody>
                {runs.data.items.map((run) => {
                  const label = `${policyNames.get(run.policyId) ?? "Policy"} v${run.policyVersion}`;
                  return (
                    <tr key={run.id} className="border-t border-line">
                      <td className="px-4 py-3">
                        <input
                          type="checkbox"
                          aria-label={`Compare ${label}`}
                          checked={picked.includes(run.id)}
                          disabled={run.state !== "Succeeded"}
                          onChange={() => togglePick(run.id)}
                        />
                      </td>
                      <td className="px-4 py-3">
                        <Link
                          to={`/evaluations/${run.id}`}
                          className="font-medium text-accent hover:underline"
                        >
                          {label}
                        </Link>
                      </td>
                      <td className="px-4 py-3 text-muted">
                        {datasetNames.get(run.datasetId) ?? "—"}
                      </td>
                      <td className="px-4 py-3">
                        <EvalStateBadge state={run.state} />
                        {isActive(run.state) && (
                          <span className="ml-2 text-xs text-muted">
                            {run.progress.done}/{run.progress.total}
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-3">
                        {run.result ? percent(run.result.recall) : "—"}
                      </td>
                      <td className="px-4 py-3">{run.result ? run.result.leaks : "—"}</td>
                      <td className="px-4 py-3">
                        {run.result ? (
                          <Badge tone={run.result.gatePassed ? "success" : "danger"}>
                            {run.result.gatePassed ? "Passed" : "Missed"}
                          </Badge>
                        ) : (
                          "—"
                        )}
                      </td>
                      <td className="px-4 py-3 text-muted">
                        {new Date(run.createdAt).toLocaleString()}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </main>
  );
}
