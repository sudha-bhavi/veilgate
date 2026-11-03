import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useStepUpMutation } from "../auth/useStepUpMutation";
import { describeApiError } from "../lib/apiErrorText";
import { describeDevice } from "../lib/device";
import { listSessions, revokeSession, signOutOtherSessions, type SessionRow } from "../lib/securityApi";
import { showToast } from "../lib/toast";
import { timeAgo } from "../lib/timeAgo";
import { useNow } from "../lib/useNow";
import Button from "./Button";
import ConfirmDialog from "./ConfirmDialog";

const QUERY_KEY = ["auth-sessions"];

/** The Sessions tab of the Security page: where you are signed in, with the means to end the sessions you do not recognise. */
export default function SessionsPanel() {
  const queryClient = useQueryClient();
  const now = useNow(60_000);
  const [revoking, setRevoking] = useState<SessionRow | null>(null);
  const [confirmingAll, setConfirmingAll] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const sessions = useQuery({ queryKey: QUERY_KEY, queryFn: listSessions });
  const refresh = () => queryClient.invalidateQueries({ queryKey: QUERY_KEY });

  function close() {
    setRevoking(null);
    setConfirmingAll(false);
    setFailure(null);
  }

  const revoke = useMutation({
    mutationFn: (id: string) => revokeSession(id),
    onSuccess: () => {
      close();
      void refresh();
      showToast("The session was ended. That device can no longer renew its sign-in.", "success");
    },
    onError: (error) => {
      setFailure(describeApiError(error));
      void refresh(); // it may already be gone
    },
  });

  const { mutation: signOutAll, stepUpDialog } = useStepUpMutation({
    mutationFn: signOutOtherSessions,
    onSuccess: ({ ended }) => {
      close();
      void refresh();
      showToast(
        ended === 1 ? "1 other session was signed out." : `${ended} other sessions were signed out.`,
        "success",
      );
    },
    onFailure: (error) => setFailure(describeApiError(error)),
  });

  const rows = sessions.data ?? [];
  const others = rows.filter((row) => !row.current);

  return (
    <section aria-labelledby="sessions-heading" className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 id="sessions-heading" className="text-lg font-semibold">
            Active sessions
          </h2>
          <p className="mt-1 text-muted">Where you are signed in. End any session you do not recognise.</p>
        </div>
        <Button
          variant="secondary"
          disabled={others.length === 0}
          onClick={() => {
            setFailure(null);
            setConfirmingAll(true);
          }}
        >
          Sign out all other sessions
        </Button>
      </div>

      {sessions.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(sessions.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => sessions.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {sessions.isPending && (
        <div className="space-y-2" aria-label="Loading sessions">
          {[0, 1].map((n) => (
            <div key={n} className="h-12 animate-pulse rounded-lg bg-line" />
          ))}
        </div>
      )}

      {sessions.data && (
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">Active sessions</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">Device</th>
                <th className="px-4 py-2 font-medium">IP address</th>
                <th className="px-4 py-2 font-medium">Signed in</th>
                <th className="px-4 py-2 font-medium">Last active</th>
                <th className="px-4 py-2 font-medium">Action</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const device = describeDevice(row.userAgent);
                return (
                  <tr key={row.id} className="border-t border-line">
                    <td className="px-4 py-3 font-medium">
                      {device}
                      {row.current && <span className="ml-2 text-xs font-normal text-muted">(current)</span>}
                    </td>
                    <td className="px-4 py-3 text-muted">{row.ip ?? "Unknown"}</td>
                    <td className="px-4 py-3 text-muted">{new Date(row.createdAt).toLocaleString()}</td>
                    <td className="px-4 py-3 text-muted">{timeAgo(row.lastActiveAt, now)}</td>
                    <td className="px-4 py-3">
                      {!row.current && (
                        <button
                          type="button"
                          aria-label={`Revoke ${device}`}
                          onClick={() => {
                            setFailure(null);
                            setRevoking(row);
                          }}
                          className="rounded text-danger hover:underline focus-visible:outline-2 focus-visible:outline-accent"
                        >
                          Revoke
                        </button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {revoking && (
        <ConfirmDialog
          title="End this session?"
          confirmLabel="End session"
          danger
          loading={revoke.isPending}
          error={failure}
          onConfirm={() => {
            setFailure(null);
            revoke.mutate(revoking.id);
          }}
          onCancel={close}
        >
          <p className="text-muted">
            {describeDevice(revoking.userAgent)} will be signed out. Anyone using it has to sign in again.
          </p>
        </ConfirmDialog>
      )}

      {confirmingAll && (
        <ConfirmDialog
          title="Sign out all other sessions?"
          confirmLabel="Sign out other sessions"
          danger
          loading={signOutAll.isPending}
          error={failure}
          onConfirm={() => {
            setFailure(null);
            signOutAll.mutate();
          }}
          onCancel={close}
        >
          <p className="text-muted">
            Every device except this one is signed out. You may be asked to confirm your identity first.
          </p>
        </ConfirmDialog>
      )}
      {stepUpDialog}
    </section>
  );
}
