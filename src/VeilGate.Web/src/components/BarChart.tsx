import { pointLabel, type RangeKey, type SeriesPoint } from "../lib/dashboardApi";

const WIDTH = 600;
const HEIGHT = 160;
const GAP = 2;

/**
 * A simple bar chart in SVG. A point with no value (for example the latency of an hour with no calls) draws no bar.
 * The same numbers are also in a table for screen readers, because a picture alone does not carry them.
 */
export default function BarChart({
  points,
  range,
  label,
  unit = "",
}: {
  points: SeriesPoint[];
  range: RangeKey;
  label: string;
  unit?: string;
}) {
  const max = Math.max(1, ...points.map((point) => point.value ?? 0));
  const barWidth = points.length === 0 ? 0 : WIDTH / points.length;
  const total = points.reduce((sum, point) => sum + (point.value ?? 0), 0);

  return (
    <figure>
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={`${label}: ${points.length} points, highest ${max.toLocaleString()}${unit}`}
        className="h-40 w-full text-accent"
      >
        <line x1="0" y1={HEIGHT - 0.5} x2={WIDTH} y2={HEIGHT - 0.5} className="stroke-line" strokeWidth="1" />
        {points.map((point, index) => {
          if (point.value === null) return null;
          const height = (point.value / max) * (HEIGHT - 8);
          return (
            <rect
              key={point.t}
              x={index * barWidth + GAP / 2}
              y={HEIGHT - height}
              width={Math.max(1, barWidth - GAP)}
              height={height}
              rx="2"
              fill="currentColor"
            >
              <title>{`${pointLabel(point.t, range)}: ${point.value.toLocaleString()}${unit}`}</title>
            </rect>
          );
        })}
      </svg>
      {points.length > 0 && (
        <figcaption className="mt-1 flex justify-between text-xs text-muted">
          <span>{pointLabel(points[0].t, range)}</span>
          <span>{pointLabel(points[points.length - 1].t, range)}</span>
        </figcaption>
      )}
      <details className="mt-2">
        <summary className="cursor-pointer text-accent">Show as a table</summary>
        <table className="mt-2 w-full text-left">
          <caption className="sr-only">{label}</caption>
          <thead className="text-xs uppercase tracking-wide text-muted">
            <tr>
              <th className="py-1 pr-4 font-medium">{range === "24h" ? "Hour" : "Day"}</th>
              <th className="py-1 font-medium">{label}</th>
            </tr>
          </thead>
          <tbody>
            {points.map((point) => (
              <tr key={point.t} className="border-t border-line">
                <td className="py-1 pr-4 text-muted">{pointLabel(point.t, range)}</td>
                <td className="py-1">{point.value === null ? "–" : `${point.value.toLocaleString()}${unit}`}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="mt-1 text-xs text-muted">Total: {total.toLocaleString()}{unit}</p>
      </details>
    </figure>
  );
}
