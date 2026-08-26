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
import { useId, useRef, useState } from "react";
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
/** A chart with room to read: gridlines, a labelled peak, and space for shape. */
const HEIGHT_LG = 132;

interface MetricChartProps {
  label: string;
  /** The number that answers the question. Formatted by the caller. */
  value: string;
  /** One short line saying what the number means. */
  help?: string;
  series: MetricPoint[];
  /**
   * A second series drawn as a line over the first — failures against attempts,
   * or upload against download.
   *
   * `value` replaces the header's running total for a series where a total
   * means nothing: adding up speed readings gives a number in no unit at all,
   * so a speed comparison states its own reading instead.
   */
  compare?: { series: MetricPoint[]; label: string; tone: MetricTone; value?: string };
  tone?: MetricTone;
  /** Cumulative series start high and stay high; their floor should not be zero. */
  zeroBased?: boolean;
  /** Replaces the date range — for a series whose window is not calendar days. */
  footer?: string;
  /** What to say when the whole window is zero. Defaults to "No {label} in the last N days". */
  emptyLabel?: string;
  /**
   * Day series carry `yyyy-MM-dd`; a live-sampled series carries full
   * timestamps and is read in hours and minutes. The axis decides how a point's
   * date is parsed and printed — getting this wrong silently renders every
   * label as "Invalid Date".
   */
  axis?: "day" | "time";
  /**
   * How a single reading is written in the hover readout. `MetricPoint.value`
   * is an int, so a series in a fractional unit has to be scaled before it gets
   * here; without this the readout would print the scaled number and quietly
   * report 12.5 MB/s as "125".
   */
  formatValue?: (value: number) => string;
  /**
   * Taller, with gridlines and the peak called out. A sparkline says "roughly
   * this shape"; a reader asking how many and when needs an axis to read
   * against.
   */
  size?: "sm" | "lg";
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
  emptyLabel,
  axis = "day",
  formatValue,
  size = "sm",
  className
}: MetricChartProps) {
  const readValue = formatValue ?? ((reading: number) => reading.toLocaleString());
  const formatPoint = axis === "time" ? formatTime : formatDay;
  const unitNoun = axis === "time" ? "readings" : "days";
  const gradientId = useId();
  const height = size === "lg" ? HEIGHT_LG : HEIGHT;
  // Which reading the pointer or keyboard is currently on. A chart you cannot
  // interrogate is decoration: the shape tells you roughly what happened, and
  // this tells you what happened on a given day.
  const [activeIndex, setActiveIndex] = useState<number | null>(null);
  const plotRef = useRef<HTMLDivElement | null>(null);
  const points = series.length ? series : [{ date: "", value: 0 }];

  const all = compare ? [...points.map((p) => p.value), ...compare.series.map((p) => p.value)] : points.map((p) => p.value);
  const max = Math.max(...all, 1);
  const min = zeroBased ? 0 : Math.min(...all);
  const span = Math.max(max - min, 1);

  const project = (list: MetricPoint[]) =>
    list.map((point, index) => {
      const x = list.length === 1 ? WIDTH / 2 : (index / (list.length - 1)) * WIDTH;
      const y = height - ((point.value - min) / span) * height;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    });

  const projected = project(points);
  const line = projected.join(" ");
  const area = `${line} ${WIDTH},${height} 0,${height}`;
  const compareLine = compare ? project(compare.series).join(" ") : null;

  // Two different situations used to collapse into "draw nothing", which left
  // cards at wildly different heights in the same row — 109px beside 250px —
  // and made a quiet metric look like a broken one.
  //
  //   · too few readings  → there is genuinely no shape to draw yet, so the
  //     plot area says so. (The original #262 complaint: two points drew a
  //     hairline with a spike against the right edge and read as broken.)
  //   · a full window that never moved → that IS the shape. A flat line across
  //     thirty days is an honest answer, and nothing like a spike.
  //
  // Either way the plot area is always reserved, so every chart card is the
  // same height as its neighbours.
  const hasEnoughReadings = points.length >= 3;
  // A window where nothing ever happened draws a flat line hard against the
  // bottom of an otherwise empty box — accurate, and completely dead to look
  // at. Saying it in words is the same fact, legible, and the card keeps its
  // height either way.
  const nothingHappened =
    points.every((point) => point.value === 0) &&
    (!compare || compare.series.every((point) => point.value === 0));
  const [lastX, lastY] = (projected.at(-1) ?? "0,0").split(",").map(Number);
  const active = activeIndex === null ? null : points[activeIndex] ?? null;
  const compareActive = activeIndex === null ? null : compare?.series[activeIndex] ?? null;
  const [activeX, activeY] = (activeIndex === null ? "0,0" : projected[activeIndex] ?? "0,0").split(",").map(Number);

  const total = points.reduce((sum, point) => sum + point.value, 0);
  const compareTotal = compare?.series.reduce((sum, point) => sum + point.value, 0) ?? 0;
  // A live series is samples, not days; saying "days" for a 5-second reading is
  // the same class of lie as an invented sparkline.
  const unit = footer ? "readings" : unitNoun;
  const summary = compare
    ? `${label}: ${value}. ${total} versus ${compareTotal} ${compare.label.toLowerCase()} over ${points.length} ${unit}.`
    : `${label}: ${value}. ${total} over ${points.length} ${unit}.`;

  return (
    <section
      className={cn(
        "flex h-full flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]",
        className
      )}
    >
      <header className="flex items-baseline justify-between gap-3 px-[var(--card-pad-x)] pt-3">
        <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
          {label}
        </span>
        {compare ? (
          // Zero failures is good news, so it is not painted in an alarm
          // colour. Three red zeros across a healthy row read as three
          // problems at a glance, which is the opposite of what they mean.
          <span
            className={cn(
              "text-[length:var(--type-caption)] font-medium",
              // A stated value is a legend for the second line, so it always
              // carries that line's colour. A running total only earns it when
              // there is actually something to report.
              compare.value !== undefined || compareTotal > 0 ? TONE[compare.tone].text : "text-muted-foreground"
            )}
          >
            {compare.value ?? `${compareTotal} ${compare.label.toLowerCase()}`}
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

      {hasEnoughReadings && !nothingHappened ? (
        <div
          ref={plotRef}
          className="relative mt-2 cursor-crosshair focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
          tabIndex={0}
          role="group"
          aria-label={`${label} readings. Use the arrow keys to step through days.`}
          onPointerMove={(event) => {
            const box = plotRef.current?.getBoundingClientRect();
            if (!box || box.width === 0) return;
            const fraction = (event.clientX - box.left) / box.width;
            setActiveIndex(Math.min(points.length - 1, Math.max(0, Math.round(fraction * (points.length - 1)))));
          }}
          onPointerLeave={() => setActiveIndex(null)}
          onBlur={() => setActiveIndex(null)}
          onKeyDown={(event) => {
            if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
            event.preventDefault();
            setActiveIndex((current) => {
              const from = current ?? points.length - 1;
              const next = event.key === "ArrowLeft" ? from - 1 : from + 1;
              return Math.min(points.length - 1, Math.max(0, next));
            });
          }}
        >
          <svg
            viewBox={`0 0 ${WIDTH} ${height}`}
            preserveAspectRatio="none"
            role="img"
            aria-label={summary}
            className="block w-full"
            style={{ height }}
          >
            <defs>
              <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={TONE[tone].fill} stopOpacity="0.28" />
                <stop offset="100%" stopColor={TONE[tone].fill} stopOpacity="0" />
              </linearGradient>
            </defs>
            {size === "lg" ? (
              // Quarter lines, so a value can be read against the peak rather
              // than guessed from the shape.
              <g aria-hidden>
                {[0.25, 0.5, 0.75].map((fraction) => (
                  <line
                    key={fraction}
                    x1="0"
                    x2={WIDTH}
                    y1={height * fraction}
                    y2={height * fraction}
                    stroke="hsl(var(--hairline))"
                    strokeWidth="1"
                    vectorEffect="non-scaling-stroke"
                  />
                ))}
              </g>
            ) : null}
            <polygon points={area} fill={`url(#${gradientId})`} className="metric-chart-area" />
            <polyline
              points={line}
              fill="none"
              stroke={TONE[tone].stroke}
              strokeWidth="1.75"
              strokeLinejoin="round"
              strokeLinecap="round"
              vectorEffect="non-scaling-stroke"
              // Drawn on, left to right, once. A chart that appears fully
              // formed reads as a picture; one that draws reads as a reading.
              className="metric-chart-line"
              pathLength={1}
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
          {/*
            The latest reading, marked. Positioned in HTML rather than as an
            SVG circle because the chart scales non-uniformly, which would
            squash a circle into an ellipse.
          */}
          <span
            aria-hidden
            className="pointer-events-none absolute h-1.5 w-1.5 -translate-x-1/2 -translate-y-1/2 rounded-full"
            style={{
              left: `${(lastX / WIDTH) * 100}%`,
              top: `${(lastY / height) * 100}%`,
              backgroundColor: TONE[tone].stroke
            }}
          />

          {active ? (
            <>
              <span
                aria-hidden
                className="pointer-events-none absolute inset-y-0 w-px bg-foreground/25"
                style={{ left: `${(activeX / WIDTH) * 100}%` }}
              />
              <span
                aria-hidden
                className="pointer-events-none absolute h-2 w-2 -translate-x-1/2 -translate-y-1/2 rounded-full ring-2 ring-card"
                style={{
                  left: `${(activeX / WIDTH) * 100}%`,
                  top: `${(activeY / height) * 100}%`,
                  backgroundColor: TONE[tone].stroke
                }}
              />
              {/* Clamped to the card: a readout that runs off the edge on the
                  first or last day is worse than no readout. */}
              <span
                aria-hidden
                className="pointer-events-none absolute top-1 z-10 -translate-x-1/2 whitespace-nowrap rounded-md border border-hairline bg-popover px-1.5 py-0.5 text-[length:var(--type-micro)] tabular-nums text-foreground shadow-md"
                style={{ left: `${Math.min(88, Math.max(12, (activeX / WIDTH) * 100))}%` }}
              >
                <span className="font-semibold">{readValue(active.value)}</span>
                {compareActive ? (
                  <span className={cn("ml-1", TONE[compare!.tone].text)}>+{readValue(compareActive.value)}</span>
                ) : null}
                <span className="ml-1 text-muted-foreground">{formatPoint(active.date)}</span>
              </span>
            </>
          ) : null}
        </div>
      ) : (
        <div
          className="mt-2 flex items-center justify-center border-t border-hairline"
          style={{ height }}
        >
          <span className="text-[length:var(--type-micro)] text-muted-foreground">
            {hasEnoughReadings
              ? emptyLabel ?? `No ${label.toLowerCase()} in the last ${points.length} ${unitNoun}`
              : "not enough history yet"}
          </span>
        </div>
      )}

      {/* Announced separately: the visual readout is aria-hidden because it is
          a duplicate of this, positioned. */}
      <span className="sr-only" role="status">
        {active ? `${formatPoint(active.date)}: ${readValue(active.value)}` : ""}
      </span>

      <footer className="mt-auto flex items-center justify-between gap-2 border-t border-hairline px-[var(--card-pad-x)] py-1.5">
        {footer ? (
          <span className="truncate text-[length:var(--type-micro)] text-muted-foreground">{footer}</span>
        ) : (
          <>
            <span className="text-[length:var(--type-micro)] text-muted-foreground">{formatPoint(points[0]?.date)}</span>
            <span className="text-[length:var(--type-micro)] text-muted-foreground">{formatPoint(points[points.length - 1]?.date)}</span>
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

/** A full timestamp, read as a clock time — the scale a live sample lives at. */
function formatTime(value?: string) {
  if (!value) return "";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return "";
  return new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" }).format(parsed);
}
