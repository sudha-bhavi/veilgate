import { Link, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { describeApiError } from "../lib/apiErrorText";
import {
  percent,
  signedPoints,
  verdict,
  type Verdict,
} from "../lib/evalFormat";
import {
  compareEvaluations,
  listEvaluations,
  listEvaluablePolicies,
  type MetricChange,
} from "../lib/evaluationsApi";

const words: Record<Verdict, { text: string; className: string }> = {
  better: { text: "Better", className: "text-success" },
  worse: { text: "Worse", className: "text-danger" },
  same: { text: "Same", className: "text-muted" },
};

function VerdictText({ value }: { value: Verdict }) {
  return <span className={`font-medium ${words[value].className}`}>{words[value].text}</span>;
}

export default function EvaluationComparePage() {
  const [params] = useSearchParams();
  const a = params.get("a") ?? "";
  const b = params.get("b") ?? "";

  const comparison = useQuery({
    queryKey: ["evaluation-compare", a, b],
    queryFn: () => compareEvaluations(a, b),
    enabled: a !== "" && b !== "",
  });
  // The two runs, to name them by policy and version.
  const runs = useQuery({
    queryKey: ["evaluations"],
    queryFn: () => listEvaluations(1, 20),
  });
  const policies = useQuery({
    queryKey: ["policies", "evaluable"],
    queryFn: listEvaluablePolicies,
  });

  const nameOf = (id: string): string => {
    const run = runs.data?.items.find((item) => item.id === id);
    if (!run) return "Run";
    const policy = policies.data?.find((item) => item.id === run.policyId)?.name ?? "Policy";
    return `${policy} v${run.policyVersion}`;
  };

  if (a === "" || b === "") {
    return (
      <main className="mx-auto max-w-4xl space-y-3 p-6">
        <p>Pick two finished runs on the Evaluations page to compare them.</p>
        <Link to="/evaluations" className="text-accent underline">
          Back to evaluations
        </Link>
      </main>
    );
  }

  if (comparison.isPending) {
    return (
      <main className="mx-auto max-w-4xl p-6">
        <div className="h-48 animate-pulse rounded-xl bg-line" />
      </main>
    );
  }

  if (comparison.isError) {
    return (
      <main className="mx-auto max-w-4xl space-y-3 p-6">
        <p role="alert" className="text-danger">
          {describeApiError(comparison.error)}
        </p>
        <Link to="/evaluations" className="text-accent underline">
          Back to evaluations
        </Link>
      </main>
    );
  }

  const c = comparison.data;
  const ratioRows: { label: string; change: MetricChange; betterWhen: "higher" | "lower" }[] = [
    { label: "Recall", change: c.recall, betterWhen: "higher" },
    { label: "Precision", change: c.precision, betterWhen: "higher" },
    { label: "F1 score", change: c.f1, betterWhen: "higher" },
    { label: "Leak rate", change: c.leakRate, betterWhen: "lower" },
  ];

  return (
    <main className="mx-auto max-w-4xl space-y-6 p-6">
      <Link to="/evaluations" className="text-muted hover:text-fg">
        ← Evaluations
      </Link>

      <header>
        <h1 className="text-xl font-semibold">Compare runs</h1>
        <p className="mt-1 text-muted">
          {nameOf(c.b)} against {nameOf(c.a)}, on the same dataset. “Better”
          means the second run did better than the first.
        </p>
      </header>

      <div className="overflow-x-auto rounded-xl border border-line bg-card">
        <table className="w-full text-left">
          <caption className="sr-only">Overall comparison</caption>
          <thead className="text-xs uppercase tracking-wide text-muted">
            <tr>
              <th className="px-4 py-2 font-medium">Measure</th>
              <th className="px-4 py-2 font-medium">First run</th>
              <th className="px-4 py-2 font-medium">Second run</th>
              <th className="px-4 py-2 font-medium">Change</th>
              <th className="px-4 py-2 font-medium">Verdict</th>
            </tr>
          </thead>
          <tbody>
            {ratioRows.map(({ label, change, betterWhen }) => (
              <tr key={label} className="border-t border-line">
                <td className="px-4 py-2 font-medium">{label}</td>
                <td className="px-4 py-2">{percent(change.a, label === "Leak rate" ? 2 : 1)}</td>
                <td className="px-4 py-2">{percent(change.b, label === "Leak rate" ? 2 : 1)}</td>
                <td className="px-4 py-2 text-muted">{signedPoints(change.delta)}</td>
                <td className="px-4 py-2">
                  <VerdictText value={verdict(change.delta, betterWhen)} />
                </td>
              </tr>
            ))}
            <tr className="border-t border-line">
              <td className="px-4 py-2 font-medium">Leaks</td>
              <td className="px-4 py-2">{c.leaksA}</td>
              <td className="px-4 py-2">{c.leaksB}</td>
              <td className="px-4 py-2 text-muted">
                {c.leaksB - c.leaksA > 0 ? "+" : ""}
                {c.leaksB - c.leaksA}
              </td>
              <td className="px-4 py-2">
                <VerdictText value={verdict(c.leaksB - c.leaksA, "lower")} />
              </td>
            </tr>
            <tr className="border-t border-line">
              <td className="px-4 py-2 font-medium">Recall target</td>
              <td className="px-4 py-2">{c.gateA ? "Passed" : "Missed"}</td>
              <td className="px-4 py-2">{c.gateB ? "Passed" : "Missed"}</td>
              <td className="px-4 py-2" />
              <td className="px-4 py-2" />
            </tr>
          </tbody>
        </table>
      </div>

      <section className="space-y-2">
        <h2 className="text-base font-semibold">By type</h2>
        <div className="overflow-x-auto rounded-xl border border-line bg-card">
          <table className="w-full text-left">
            <caption className="sr-only">Comparison by type of sensitive item</caption>
            <thead className="text-xs uppercase tracking-wide text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">Type</th>
                <th className="px-4 py-2 font-medium">Recall change</th>
                <th className="px-4 py-2 font-medium">Verdict</th>
                <th className="px-4 py-2 font-medium">Precision change</th>
                <th className="px-4 py-2 font-medium">Verdict</th>
              </tr>
            </thead>
            <tbody>
              {Object.entries(c.perType)
                .sort(([x], [y]) => x.localeCompare(y))
                .map(([type, change]) => (
                  <tr key={type} className="border-t border-line">
                    <td className="px-4 py-2 font-medium">{type}</td>
                    <td className="px-4 py-2 text-muted">{signedPoints(change.recall.delta)}</td>
                    <td className="px-4 py-2">
                      <VerdictText value={verdict(change.recall.delta, "higher")} />
                    </td>
                    <td className="px-4 py-2 text-muted">{signedPoints(change.precision.delta)}</td>
                    <td className="px-4 py-2">
                      <VerdictText value={verdict(change.precision.delta, "higher")} />
                    </td>
                  </tr>
                ))}
            </tbody>
          </table>
        </div>
      </section>
    </main>
  );
}
