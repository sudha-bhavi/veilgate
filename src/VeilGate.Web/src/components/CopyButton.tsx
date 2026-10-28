import { useState } from "react";
import Button from "./Button";

export default function CopyButton({
  text,
  label = "Copy",
}: {
  text: string;
  label?: string;
}) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // the browser blocked the clipboard: the text is still on screen to select
    }
  }

  return (
    <Button variant="secondary" onClick={copy}>
      {copied ? "Copied" : label}
    </Button>
  );
}
