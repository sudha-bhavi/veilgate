import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { describeApiError } from "../lib/apiErrorText";
import { testPolicy } from "../lib/policiesApi";
import Button from "./Button";
import HighlightedText from "./HighlightedText";

const SAMPLE =
  "Patient Jane Roe, born 03/14/1962, MRN 4481920, can be reached at 555-201-8890. She was seen at Riverside Clinic on 2026-09-02 for a follow-up.";

type Props = { policyId: string; dirty: boolean };

export default function PolicyTestPanel({ policyId, dirty }: Props) {
  const [text, setText] = useState("");
  const test = useMutation({
    mutationFn: (sample: string) => testPolicy(policyId, sample),
  });

  return (
    <div className="space-y-4">
      <p className="text-muted">
        Try the saved policy on made-up text. Nothing is stored and nothing is
        sent to a model. Dates show as [REDACTED] here.
      </p>
      {dirty && (
        <p className="text-xs text-muted">
          You have unsaved changes in the editor. This test uses the saved text,
          not those changes.
        </p>
      )}

      <div>
        <label htmlFor="policy-test-text" className="mb-1 block text-muted">
          Sample text
        </label>
        <textarea
          id="policy-test-text"
          rows={5}
          value={text}
          onChange={(event) => setText(event.target.value)}
          className="w-full rounded-lg border border-line bg-surface px-3 py-2 font-mono text-[13px] text-fg focus-visible:outline-2 focus-visible:outline-accent"
        />
      </div>

      <div className="flex gap-3">
        <Button
          loading={test.isPending}
          disabled={text.trim().length === 0}
          onClick={() => test.mutate(text)}
        >
          Run test
        </Button>
        <Button variant="secondary" onClick={() => setText(SAMPLE)}>
          Use sample text
        </Button>
      </div>

      <div aria-live="polite">
        {test.isError && (
          <p
            role="alert"
            className="rounded-xl border border-danger p-4 text-danger"
          >
            {describeApiError(test.error)}
          </p>
        )}

        {test.isSuccess && test.data.blocked && (
          <p role="alert" className="rounded-xl border border-warning p-4">
            The gateway would refuse this text. A rule says BLOCK for:{" "}
            {test.data.blockedTypes.join(", ")}.
          </p>
        )}

        {test.isSuccess && !test.data.blocked && (
          <div className="space-y-4 rounded-xl border border-line bg-card p-4">
            <div>
              <h3 className="text-sm font-semibold">Found in your text</h3>
              <HighlightedText text={test.variables} spans={test.data.entities} />
            </div>
            <div>
              <h3 className="text-sm font-semibold">Masked text</h3>
              <p className="whitespace-pre-wrap break-words rounded-lg border border-line bg-bg p-3 font-mono text-[13px] leading-6">
                {test.data.maskedText ?? ""}
              </p>
            </div>
            {test.data.entities.length === 0 ? (
              <p className="text-muted">Nothing sensitive was found.</p>
            ) : (
              <table className="w-full text-left">
                <thead className="text-xs uppercase tracking-wide text-muted">
                  <tr>
                    <th className="py-1 pr-4 font-medium">Type</th>
                    <th className="py-1 pr-4 font-medium">Action</th>
                    <th className="py-1 font-medium">Confidence</th>
                  </tr>
                </thead>
                <tbody>
                  {test.data.entities.map((entity, index) => (
                    <tr key={index} className="border-t border-line">
                      <td className="py-1 pr-4">{entity.type}</td>
                      <td className="py-1 pr-4">{entity.action}</td>
                      <td className="py-1">
                        {Math.round(entity.confidence * 100)}%
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
