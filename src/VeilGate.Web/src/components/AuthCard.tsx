import type { ReactNode } from "react";

/** The centered 420 px card every sign-in screen uses (doc 02 §2). The title is the first focusable heading. */
export default function AuthCard({
  title,
  children,
}: {
  title: string;
  children: ReactNode;
}) {
  return (
    <main className="flex min-h-screen items-center justify-center p-4">
      <div className="w-full max-w-[420px] rounded-xl border border-line bg-card p-6">
        <p className="mb-4 text-lg font-semibold text-accent">VeilGate</p>
        <h1 tabIndex={-1} className="mb-4 text-xl font-semibold outline-none">
          {title}
        </h1>
        {children}
      </div>
    </main>
  );
}
