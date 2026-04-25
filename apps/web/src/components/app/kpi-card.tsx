import type { LucideIcon } from "lucide-react";
import { cn } from "../../lib/utils";

interface KpiCardProps {
  label: string;
  value: string;
  unit?: string;
  icon: LucideIcon;
  meta: string;
  delta?: { value: string; tone: "up" | "down" };
  sparkline: number[];
  accent?: boolean;
}

function buildAreaPath(sparkline: number[], w = 100, h = 48) {
  if (sparkline.length < 2) return { line: "", area: "" };
  const max = Math.max(...sparkline);
  const min = Math.min(...sparkline) - 2;
  const range = max - min || 1;
  const pts = sparkline.map((v, i) => ({
    x: (i / (sparkline.length - 1)) * w,
    y: h - ((v - min) / range) * h * 0.9
  }));
  const line = pts.map((p, i) => `${i === 0 ? "M" : "L"}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(" ");
  const area = `${line} L${w},${h} L0,${h} Z`;
  return { line, area };
}

export function KpiCard({
  label,
  value,
  unit,
  icon: Icon,
  meta,
  delta,
  sparkline,
  accent = false
}: KpiCardProps) {
  const { line, area } = buildAreaPath(sparkline);
  const gradId = `spark-${label.replace(/\s+/g, "-")}`;

  return (
    <div
      className={cn(
        "group relative flex h-full flex-col overflow-hidden rounded-2xl border bg-card",
        accent
          ? "border-primary/20 dark:border-primary/15 shadow-[0_0_0_1px_hsl(var(--primary)/0.08),var(--shadow-card)]"
          : "border-hairline shadow-card"
      )}
    >
      {/* Accent top-edge glow */}
      <div
        className={cn(
          "absolute inset-x-0 top-0 h-px",
          accent
            ? "bg-gradient-to-r from-transparent via-primary/70 to-transparent"
            : "bg-gradient-to-r from-transparent via-hairline to-transparent"
        )}
      />

      <div className="flex flex-1 flex-col px-[var(--tile-pad)] py-[var(--tile-pad)]">
        {/* Label + icon row */}
        <div className="flex items-start justify-between gap-2">
          <div className="flex items-center gap-2">
            <span
              className={cn(
                "flex h-7 w-7 items-center justify-center rounded-lg",
                accent
                  ? "bg-primary/10 text-primary dark:bg-primary/15"
                  : "bg-muted/60 text-muted-foreground"
              )}
            >
              <Icon className="h-3.5 w-3.5" />
            </span>
            <span className="text-[length:var(--metric-label-size)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
              {label}
            </span>
          </div>
          {delta ? (
            <span
              className={cn(
                "rounded-md px-1.5 py-0.5 text-[10px] font-semibold tabular",
                delta.tone === "up"
                  ? "bg-success/12 text-success dark:bg-success/15"
                  : "bg-destructive/10 text-destructive dark:bg-destructive/12"
              )}
            >
              {delta.value}
            </span>
          ) : null}
        </div>

        {/* Hero number */}
        <div className="mt-4 flex items-end gap-1.5">
          <span
            className={cn(
              "font-bold leading-none tracking-display tabular",
              "text-[length:var(--metric-value-size)]",
              accent ? "text-primary" : "text-foreground"
            )}
          >
            {value}
          </span>
          {unit ? (
            <span className="mb-0.5 text-[length:var(--metric-unit-size)] font-medium text-muted-foreground">{unit}</span>
          ) : null}
        </div>

        {/* Supporting text */}
        <p className="mt-2.5 text-[length:var(--metric-meta-size)] leading-relaxed text-muted-foreground">{meta}</p>

        {/* Area sparkline */}
        <div className="mt-auto pt-4">
          <svg
            viewBox="0 0 100 48"
            preserveAspectRatio="none"
            className="h-12 w-full overflow-visible"
          >
            <defs>
              <linearGradient id={gradId} x1="0" y1="0" x2="0" y2="1">
                <stop
                  offset="0%"
                  stopColor={accent ? "hsl(var(--primary))" : "hsl(var(--muted-foreground))"}
                  stopOpacity={accent ? "0.28" : "0.14"}
                />
                <stop
                  offset="100%"
                  stopColor={accent ? "hsl(var(--primary))" : "hsl(var(--muted-foreground))"}
                  stopOpacity="0"
                />
              </linearGradient>
            </defs>
            <path d={area} fill={`url(#${gradId})`} />
            <path
              d={line}
              fill="none"
              stroke={accent ? "hsl(var(--primary))" : "hsl(var(--muted-foreground) / 0.45)"}
              strokeWidth="1.5"
              strokeLinecap="round"
              strokeLinejoin="round"
              vectorEffect="non-scaling-stroke"
            />
          </svg>
        </div>
      </div>
    </div>
  );
}
