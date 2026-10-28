import { toSegments, type Span } from "../lib/highlight";

export default function HighlightedText({
  text,
  spans,
}: {
  text: string;
  spans: readonly Span[];
}) {
  return (
    <p className="whitespace-pre-wrap break-words rounded-lg border border-line bg-bg p-3 font-mono text-[13px] leading-6">
      {toSegments(text, spans).map((segment, index) =>
        segment.type ? (
          <mark key={index} className="rounded bg-warning/30 px-0.5 text-fg">
            {segment.text}
            <sup className="ml-0.5 text-[10px] text-muted">{segment.type}</sup>
          </mark>
        ) : (
          <span key={index}>{segment.text}</span>
        ),
      )}
    </p>
  );
}
