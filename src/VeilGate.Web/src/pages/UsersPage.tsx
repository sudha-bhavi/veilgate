import { useState, type FormEvent } from "react";
import { useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useSession } from "../auth/useSession";
import Badge, { type BadgeTone } from "../components/Badge";
import Button from "../components/Button";
import InviteUserDialog from "../components/InviteUserDialog";
import Pager from "../components/Pager";
import UserActionDialog, { type ActionOutcome } from "../components/UserActionDialog";
import { describeApiError } from "../lib/apiErrorText";
import { pageCount, readPage } from "../lib/paging";
import { showToast } from "../lib/toast";
import { ACTION_LABELS, actionsFor, type UserAction } from "../lib/userRules";
import {
  ASSIGNABLE_ROLES,
  listUsers,
  roleLabel,
  type InviteResult,
  type TenantUser,
  type UserStatus,
} from "../lib/usersApi";

const field =
  "rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

const STATUS_TONE: Record<UserStatus, BadgeTone> = {
  Active: "success",
  Invited: "warning",
  Locked: "warning",
  Disabled: "danger",
};

type Dialog =
  | { kind: "invite" }
  | { kind: "action"; action: UserAction; user: TenantUser };

export default function UsersPage() {
  const session = useSession();
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const role = params.get("role") ?? "";
  const status = params.get("status") ?? "";
  const q = params.get("q") ?? "";
  const page = readPage(params.get("page"));
  const [search, setSearch] = useState(q);
  const [dialog, setDialog] = useState<Dialog | null>(null);

  const users = useQuery({
    queryKey: ["users", { role, status, q, page }],
    queryFn: () => listUsers({ role, status, q, page }),
    placeholderData: keepPreviousData,
  });

  const selfId = session.status === "signedIn" ? session.claims.sub : "";
  const filtered = role !== "" || status !== "" || q !== "";

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

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["users"] });

  function invited(result: InviteResult) {
    setDialog(null);
    void refresh();
    if (result.emailSent) showToast(`Invitation sent to ${result.email}.`, "success");
    else
      showToast(
        `${result.email} was invited, but the email could not be sent. Use "Resend invite" to try again.`,
        "error",
      );
  }

  function finished(outcome: ActionOutcome) {
    setDialog(null);
    void refresh();
    showToast(outcome.message, outcome.kind);
  }

  const data = users.data;
  const pages = data ? pageCount(data.total, data.pageSize) : 1;

  return (
    <main className="mx-auto max-w-6xl space-y-6 p-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Users</h1>
          <p className="mt-1 text-muted">The people in your organisation and what they may do.</p>
        </div>
        <Button onClick={() => setDialog({ kind: "invite" })}>
          <Plus className="size-4" aria-hidden="true" />
          Invite user
        </Button>
      </header>

      <div className="flex flex-wrap items-end gap-3">
        <form onSubmit={submitSearch} className="flex items-end gap-2">
          <div>
            <label htmlFor="user-search" className="mb-1 block text-muted">
              Search
            </label>
            <input
              id="user-search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Name or email"
              className={field}
            />
          </div>
          <Button type="submit" variant="secondary">
            Search
          </Button>
        </form>
        <div>
          <label htmlFor="user-role" className="mb-1 block text-muted">
            Role
          </label>
          <select
            id="user-role"
            value={role}
            onChange={(event) => update({ role: event.target.value, page: "" })}
            className={field}
          >
            <option value="">All roles</option>
            {ASSIGNABLE_ROLES.map((r) => (
              <option key={r.value} value={r.value}>
                {r.label}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="user-status" className="mb-1 block text-muted">
            Status
          </label>
          <select
            id="user-status"
            value={status}
            onChange={(event) => update({ status: event.target.value, page: "" })}
            className={field}
          >
            <option value="">All statuses</option>
            <option value="Active">Active</option>
            <option value="Invited">Invited</option>
            <option value="Disabled">Disabled</option>
          </select>
        </div>
      </div>

      {users.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(users.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => users.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {users.isPending && (
        <div className="space-y-2" aria-label="Loading users">
          {[0, 1, 2].map((n) => (
            <div key={n} className="h-12 animate-pulse rounded-lg bg-line" />
          ))}
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="rounded-xl border border-line bg-card p-6 text-center">
          <p>{filtered ? "No people match these filters." : "No people yet."}</p>
        </div>
      )}

      {data && data.items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">Users</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">Name</th>
                <th className="px-4 py-2 font-medium">Email</th>
                <th className="px-4 py-2 font-medium">Role</th>
                <th className="px-4 py-2 font-medium">Two-step</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Last sign-in</th>
                <th className="px-4 py-2 font-medium">Actions</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((user) => {
                const actions = actionsFor(user, selfId);
                return (
                  <tr key={user.id} className="border-t border-line">
                    <td className="px-4 py-3 font-medium">
                      {user.name}
                      {user.id.toLowerCase() === selfId.toLowerCase() && (
                        <span className="ml-2 text-xs font-normal text-muted">(you)</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-muted">{user.email}</td>
                    <td className="px-4 py-3">{roleLabel(user.role)}</td>
                    <td className="px-4 py-3 text-muted">{user.hasMfa ? "On" : "Not set up"}</td>
                    <td className="px-4 py-3">
                      <Badge tone={STATUS_TONE[user.status]}>{user.status}</Badge>
                    </td>
                    <td className="px-4 py-3 text-muted">
                      {user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : "Never"}
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap gap-x-3 gap-y-1">
                        {actions.map((action) => (
                          <button
                            key={action}
                            type="button"
                            aria-label={`${ACTION_LABELS[action]} ${user.name}`}
                            onClick={() => setDialog({ kind: "action", action, user })}
                            className={`rounded hover:underline focus-visible:outline-2 focus-visible:outline-accent ${
                              action === "disable" || action === "reset" ? "text-danger" : "text-accent"
                            }`}
                          >
                            {ACTION_LABELS[action]}
                          </button>
                        ))}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {data && data.total > 0 && (
        <Pager
          page={page}
          pages={pages}
          summary={`${data.total} ${data.total === 1 ? "person" : "people"}`}
          onPage={(next) => update({ page: next > 1 ? String(next) : "" })}
        />
      )}

      {dialog?.kind === "invite" && (
        <InviteUserDialog onInvited={invited} onCancel={() => setDialog(null)} />
      )}
      {dialog?.kind === "action" && (
        <UserActionDialog
          user={dialog.user}
          action={dialog.action}
          onDone={finished}
          onCancel={() => setDialog(null)}
        />
      )}
    </main>
  );
}
