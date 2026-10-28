import { useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { canAuthorPolicies } from "../auth/policyAccess";
import { useSession } from "../auth/useSession";
import Button from "../components/Button";
import PolicyStatusBadge from "../components/PolicyStatusBadge";
import { describeApiError } from "../lib/apiErrorText";
import { pageCount, readPage } from "../lib/paging";
import { listPolicyPage } from "../lib/policiesApi";
import { POLICY_STATUSES, statusLabel } from "../lib/policyStatus";

const field =
  "rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

export default function PoliciesPage() {
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const status = params.get("status") ?? "";
  const q = params.get("q") ?? "";
  const page = readPage(params.get("page"));
  const [search, setSearch] = useState(q);

  const policies = useQuery({
    queryKey: ["policies", { status, q, page }],
    queryFn: () => listPolicyPage({ status, q, page }),
    placeholderData: keepPreviousData, // keep showing the old rows while the next page loads
  });

  const canCreate =
    session.status === "signedIn" && canAuthorPolicies(session.claims.role);
  const filtered = status !== "" || q !== "";

  function update(next: Record<string, string>) {
    const merged = new URLSearchParams(params);
    for (const [key, value] of Object.entries(next)) {
      if (value) merged.set(key, value);
      else merged.delete(key);
    }
    setParams(merged);
  }

  function submitSearch(event: FormEvent) {
    event.preventDefault();
    update({ q: search.trim(), page: "" });
  }

  const data = policies.data;
  const pages = data ? pageCount(data.total, data.pageSize) : 1;

  return (
    <main className="mx-auto max-w-5xl space-y-6 p-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Policies</h1>
          <p className="mt-1 text-muted">
            Rules for what VeilGate hides, and how, before text reaches a model.
          </p>
        </div>
        {canCreate && (
          <Link
            to="/policies/new"
            className="inline-flex items-center gap-2 rounded-lg bg-brand px-4 py-2 font-medium text-white hover:bg-brand-hover focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
          >
            <Plus className="size-4" aria-hidden="true" />
            New policy
          </Link>
        )}
      </header>

      <div className="flex flex-wrap items-end gap-3">
        <form onSubmit={submitSearch} className="flex items-end gap-2">
          <div>
            <label htmlFor="pol-search" className="mb-1 block text-muted">
              Search
            </label>
            <input
              id="pol-search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              className={field}
            />
          </div>
          <Button type="submit" variant="secondary">
            Search
          </Button>
        </form>
        <div>
          <label htmlFor="pol-status" className="mb-1 block text-muted">
            Status
          </label>
          <select
            id="pol-status"
            value={status}
            onChange={(event) =>
              update({ status: event.target.value, page: "" })
            }
            className={field}
          >
            <option value="">All statuses</option>
            {POLICY_STATUSES.map((value) => (
              <option key={value} value={value}>
                {statusLabel(value)}
              </option>
            ))}
          </select>
        </div>
      </div>

      {policies.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(policies.error)}</p>
          <Button
            className="mt-3"
            variant="secondary"
            onClick={() => policies.refetch()}
          >
            Retry
          </Button>
        </div>
      )}

      {policies.isPending && (
        <div className="space-y-2" aria-label="Loading policies">
          {[0, 1, 2].map((n) => (
            <div key={n} className="h-12 animate-pulse rounded-lg bg-line" />
          ))}
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="rounded-xl border border-line bg-card p-6 text-center">
          <p>
            {filtered ? "No policies match these filters." : "No policies yet."}
          </p>
          {!filtered && canCreate && (
            <Link
              to="/policies/new"
              className="mt-2 inline-block text-accent underline"
            >
              Create the first policy
            </Link>
          )}
        </div>
      )}

      {data && data.items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">Policies</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">Name</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Version</th>
                <th className="px-4 py-2 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((policy) => (
                <tr key={policy.id} className="border-t border-line">
                  <td className="px-4 py-3">
                    <Link
                      to={`/policies/${policy.id}`}
                      className="font-medium text-accent hover:underline"
                    >
                      {policy.name}
                    </Link>
                    {policy.hasDraft && (
                      <span className="ml-2 text-xs text-muted">
                        has a draft
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    <PolicyStatusBadge status={policy.status} />
                  </td>
                  <td className="px-4 py-3 text-muted">
                    {policy.currentVersion === null
                      ? "—"
                      : `v${policy.currentVersion}`}
                  </td>
                  <td className="px-4 py-3 text-muted">
                    {new Date(policy.createdAt).toLocaleDateString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.total > 0 && (
        <nav aria-label="Pages" className="flex items-center justify-between">
          <p className="text-muted">
            {data.total} {data.total === 1 ? "policy" : "policies"} · page{" "}
            {page} of {pages}
          </p>
          <div className="flex gap-2">
            <Button
              variant="secondary"
              disabled={page <= 1}
              onClick={() =>
                update({ page: page - 1 > 1 ? String(page - 1) : "" })
              }
            >
              Previous
            </Button>
            <Button
              variant="secondary"
              disabled={page >= pages}
              onClick={() => update({ page: String(page + 1) })}
            >
              Next
            </Button>
          </div>
        </nav>
      )}
    </main>
  );
}
