import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useStepUpMutation } from "../auth/useStepUpMutation";
import { getMfaStatus, regenerateRecoveryCodes } from "../lib/accountApi";
import { describeApiError } from "../lib/apiErrorText";
import { showToast } from "../lib/toast";
import Button from "./Button";
import ConfirmDialog from "./ConfirmDialog";
import RecoveryCodes from "./RecoveryCodes";

const QUERY_KEY = ["mfa-status"];

// Below this many unused codes the page warns the person to make new ones.
const LOW_CODES = 3;

/** The MFA tab: the authenticator app, passkeys (not available yet) and the recovery codes, with a way to replace them. */
export default function MfaPanel() {
  const queryClient = useQueryClient();
  const status = useQuery({ queryKey: QUERY_KEY, queryFn: getMfaStatus });

  const [confirming, setConfirming] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [fresh, setFresh] = useState<string[] | null>(null);
  const [saved, setSaved] = useState(false);

  const { mutation, stepUpDialog } = useStepUpMutation({
    mutationFn: regenerateRecoveryCodes,
    onSuccess: (codes) => {
      setConfirming(false);
      setSaved(false);
      setFresh(codes);
      void queryClient.invalidateQueries({ queryKey: QUERY_KEY });
      showToast("New recovery codes were created. The old ones no longer work.", "success");
    },
    onFailure: (error) => setFailure(describeApiError(error)),
  });

  const data = status.data;

  return (
    <section aria-labelledby="mfa-heading" className="space-y-6">
      <div>
        <h2 id="mfa-heading" className="text-lg font-semibold">
          Two-step sign-in
        </h2>
        <p className="mt-1 text-muted">How you prove it is you, after your password.</p>
      </div>

      {status.isError && (
        <div role="alert" className="rounded-xl border border-danger p-4">
          <p className="text-danger">{describeApiError(status.error)}</p>
          <Button className="mt-3" variant="secondary" onClick={() => status.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {status.isPending && <div className="h-24 animate-pulse rounded-xl bg-line" aria-label="Loading two-step sign-in" />}

      {data && (
        <dl className="divide-y divide-line rounded-xl border border-line bg-card">
          <div className="flex flex-wrap items-center justify-between gap-3 p-4">
            <div>
              <dt className="font-medium">Authenticator app</dt>
              <dd className="text-muted">
                {data.authenticator ? `Added ${new Date(data.authenticator.addedAt).toLocaleDateString()}` : "Not set up"}
              </dd>
            </div>
            <p className="text-sm text-muted">You must keep at least one second factor, so it cannot be removed here.</p>
          </div>

          <div className="p-4">
            <dt className="font-medium">Passkeys</dt>
            <dd className="text-muted">Not available yet.</dd>
          </div>

          <div className="flex flex-wrap items-center justify-between gap-3 p-4">
            <div>
              <dt className="font-medium">Recovery codes</dt>
              <dd className="text-muted">
                {data.recoveryCodesTotal === 0
                  ? "None"
                  : `${data.recoveryCodesLeft} of ${data.recoveryCodesTotal} left`}
                {data.recoveryCodesTotal > 0 && data.recoveryCodesLeft < LOW_CODES && (
                  <span className="ml-2 text-warning">Running low: make new ones.</span>
                )}
              </dd>
            </div>
            <Button
              variant="secondary"
              disabled={!data.authenticator}
              onClick={() => {
                setFailure(null);
                setConfirming(true);
              }}
            >
              Regenerate codes
            </Button>
          </div>
        </dl>
      )}

      {fresh && (
        <div role="region" aria-label="New recovery codes" className="space-y-4 rounded-xl border border-warning p-4">
          <p>
            Save these new recovery codes now. They are shown only once, each works a single time, and your old codes no
            longer work.
          </p>
          <RecoveryCodes codes={fresh} />
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={saved} onChange={(event) => setSaved(event.target.checked)} />I have saved these
            codes
          </label>
          <Button disabled={!saved} onClick={() => setFresh(null)}>
            Done
          </Button>
        </div>
      )}

      {confirming && (
        <ConfirmDialog
          title="Replace your recovery codes?"
          confirmLabel="Replace codes"
          danger
          loading={mutation.isPending}
          error={failure}
          onConfirm={() => {
            setFailure(null);
            mutation.mutate();
          }}
          onCancel={() => setConfirming(false)}
        >
          <p className="text-muted">
            You get 10 new codes. Every old code stops working at once, including ones you have not used. You may be asked
            to confirm your identity first.
          </p>
        </ConfirmDialog>
      )}
      {stepUpDialog}
    </section>
  );
}
