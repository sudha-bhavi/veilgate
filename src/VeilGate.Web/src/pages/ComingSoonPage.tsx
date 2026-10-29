import Badge from "../components/Badge";
import type { ComingSoon } from "./comingSoon";

type Props = Omit<ComingSoon, "path">;

export default function ComingSoonPage({ title, summary, planned }: Props) {
  return (
    <main className="mx-auto max-w-2xl space-y-4 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">{title}</h1>
        <Badge tone="warning">Not built yet</Badge>
      </div>
      <p className="text-muted">{summary}</p>

      <section className="rounded-xl border border-line bg-card p-4">
        <h2 className="text-sm font-semibold">Planned</h2>
        <ul className="mt-2 list-disc space-y-1 pl-5 text-muted">
          {planned.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      </section>

      <p className="text-xs text-muted">
        The server side of this page is not built yet, so there is nothing to
        show. The Playground, Policies, API clients, Providers and Evaluations
        already work.
      </p>
    </main>
  );
}
