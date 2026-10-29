import { useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useSession } from "../auth/useSession";
import Badge from "../components/Badge";
import Button from "../components/Button";
import Pager from "../components/Pager";
import { listApiClients } from "../lib/apiClientsApi";
import { describeApiError } from "../lib/apiErrorText";
import { pageCount, readPage } from "../lib/paging";

const field =
  "rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

export default function ApiClientsPage() {
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const status = params.get("status") ?? "";
  const q = params.get("q") ?? "";
  const page = readPage(params.get("page"));
  const [search, setSearch] = useState(q);

  const clients = useQuery({
    queryKey: ["api-clients", { status, q, page }],
    queryFn: () => listApiClients({ status, q, page }),
    placeholderData: keepPreviousData,
  });

  const isDeveloper =
    session.status === "signedIn" && session.claims.role === "Developer";
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

  const data = clients.data;
  const pages = data ? pageCount(data.total, data.pageSize) : 1;

  return (
    <main className="mx-auto max-w-5xl space-y-6 p-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">API clients</h1>
          <p className="mt-1 text-muted">
            Applications that call the gateway with their own credentials.
            {isDeveloper && " You see the clients you created."}
          </p>
        </div>
        <Link
          to="/clients/new"
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-4 py-2 font-medium text-white hover:bg-brand-hover focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
        >
          <Plus className="size-4" aria-hidden="true" />
          New API client
        </Link>
      </header>

      <div className="flex flex-wrap items-end gap-3">
        <form onSubmit={submitSearch} className="flex items-end gap-2">
          <div>
            <label htmlFor="client-search" className="mb-1 block text-muted">
              Search
            </label>
            <input
              id="client-search"
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
          <label htmlFor="client-status" className="mb-1 block text-muted">
            Status
          </label>
          <select
            id="client-status"
            value={status}
            onChange={(event) => update({ status: event.target.value, page: "" })}
            className={field}
          >
            <option value="">All statuses</option>
            <option value="Active">Active</option>
            <option value="Revoked">Revoked</option>
          </select>
        </div>
      </div>

      {clients.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(clients.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => clients.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {clients.isPending && (
        <div className="space-y-2" aria-label="Loading API clients">
          {[0, 1, 2].map((n) => (
            <div key={n} className="h-12 animate-pulse rounded-lg bg-line" />
          ))}
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="rounded-xl border border-line bg-card p-6 text-center">
          <p>{filtered ? "No API clients match these filters." : "No API clients yet."}</p>
          {!filtered && (
            <Link to="/clients/new" className="mt-2 inline-block text-accent underline">
              Create the first API client
            </Link>
          )}
        </div>
      )}

      {data && data.items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">API clients</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">Name</th>
                <th className="px-4 py-2 font-medium">Client ID</th>
                <th className="px-4 py-2 font-medium">Permissions</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Per minute</th>
                <th className="px-4 py-2 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((client) => (
                <tr key={client.id} className="border-t border-line">
                  <td className="px-4 py-3">
                    <Link
                      to={`/clients/${client.id}`}
                      className="font-medium text-accent hover:underline"
                    >
                      {client.name}
                    </Link>
                  </td>
                  <td className="px-4 py-3 font-mono text-[13px] text-muted">
                    {client.clientId}
                  </td>
                  <td className="px-4 py-3 text-muted">
                    {client.scopes.map((s) => s.replace("gateway:", "")).join(", ")}
                  </td>
                  <td className="px-4 py-3">
                    <Badge tone={client.status === "Active" ? "success" : "danger"}>
                      {client.status}
                    </Badge>
                  </td>
                  <td className="px-4 py-3 text-muted">
                    {client.rateLimitPerMinute.toLocaleString()}
                  </td>
                  <td className="px-4 py-3 text-muted">
                    {new Date(client.createdAt).toLocaleDateString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.total > 0 && (
        <Pager
          page={page}
          pages={pages}
          summary={`${data.total} ${data.total === 1 ? "client" : "clients"}`}
          onPage={(next) => update({ page: next > 1 ? String(next) : "" })}
        />
      )}
    </main>
  );
}
