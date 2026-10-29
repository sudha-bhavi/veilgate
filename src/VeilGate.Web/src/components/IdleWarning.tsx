import Button from "./Button";

export default function IdleWarning({ onStay }: { onStay: () => void }) {
  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/50 p-4">
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="idle-title"
        aria-describedby="idle-body"
        className="w-full max-w-sm rounded-xl border border-line bg-surface p-5 shadow-lg"
      >
        <h2 id="idle-title" className="text-base font-semibold">
          Still there?
        </h2>
        <p id="idle-body" className="mt-2 text-muted">
          For your security you will be signed out in about a minute because of
          inactivity.
        </p>
        <Button autoFocus className="mt-4" onClick={onStay}>
          Stay signed in
        </Button>
      </div>
    </div>
  );
}
