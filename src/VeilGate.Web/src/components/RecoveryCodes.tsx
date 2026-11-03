import Button from "./Button";
import CopyButton from "./CopyButton";

function downloadCodes(codes: readonly string[]) {
  const text = `VeilGate recovery codes\nEach code works once.\n\n${codes.join("\n")}\n`;
  const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = "veilgate-recovery-codes.txt";
  link.click();
  URL.revokeObjectURL(url);
}

/** Recovery codes with the ways to keep them: download, copy, print. They are shown once, so the person must save them now. */
export default function RecoveryCodes({ codes }: { codes: readonly string[] }) {
  return (
    <div className="space-y-4">
      <ol aria-label="Recovery codes" className="grid grid-cols-2 gap-x-4 gap-y-1 font-mono">
        {codes.map((code) => (
          <li key={code}>{code}</li>
        ))}
      </ol>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" onClick={() => downloadCodes(codes)}>
          Download .txt
        </Button>
        <CopyButton text={codes.join("\n")} label="Copy all" />
        <Button variant="secondary" onClick={() => window.print()}>
          Print
        </Button>
      </div>
    </div>
  );
}
