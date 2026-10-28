import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { currentTheme, setTheme, type Theme } from "../lib/theme";

type SystemInfo = { name: string; version: string; environment: string };

async function fetchInfo(): Promise<SystemInfo> {
  const response = await fetch("/api/v1/system/info");
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}

export default function StatusPage() {
  const info = useQuery({ queryKey: ["system-info"], queryFn: fetchInfo });
  const [theme, setCurrent] = useState<Theme>(currentTheme);

  function toggleTheme() {
    const next: Theme = theme === "dark" ? "light" : "dark";
    setTheme(next);
    setCurrent(next);
  }

  return (
    <main className="mx-auto max-w-xl p-6">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">VeilGate console</h1>
        <button
          onClick={toggleTheme}
          className="rounded-lg border border-line px-3 py-1.5 text-muted hover:text-fg"
        >
          {theme === "dark" ? "Light theme" : "Dark theme"}
        </button>
      </div>

      <section className="mt-6 rounded-xl border border-line bg-card p-4">
        <h2 className="text-xs uppercase tracking-wide text-muted">
          API status
        </h2>

        {info.isPending && (
          <div className="mt-3 h-12 animate-pulse rounded-lg bg-line" />
        )}

        {info.isError && (
          <div className="mt-3">
            <p className="text-danger">
              Could not reach the API ({info.error.message}).
            </p>
            <button
              onClick={() => info.refetch()}
              className="mt-2 rounded-lg bg-brand px-3 py-1.5 text-white hover:bg-brand-hover"
            >
              Retry
            </button>
          </div>
        )}

        {info.isSuccess && (
          <dl className="mt-3 grid grid-cols-[8rem_1fr] gap-y-1">
            <dt className="text-muted">Name</dt>
            <dd>{info.data.name}</dd>
            <dt className="text-muted">Version</dt>
            <dd>{info.data.version}</dd>
            <dt className="text-muted">Environment</dt>
            <dd>{info.data.environment}</dd>
          </dl>
        )}
      </section>
    </main>
  );
}
