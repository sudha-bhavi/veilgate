import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { workflowActions } from "../auth/policyWorkflow";
import { useStepUpMutation } from "../auth/useStepUpMutation";
import { describeApiError } from "../lib/apiErrorText";
import {
  archivePolicy,
  publishPolicy,
  rejectPolicy,
  submitPolicy,
  type PolicyDetail,
} from "../lib/policiesApi";
import { showToast } from "../lib/toast";
import Button from "./Button";
import ConfirmDialog from "./ConfirmDialog";

type DialogName = "submit" | "publish" | "reject" | "archive";

const textarea =
  "w-full rounded-lg border border-line bg-bg px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

type Props = { policy: PolicyDetail; role: string; dirty: boolean };

export default function PolicyWorkflowBar({ policy, role, dirty }: Props) {
  const queryClient = useQueryClient();
  const actions = workflowActions(policy, role);
  const [dialog, setDialog] = useState<DialogName | null>(null);
  const [text, setText] = useState(""); // the summary or the reason, whichever dialog is open
  const [failure, setFailure] = useState<string | null>(null);

  function open(name: DialogName) {
    setText("");
    setFailure(null);
    setDialog(name);
  }

  function finished(message: string) {
    setDialog(null);
    setFailure(null);
    showToast(message, "success");
    void queryClient.invalidateQueries({ queryKey: ["policy", policy.id] });
    void queryClient.invalidateQueries({ queryKey: ["policies"] });
  }

  const fail = (error: unknown) => setFailure(describeApiError(error));

  const submit = useMutation({
    mutationFn: (summary: string) => submitPolicy(policy.id, summary),
    onSuccess: () => finished("Submitted for approval."),
    onError: fail,
  });
  const reject = useMutation({
    mutationFn: (reason: string) => rejectPolicy(policy.id, reason),
    onSuccess: () => finished("Draft rejected."),
    onError: fail,
  });
  const archive = useMutation({
    mutationFn: () => archivePolicy(policy.id),
    onSuccess: () => finished("Policy archived."),
    onError: fail,
  });
  const { mutation: publish, stepUpDialog } = useStepUpMutation({
    mutationFn: () => publishPolicy(policy.id),
    onSuccess: ({ versionNumber }) =>
      finished(`Published version ${versionNumber}.`),
    onFailure: fail,
  });

  if (!Object.values(actions).some(Boolean)) return null;

  const trimmed = text.trim();

  return (
    <section
      aria-label="Approval workflow"
      className="space-y-3 rounded-xl border border-line bg-card p-4"
    >
      {actions.waitingForReview && (
        <p>This draft is waiting for approval by a Security Officer.</p>
      )}
      {actions.publish && (
        <p>
          Version {policy.draft?.versionNo} is waiting for your decision.
          {policy.draft?.summary && (
            <> The author wrote: “{policy.draft.summary}”</>
          )}
        </p>
      )}

      <div className="flex flex-wrap items-center gap-3">
        {actions.submit && (
          <Button disabled={dirty} onClick={() => open("submit")}>
            Submit for approval
          </Button>
        )}
        {actions.publish && (
          <Button onClick={() => open("publish")}>Publish</Button>
        )}
        {actions.reject && (
          <Button variant="danger" onClick={() => open("reject")}>
            Reject
          </Button>
        )}
        {actions.archive && (
          <Button variant="secondary" onClick={() => open("archive")}>
            Archive
          </Button>
        )}
        {actions.submit && dirty && (
          <span className="text-xs text-muted">
            Save your draft before submitting it.
          </span>
        )}
      </div>

      {dialog === "submit" && (
        <ConfirmDialog
          title="Submit for approval"
          confirmLabel="Submit"
          loading={submit.isPending}
          confirmDisabled={trimmed.length < 10 || trimmed.length > 500}
          error={failure}
          onConfirm={() => submit.mutate(trimmed)}
          onCancel={() => setDialog(null)}
        >
          <label htmlFor="wf-text" className="block text-muted">
            What changed, and why? (10 to 500 characters)
          </label>
          <textarea
            id="wf-text"
            autoFocus
            rows={4}
            value={text}
            onChange={(event) => setText(event.target.value)}
            className={textarea}
          />
          <p className="text-xs text-muted">{trimmed.length} / 500</p>
        </ConfirmDialog>
      )}

      {dialog === "publish" && (
        <ConfirmDialog
          title={`Publish version ${policy.draft?.versionNo}?`}
          confirmLabel="Publish now"
          loading={publish.isPending}
          error={failure}
          onConfirm={() => {
            setFailure(null);
            publish.mutate();
          }}
          onCancel={() => setDialog(null)}
        >
          <p className="text-muted">
            It takes effect right away for every request that uses this policy.
            You may be asked to confirm your identity first. The person who
            submitted a draft cannot publish it.
          </p>
        </ConfirmDialog>
      )}

      {dialog === "reject" && (
        <ConfirmDialog
          title="Reject this draft"
          confirmLabel="Reject"
          danger
          loading={reject.isPending}
          confirmDisabled={trimmed.length === 0 || trimmed.length > 500}
          error={failure}
          onConfirm={() => reject.mutate(trimmed)}
          onCancel={() => setDialog(null)}
        >
          <label htmlFor="wf-text" className="block text-muted">
            Why is it being rejected? (up to 500 characters)
          </label>
          <textarea
            id="wf-text"
            autoFocus
            rows={4}
            value={text}
            onChange={(event) => setText(event.target.value)}
            className={textarea}
          />
        </ConfirmDialog>
      )}

      {dialog === "archive" && (
        <ConfirmDialog
          title="Archive this policy?"
          confirmLabel="Archive"
          danger
          loading={archive.isPending}
          error={failure}
          onConfirm={() => archive.mutate()}
          onCancel={() => setDialog(null)}
        >
          <p className="text-muted">An archived policy can no longer be changed.</p>
        </ConfirmDialog>
      )}

      {stepUpDialog}
    </section>
  );
}
