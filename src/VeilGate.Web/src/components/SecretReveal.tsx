import { useEffect, useId, useRef, useState } from "react";
import Button from "./Button";
import CopyButton from "./CopyButton";

type Props = {
  title: string;
  intro: string;
  rows: { label: string; value: string }[];
  onDone: () => void;
};

/**
 * Shows credentials that the server will never show again. The person has to say they stored them before moving on.
 * Nothing here is written to storage; leaving the page drops the secret from memory.
 * It takes focus when it appears, which also scrolls it into view: after a rotate the person is far down the page.
 */
export default function SecretReveal({ title, intro, rows, onDone }: Props) {
  const titleId = useId();
  const heading = useRef<HTMLHeadingElement>(null);
  const [stored, setStored] = useState(false);

  useEffect(() => heading.current?.focus(), []);

  return (
    <section
      aria-labelledby={titleId}
      className="space-y-4 rounded-xl border border-warning p-4"
    >
      <div>
        <h2
          id={titleId}
          ref={heading}
          tabIndex={-1}
          className="text-base font-semibold outline-none"
        >
          {title}
        </h2>
        <p className="mt-1 text-muted">{intro}</p>
      </div>

      {rows.map((row) => (
        <div key={row.label}>
          <p className="mb-1 text-muted">{row.label}</p>
          <div className="flex items-start gap-2">
            <code className="min-w-0 flex-1 break-all rounded-lg border border-line bg-bg px-3 py-2 font-mono text-[13px]">
              {row.value}
            </code>
            <CopyButton text={row.value} label={`Copy ${row.label.toLowerCase()}`} />
          </div>
        </div>
      ))}

      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          checked={stored}
          onChange={(event) => setStored(event.target.checked)}
        />
        I have stored the secret somewhere safe
      </label>
      <Button disabled={!stored} onClick={onDone}>
        Continue
      </Button>
    </section>
  );
}
