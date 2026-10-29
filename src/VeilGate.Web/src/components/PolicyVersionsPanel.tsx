import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { canReviewPolicies } from "../auth/policyAccess";
import { useStepUpMutation } from "../auth/useStepUpMutation";
import { describeApiError } from "../lib/apiErrorText";
import { classifyDiffLine, type DiffKind } from "../lib/diffLines";
import {
  getDiff,
  listVersions,
  rollbackPolicy,
  type PolicyVersionItem,
} from "../lib/policiesApi";
import { showToast } from "../lib/toast";
import Button from "./Button";
import ConfirmDialog from "./ConfirmDialog";
import PolicyStatusBadge from "./PolicyStatusBadge";

const tone: Record<DiffKind, string> = {
  add: "text-success",
  remove: "text-danger",
  hunk: "text-accent",
  meta: "text-muted",
  context: "text-fg",
};

const field =
  "rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent";

export default function PolicyVersionsPanel({
  policyId,
  role,
}: {
  policyId: string;
  role: string;
}) {
  const queryClient = useQueryClient();
  const versions = useQuery({
    queryKey: ["policy-versions", policyId],
    queryFn: () => listVersions(policyId),
  });

  const items = [...(versions.data?.items ?? [])].sort(
    (a, b) => b.versionNo - a.versionNo,
  );
  const [from, setFrom] = useState<number | null>(null);
  const [to, setTo] = useState<number | null>(null);
  const fromNo = from ?? items[1]?.versionNo ?? null; // by default compare the two newest
  const toNo = to ?? items[0]?.versionNo ?? null;

  const [shown, setShown] = useState<{ from: number; to: number } | null>(null);
  const diff = useQuery({
    queryKey: ["policy-diff", policyId, shown?.from, shown?.to],
    queryFn: () => getDiff(policyId, shown?.from ?? 0, shown?.to ?? 0),
    enabled: shown !== null,
  });

  const [target, setTarget] = useState<PolicyVersionItem | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const { mutation: rollback, stepUpDialog } = useStepUpMutation({
    mutationFn: (versionId: string) => rollbackPolicy(policyId, versionId),
    onSuccess: ({ versionNumber }) => {
      setTarget(null);
      setFailure(null);
      showToast(`Rolled back. Published version ${versionNumber}.`, "success");
      void queryClient.invalidateQueries({ queryKey: ["policy", policyId] });
      void queryClient.invalidateQueries({
        queryKey: ["policy-versions", policyId],
      });
      void queryClient.invalidateQueries({ queryKey: ["policies"] });
    },
    onFailure: (error) => setFailure(describeApiError(error)),
  });

  const canRollBack = canReviewPolicies(role);

  if (versions.isPending) {
    return <div className="h-48 animate-pulse rounded-xl bg-line" />;
  }
  if (versions.isError) {
    return (
      <p role="alert" className="text-danger">
        {describeApiError(versions.error)}
      </p>
    );
  }

  return (
    <div className="space-y-6">
      <div className="overflow-x-auto rounded-xl border border-line bg-card">
        <table className="w-full text-left">
          <caption className="sr-only">Versions</caption>
          <thead className="text-xs uppercase tracking-wide text-muted">
            <tr>
              <th className="px-4 py-2 font-medium">Version</th>
              <th className="px-4 py-2 font-medium">State</th>
              <th className="px-4 py-2 font-medium">Summary</th>
              <th className="px-4 py-2 font-medium">Published</th>
              <th className="px-4 py-2" />
            </tr>
          </thead>
          <tbody>
            {items.map((version) => (
              <tr key={version.id} className="border-t border-line">
                <td className="px-4 py-3 font-medium">v{version.versionNo}</td>
                <td className="px-4 py-3">
                  <PolicyStatusBadge status={version.state} />
                </td>
                <td className="px-4 py-3 text-muted">{version.summary ?? "—"}</td>
                <td className="px-4 py-3 text-muted">
                  {version.publishedAt
                    ? new Date(version.publishedAt).toLocaleDateString()
                    : "—"}
                </td>
                <td className="px-4 py-3 text-right">
                  {canRollBack && version.state === "Archived" && (
                    <Button
                      variant="secondary"
                      onClick={() => {
                        setFailure(null);
                        setTarget(version);
                      }}
                    >
                      Roll back to v{version.versionNo}
                    </Button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {items.length >= 2 && (
        <section className="space-y-3">
          <h2 className="text-sm font-semibold">Compare two versions</h2>
          <div className="flex flex-wrap items-end gap-3">
            <div>
              <label htmlFor="diff-from" className="mb-1 block text-muted">
                From
              </label>
              <select
                id="diff-from"
                value={fromNo ?? ""}
                onChange={(event) => setFrom(Number(event.target.value))}
                className={field}
              >
                {items.map((v) => (
                  <option key={v.id} value={v.versionNo}>
                    v{v.versionNo}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label htmlFor="diff-to" className="mb-1 block text-muted">
                To
              </label>
              <select
                id="diff-to"
                value={toNo ?? ""}
                onChange={(event) => setTo(Number(event.target.value))}
                className={field}
              >
                {items.map((v) => (
                  <option key={v.id} value={v.versionNo}>
                    v{v.versionNo}
                  </option>
                ))}
              </select>
            </div>
            <Button
              variant="secondary"
              disabled={fromNo === null || toNo === null || fromNo === toNo}
              onClick={() =>
                fromNo !== null &&
                toNo !== null &&
                setShown({ from: fromNo, to: toNo })
              }
            >
              Show differences
            </Button>
          </div>

          {diff.isError && (
            <p role="alert" className="text-danger">
              {describeApiError(diff.error)}
            </p>
          )}
          {diff.data &&
            (diff.data.unifiedDiff.trim() === "" ? (
              <p className="text-muted">No differences.</p>
            ) : (
              <pre className="overflow-x-auto rounded-lg border border-line bg-bg p-3 font-mono text-[13px] leading-6">
                {diff.data.unifiedDiff.split("\n").map((line, index) => (
                  <span
                    key={index}
                    className={`block ${tone[classifyDiffLine(line)]}`}
                  >
                    {line || " "}
                  </span>
                ))}
              </pre>
            ))}
        </section>
      )}

      {target && (
        <ConfirmDialog
          title={`Roll back to version ${target.versionNo}?`}
          confirmLabel="Roll back"
          loading={rollback.isPending}
          error={failure}
          onConfirm={() => rollback.mutate(target.id)}
          onCancel={() => setTarget(null)}
        >
          <p className="text-muted">
            This publishes the text of version {target.versionNo} again as a new
            version. History is kept, and nothing is deleted. You may be asked
            to confirm your identity first.
          </p>
        </ConfirmDialog>
      )}

      {stepUpDialog}
    </div>
  );
}
