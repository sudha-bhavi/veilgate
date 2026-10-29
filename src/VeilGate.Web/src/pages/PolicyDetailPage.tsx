import { lazy, Suspense, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { canAuthorPolicies } from "../auth/policyAccess";
import { useSession } from "../auth/useSession";
import Button from "../components/Button";
import PolicyStatusBadge from "../components/PolicyStatusBadge";
import PolicyTestPanel from "../components/PolicyTestPanel";
import PolicyVersionsPanel from "../components/PolicyVersionsPanel";
import PolicyWorkflowBar from "../components/PolicyWorkflowBar";
import { ApiError } from "../lib/api";
import { describeApiError } from "../lib/apiErrorText";
import {
  editableYaml,
  getPolicy,
  saveDraft,
  validatePolicy,
} from "../lib/policiesApi";
import { describeIssue } from "../lib/policyIssues";
import { showToast } from "../lib/toast";

// Monaco is large, so it is only downloaded when someone opens a policy.
const PolicyEditor = lazy(() => import("../components/PolicyEditor"));

type Tab = "editor" | "versions" | "test";
const TABS: { id: Tab; label: string }[] = [
  { id: "editor", label: "Editor" },
  { id: "versions", label: "Versions" },
  { id: "test", label: "Test" },
];

export default function PolicyDetailPage() {
  const { id = "" } = useParams();
  const session = useSession();
  const queryClient = useQueryClient();
  const policy = useQuery({
    queryKey: ["policy", id],
    queryFn: () => getPolicy(id),
  });

  const [tab, setTab] = useState<Tab>("editor");
  const [edited, setEdited] = useState<string | null>(null); // null = nothing typed yet
  const saved = policy.data ? editableYaml(policy.data) : "";
  const text = edited ?? saved;
  const dirty = edited !== null && edited !== saved;

  const validate = useMutation({ mutationFn: () => validatePolicy(id) });

  const save = useMutation({
    mutationFn: (variables: { yaml: string; etag: string }) =>
      saveDraft(id, variables.yaml, variables.etag),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["policy", id] });
      setEdited(null);
      validate.reset();
      void queryClient.invalidateQueries({ queryKey: ["policies"] });
      showToast("Draft saved.", "success");
    },
  });

  // Closing the tab or reloading with unsaved edits asks first.
  useEffect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [dirty]);

  if (policy.isPending) {
    return (
      <main className="mx-auto max-w-5xl p-6">
        <div className="h-96 animate-pulse rounded-xl bg-line" />
      </main>
    );
  }

  if (policy.isError) {
    return (
      <main className="mx-auto max-w-5xl space-y-3 p-6">
        <p role="alert" className="text-danger">
          {describeApiError(policy.error)}
        </p>
        <Link to="/policies" className="text-accent underline">
          Back to policies
        </Link>
      </main>
    );
  }

  const detail = policy.data;
  const role = session.status === "signedIn" ? session.claims.role : "";
  const canWrite = canAuthorPolicies(role);
  const readOnlyReason = !canWrite
    ? "Your role can read policies but not change them."
    : detail.status === "Archived"
      ? "This policy is archived."
      : detail.draft?.state === "PendingApproval"
        ? "A draft is waiting for approval and cannot be edited."
        : null;
  const conflict =
    save.error instanceof ApiError &&
    (save.error.status === 409 || save.error.status === 412);
  const result = validate.data;

  return (
    <main className="mx-auto max-w-5xl space-y-4 p-6">
      <Link to="/policies" className="text-muted hover:text-fg">
        ← Policies
      </Link>

      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="flex items-center gap-3">
            <h1 className="text-xl font-semibold">{detail.name}</h1>
            <PolicyStatusBadge status={detail.status} />
          </div>
          {detail.description && (
            <p className="mt-1 text-muted">{detail.description}</p>
          )}
          <p className="mt-1 text-xs text-muted">
            {detail.draft
              ? `Editing draft v${detail.draft.versionNo}`
              : detail.current
                ? `Showing published v${detail.current.versionNo}. Saving creates a new draft.`
                : "No text yet."}
          </p>
        </div>

        {!readOnlyReason && (
          <div className="flex flex-wrap items-center gap-3">
            {dirty && (
              <Button variant="ghost" onClick={() => setEdited(null)}>
                Revert
              </Button>
            )}
            <Button
              variant="secondary"
              loading={validate.isPending}
              disabled={dirty}
              onClick={() => validate.mutate()}
            >
              Validate
            </Button>
            <Button
              loading={save.isPending}
              disabled={!dirty}
              onClick={() => save.mutate({ yaml: text, etag: detail.etag })}
            >
              Save draft
            </Button>
          </div>
        )}
      </header>

      {dirty && !readOnlyReason && (
        <p className="text-xs text-muted">
          Unsaved changes. Save the draft first: validating checks the saved
          text.
        </p>
      )}

      <PolicyWorkflowBar policy={detail} role={role} dirty={dirty} />

      {readOnlyReason && (
        <p className="rounded-lg border border-line p-3 text-muted">
          Read only. {readOnlyReason}
        </p>
      )}

      <div
        role="tablist"
        aria-label="Policy sections"
        className="flex gap-1 border-b border-line"
      >
        {TABS.map(({ id: tabId, label }) => (
          <button
            key={tabId}
            type="button"
            role="tab"
            id={`tab-${tabId}`}
            aria-selected={tab === tabId}
            aria-controls="policy-panel"
            onClick={() => setTab(tabId)}
            className={`-mb-px border-b-2 px-4 py-2 font-medium ${
              tab === tabId
                ? "border-accent text-fg"
                : "border-transparent text-muted hover:text-fg"
            }`}
          >
            {label}
          </button>
        ))}
      </div>

      {tab === "versions" && (
        <div role="tabpanel" id="policy-panel" aria-labelledby="tab-versions">
          <PolicyVersionsPanel policyId={detail.id} role={role} />
        </div>
      )}

      {tab === "test" && (
        <div role="tabpanel" id="policy-panel" aria-labelledby="tab-test">
          <PolicyTestPanel policyId={detail.id} dirty={dirty} />
        </div>
      )}

      {tab === "editor" && (
        <div
          role="tabpanel"
          id="policy-panel"
          aria-labelledby="tab-editor"
          className="space-y-4"
        >
          <Suspense
            fallback={<div className="h-96 animate-pulse rounded-xl bg-line" />}
          >
            <PolicyEditor
              value={text}
              onChange={(value) => {
                setEdited(value);
                validate.reset(); // the old result describes text that no longer exists
              }}
              readOnly={readOnlyReason !== null}
              issues={result?.errors}
            />
          </Suspense>

          {validate.isError && (
            <p
              role="alert"
              className="rounded-xl border border-danger p-4 text-danger"
            >
              {describeApiError(validate.error)}
            </p>
          )}

          {result && (
            <section
              aria-live="polite"
              className="space-y-3 rounded-xl border border-line bg-card p-4"
            >
              {result.valid ? (
                <p className="font-medium text-success">The policy is valid.</p>
              ) : (
                <>
                  <p className="font-medium text-danger">
                    {result.errors.length}{" "}
                    {result.errors.length === 1 ? "problem" : "problems"} found
                  </p>
                  <ul className="list-disc space-y-1 pl-5">
                    {result.errors.map((issue, index) => (
                      <li key={index}>{describeIssue(issue)}</li>
                    ))}
                  </ul>
                </>
              )}
              {result.warnings.length > 0 && (
                <div>
                  <h2 className="text-sm font-semibold">Warnings</h2>
                  <ul className="list-disc space-y-1 pl-5 text-muted">
                    {result.warnings.map((warning, index) => (
                      <li key={index}>{warning}</li>
                    ))}
                  </ul>
                </div>
              )}
            </section>
          )}

          {save.isError && (
            <div role="alert" className="rounded-xl border border-danger p-4">
              <p className="text-danger">{describeApiError(save.error)}</p>
              {conflict && (
                <Button
                  className="mt-3"
                  variant="secondary"
                  onClick={() => {
                    setEdited(null);
                    save.reset();
                    void policy.refetch();
                  }}
                >
                  Discard my changes and reload
                </Button>
              )}
            </div>
          )}
        </div>
      )}
    </main>
  );
}
