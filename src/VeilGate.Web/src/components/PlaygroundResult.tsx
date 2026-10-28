import type { ReactNode } from "react";
import type { PlaygroundOutcome } from "../lib/playgroundApi";
import HighlightedText from "./HighlightedText";

function Stage({
  title,
  hint,
  children,
}: {
  title: string;
  hint: string;
  children: ReactNode;
}) {
  return (
    <section className="space-y-2">
      <div>
        <h3 className="text-sm font-semibold">{title}</h3>
        <p className="text-muted">{hint}</p>
      </div>
      {children}
    </section>
  );
}

function TextBlock({ text }: { text: string }) {
  return (
    <p className="whitespace-pre-wrap break-words rounded-lg border border-line bg-bg p-3 font-mono text-[13px] leading-6">
      {text || "(empty)"}
    </p>
  );
}

function EntityTable({
  detailLabel,
  rows,
}: {
  detailLabel: string;
  rows: { type: string; detail: string }[];
}) {
  if (rows.length === 0)
    return <p className="text-muted">Nothing sensitive was found.</p>;
  return (
    <table className="w-full text-left">
      <thead className="text-xs uppercase tracking-wide text-muted">
        <tr>
          <th className="py-1 pr-4 font-medium">Type</th>
          <th className="py-1 font-medium">{detailLabel}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row, index) => (
          <tr key={index} className="border-t border-line">
            <td className="py-1 pr-4">{row.type}</td>
            <td className="py-1">{row.detail}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export default function PlaygroundResult({
  outcome,
  originalText,
}: {
  outcome: PlaygroundOutcome;
  originalText: string;
}) {
  return (
    <div className="space-y-6">
      {outcome.mode === "detect" && (
        <>
          <Stage
            title="Found in your text"
            hint="Each highlighted piece is something VeilGate would protect."
          >
            <HighlightedText
              text={originalText}
              spans={outcome.data.entities}
            />
          </Stage>
          <EntityTable
            detailLabel="Confidence"
            rows={outcome.data.entities.map((e) => ({
              type: e.type,
              detail: `${Math.round(e.confidence * 100)}%`,
            }))}
          />
        </>
      )}

      {outcome.mode === "deidentify" && (
        <>
          <Stage
            title="Found in your text"
            hint="What the detectors picked up."
          >
            <HighlightedText
              text={originalText}
              spans={outcome.data.entities}
            />
          </Stage>
          <Stage title="Masked text" hint="This is what leaves VeilGate.">
            <TextBlock text={outcome.data.maskedText} />
          </Stage>
          <EntityTable
            detailLabel="Action"
            rows={outcome.data.entities.map((e) => ({
              type: e.type,
              detail: e.action,
            }))}
          />
        </>
      )}

      {outcome.mode === "chat" && (
        <>
          <Stage
            title="Sent to the model"
            hint={`${outcome.data.entitiesProtected} item(s) protected before the model saw your text.`}
          >
            <TextBlock text={outcome.data.maskedText} />
          </Stage>
          <Stage
            title="The model answered"
            hint="Still masked, exactly as the model wrote it."
          >
            <TextBlock text={outcome.data.llmResponseMasked} />
          </Stage>
          <Stage
            title="Returned to the caller"
            hint="The answer with the original values put back."
          >
            <TextBlock text={outcome.data.restoredResponse} />
          </Stage>
        </>
      )}

      <p className="text-xs text-muted">Request id: {outcome.data.requestId}</p>
    </div>
  );
}
