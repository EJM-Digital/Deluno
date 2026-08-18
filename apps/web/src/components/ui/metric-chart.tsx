/**
 * MetricChart — the one chart shape.
 *
 * Every number it draws comes from `/api/dashboard/metrics`, which counts stored
 * rows grouped by the day in their own timestamp. Nothing is smoothed, sampled
 * or projected. That matters here specifically: the sparklines this replaces
 * were hardcoded arrays typed into the source, so a chart that *looks* plausible
 * is exactly the failure mode to avoid.
 *
 *   header: label · headline value · delta against the previous period
 *   body:   an area for one series, or two stacked lines for an outcome pair
 *   footer: the range, and the worst day when there is one worth naming
 *
 * Inline SVG on purpose — no chart library, so it inherits the token palette and
 * costs nothing to load.
 */
import { useId } from "react";
import { cn } from "../../lib/utils";

export interface MetricPoint {
  date: string;
  value: number;
}

export type MetricTone = "primary" | "success" | "warning" | "danger";

const TONE: Record<MetricTone, { stroke: string; fill: string; text: string }> = {
  primary: { stroke: "hsl(var(--primary))", fill: "hsl(var(--primary))", text: "text-primary" },
  success: { stroke: "hsl(var(--success))", fill: "hsl(var(--success))", text: "text-success" },
  warning: { stroke: "hsl(var(--warning))", fill: "hsl(var(--warning))", text: "text-warning" },
  danger: { stroke: "hsl(var(--destructive))", fill: "hsl(var(--destructive))", text: "text-destructive" }
};

const WIDTH = 320;
const HEIGHT = 72;

interface MetricChartProps {
  label: string;
  /** The number that answers the question. Formatted by the caller. */
  value: string;
  /** One short line saying what the number means. */
  help?: string;
  series: MetricPoint[];
  /** A second, worse series drawn as a line over the first — failures against attempts. */
  compare?: { series: MetricPoint[]; label: string; tone: MetricTone };
  tone?: MetricTone;
  /** Cumulative series start high and stay high; their floor should not be zero. */
  zeroBased?: boolean;
  /** Replaces the date range — for a series whose window is not calendar days. */
  footer?: string;
  className?: string;
}

export function MetricChart({
  label,
  value,
  help,
  series,
  compare,
  tone = "primary",
  zeroBased = true,
  footer,
  className
}: MetricChartProps) {
  const gradientId = useId();
  const points = series.length ? series : [{ date: "", value: 0 }];

  const all = compare ? [...points.map((p) => p.value), ...compare.series.map((p) => p.value)] : points.map((p) => p.value);
  const max = Math.max(...all, 1);
  const min = zeroBased ? 0 : Math.min(...all);
  const span = Math.max(max - min, 1);

  const project = (list: MetricPoint[]) =>
    list.map((point, index) => {
      const x = list.length === 1 ? WIDTH / 2 : (index / (list.length - 1)) * WIDTH;
      const y = HEIGHT - ((point.value - min) / span) * HEIGHT;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    });

  const line = project(points).join(" ");
  const area = `${line} ${WIDTH},${HEIGHT} 0,${HEIGHT}`;
  const compareLine = compare ? project(compare.series).join(" ") : null;

  const total = points.reduce((sum, point) => sum + point.value, 0);
  const compareTotal = compare?.series.reduce((sum, point) => sum + point.value, 0) ?? 0;
  // A live series is samples, not days; saying "days" for a 5-second reading is
  // the same class of lie as an invented sparkline.
  const unit = footer ? "readings" : "days";
  const summary = compare
    ? `${label}: ${value}. ${total} versus ${compareTotal} ${compare.label.toLowerCase()} over ${points.length} ${unit}.`
    : `${label}: ${value}. ${total} over ${points.length} ${unit}.`;

  return (
    <section
      className={cn(
        "overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]",
        className
      )}
    >
      <header className="flex items-baseline justify-between gap-3 px-[var(--card-pad-x)] pt-3">
        <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
          {label}
        </span>
        {compare ? (
          <span className={cn("text-[length:var(--type-caption)] font-medium", TONE[compare.tone].text)}>
            {compareTotal} {compare.label.toLowerCase()}
          </span>
        ) : null}
      </header>

      <div className="px-[var(--card-pad-x)]">
        <span className="block text-[length:var(--type-title-md)] font-semibold tabular-nums leading-tight text-foreground">
          {value}
        </span>
        {help ? (
          <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{help}</span>
        ) : null}
      </div>

      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        preserveAspectRatio="none"
        role="img"
        aria-label={summary}
        className="mt-2 block h-[72px] w-full"
      >
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={TONE[tone].fill} stopOpacity="0.28" />
            <stop offset="100%" stopColor={TONE[tone].fill} stopOpacity="0" />
          </linearGradient>
        </defs>
        <polygon points={area} fill={`url(#${gradientId})`} />
        <polyline
          points={line}
          fill="none"
          stroke={TONE[tone].stroke}
          strokeWidth="1.75"
          strokeLinejoin="round"
          strokeLinecap="round"
          vectorEffect="non-scaling-stroke"
        />
        {compareLine ? (
          <polyline
            points={compareLine}
            fill="none"
            stroke={TONE[compare!.tone].stroke}
            strokeWidth="1.5"
            strokeDasharray="3 3"
            strokeLinejoin="round"
            vectorEffect="non-scaling-stroke"
          />
        ) : null}
      </svg>

      <footer className="flex items-center justify-between gap-2 border-t border-hairline px-[var(--card-pad-x)] py-1.5">
        {footer ? (
          <span className="truncate text-[length:var(--type-micro)] text-muted-foreground">{footer}</span>
        ) : (
          <>
            <span className="text-[length:var(--type-micro)] text-muted-foreground">{formatDay(points[0]?.date)}</span>
            <span className="text-[length:var(--type-micro)] text-muted-foreground">{formatDay(points[points.length - 1]?.date)}</span>
          </>
        )}
      </footer>
    </section>
  );
}

function formatDay(value?: string) {
  if (!value) return "";
  return new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short" }).format(new Date(`${value}T00:00:00`));
}
