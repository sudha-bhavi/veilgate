import { useQuery } from "@tanstack/react-query";
import Badge from "../components/Badge";
import Button from "../components/Button";
import { describeApiError } from "../lib/apiErrorText";
import { getSettings } from "../lib/settingsApi";

function Rule({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div className="rounded-xl border border-line bg-card p-4">
      <dt className="text-muted">{label}</dt>
      <dd className="mt-1 text-lg font-semibold">{value}</dd>
      <p className="mt-1 text-xs text-muted">{hint}</p>
    </div>
  );
}

/** Read-only: the organisation and the rules that apply to it. Every rule is server-wide, so there is nothing to change here. */
export default function SettingsPage() {
  const settings = useQuery({ queryKey: ["settings"], queryFn: getSettings });
  const s = settings.data;

  return (
    <main className="mx-auto max-w-4xl space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold">Settings</h1>
        <p className="mt-1 text-muted">
          Your organisation and the rules that apply to it. These rules are set for the whole VeilGate server, so they are shown here
          but cannot be changed from the console.
        </p>
      </header>

      {settings.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(settings.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => settings.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {settings.isPending && <div className="h-24 animate-pulse rounded-xl bg-line" aria-label="Loading the settings" />}

      {s && (
        <>
          <section aria-labelledby="org-heading" className="rounded-xl border border-line bg-card p-4">
            <h2 id="org-heading" className="text-sm font-semibold">
              Organisation
            </h2>
            <p className="mt-2 flex flex-wrap items-center gap-3">
              <span className="text-lg font-semibold">{s.organisation.name}</span>
              <Badge tone={s.organisation.status === "Active" ? "success" : "danger"}>{s.organisation.status}</Badge>
            </p>
          </section>

          <section aria-labelledby="rules-heading" className="space-y-3">
            <h2 id="rules-heading" className="text-sm font-semibold">
              Sign-in and session rules
            </h2>
            <dl className="grid gap-4 sm:grid-cols-2">
              <Rule
                label="Two-step sign-in"
                value={s.rules.mfaRequired ? "Required for everyone" : "Optional"}
                hint="Every person sets up an authenticator app the first time they sign in."
              />
              <Rule
                label="Account lock"
                value={`${s.rules.lockoutAttempts} wrong tries, then ${s.rules.lockoutMinutes} minutes`}
                hint="Wrong passwords and wrong codes both count. A correct sign-in clears the count."
              />
              <Rule
                label="Idle sign-out"
                value={`${s.rules.sessionIdleHours} hours`}
                hint="A session with no activity for this long ends."
              />
              <Rule
                label="Longest session"
                value={`${s.rules.sessionMaxDays} days`}
                hint="After this, everyone signs in again, however active the session."
              />
              <Rule
                label="Access token lifetime"
                value={`${s.rules.accessTokenMinutes} minutes`}
                hint="Short on purpose; the console renews it quietly while the session lives."
              />
            </dl>
          </section>

          <p className="text-xs text-muted">
            Not here yet: changing the organisation's name, a default policy, and how long request history is kept (history is kept until
            removed; there is no automatic deletion).
          </p>
        </>
      )}
    </main>
  );
}
