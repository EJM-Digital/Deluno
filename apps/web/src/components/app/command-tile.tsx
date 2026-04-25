import type { LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../../lib/utils";

interface CommandTileProps {
  label: string;
  value: string;
  unit?: string;
  icon?: LucideIcon;
  /** Right-side visual: progress bar, sparkline, or a badge. */
  visual?: ReactNode;
  /** Top-right glyph (e.g., a check for healthy) */
  accessory?: ReactNode;
  /** Supporting line (small text below the visual) */
  meta?: string;
  /** Accent variant applies a primary-tinted glow */
  accent?: boolean;
  className?: string;
}

/**
 * Hero KPI tile — big number, one supporting visual, one meta line.
 * Modelled after Apple-native macOS dashboard cards.
 */
export function CommandTile({
  label,
  value,
  unit,
  icon: Icon,
  visual,
  accessory,
  meta,
  accent = false,
  className
}: CommandTileProps) {
  return (
    <div
      className={cn(
        "group relative overflow-hidden rounded-2xl border border-hairline bg-card px-[var(--tile-pad)] py-[var(--tile-pad)] shadow-card transition-all",
        "dark:border-white/[0.06]",
        accent &&
          "shadow-[0_0_0_1px_hsl(var(--primary)/0.18),0_12px_40px_hsl(var(--primary)/0.12)] dark:shadow-[0_0_0_1px_hsl(var(--primary)/0.22),0_20px_60px_hsl(var(--primary)/0.2)]",
        className
      )}
    >
      {/* Subtle ambient glow layer — behind content */}
      <div
        aria-hidden
        className={cn(
          "pointer-events-none absolute inset-0 bg-gradient-to-br",
          accent
            ? "from-primary/10 via-transparent to-info/6 dark:from-primary/14 dark:to-info/8"
            : "from-muted/20 via-transparent to-transparent dark:from-white/[0.02]"
        )}
      />

      {/* Top-border accent */}
      <div
        aria-hidden
        className={cn(
          "absolute inset-x-0 top-0 h-px",
          accent
            ? "bg-gradient-to-r from-transparent via-primary/70 to-transparent"
            : "bg-gradient-to-r from-transparent via-hairline to-transparent"
        )}
      />

      <div className="relative">
        {/* Label row */}
        <div className="flex items-center justify-between gap-2">
          <div className="flex items-center gap-2 min-w-0">
            {Icon ? (
              <Icon
                className={cn(
                  "h-[15px] w-[15px] shrink-0",
                  accent ? "text-primary" : "text-muted-foreground"
                )}
                strokeWidth={1.85}
              />
            ) : null}
            <span className="truncate text-[length:var(--metric-label-size)] font-semibold text-foreground">{label}</span>
          </div>
          {accessory ? <div className="shrink-0">{accessory}</div> : null}
        </div>

        {/* Value row */}
        <div className="mt-3 flex items-end gap-1.5">
          <span
            className={cn(
              "tabular font-display font-bold leading-[0.95] tracking-display",
              "text-[length:var(--metric-value-size)]",
              accent
                ? "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] bg-clip-text text-transparent"
                : "text-foreground"
            )}
          >
            {value}
          </span>
          {unit ? (
            <span className="mb-1.5 text-[length:var(--metric-unit-size)] font-medium text-muted-foreground">
              {unit}
            </span>
          ) : null}
        </div>

        {/* Visual */}
        {visual ? <div className="mt-4">{visual}</div> : null}

        {/* Meta */}
        {meta ? (
          <p className="mt-2 text-[length:var(--metric-meta-size)] leading-relaxed text-muted-foreground">{meta}</p>
        ) : null}
      </div>
    </div>
  );
}

/* ══════════ VISUALS ══════════ */

/**
 * Premium gradient progress bar — reference-style, with gloss + glow.
 */
export function GradientProgress({
  value,
  tone = "primary",
  showValue = false
}: {
  value: number;
  tone?: "primary" | "success" | "warn";
  showValue?: boolean;
}) {
  const gradients = {
    primary: "from-primary via-primary to-[hsl(var(--primary-2))]",
    success: "from-success via-success to-[hsl(152_55%_52%)]",
    warn: "from-warning via-warning to-[hsl(24_92%_56%)]"
  }[tone];

  const glow = {
    primary: "shadow-[0_0_10px_hsl(var(--primary)/0.55)]",
    success: "shadow-[0_0_10px_hsl(var(--success)/0.55)]",
    warn: "shadow-[0_0_10px_hsl(var(--warning)/0.55)]"
  }[tone];

  return (
    <div className="flex items-center gap-2.5">
      <div className="relative h-1.5 flex-1 overflow-hidden rounded-full bg-muted/60 dark:bg-white/[0.05]">
        <div
          className={cn(
            "relative h-full rounded-full bg-gradient-to-r transition-[width] duration-500",
            gradients,
            glow
          )}
          style={{ width: `${Math.min(100, Math.max(0, value))}%` }}
        >
          {/* Gloss stripe on top */}
          <div className="absolute inset-x-0 top-0 h-[45%] rounded-t-full bg-gradient-to-b from-white/35 to-transparent" />
        </div>
      </div>
      {showValue ? (
        <span className="tabular shrink-0 text-[11px] font-semibold text-muted-foreground">
          {Math.round(value)}%
        </span>
      ) : null}
    </div>
  );
}

/**
 * Inline sparkline (smoothed path) rendered at tile-bottom.
 */
export function InlineSparkline({
  data,
  tone = "primary",
  className
}: {
  data: number[];
  tone?: "primary" | "success" | "warn" | "neutral";
  className?: string;
}) {
  if (data.length < 2) return null;
  const w = 120;
  const h = 28;
  const max = Math.max(...data);
  const min = Math.min(...data) - 2;
  const range = max - min || 1;
  const pts = data.map((v, i) => ({
    x: (i / (data.length - 1)) * w,
    y: h - ((v - min) / range) * h * 0.85
  }));
  const line = pts
    .map((p, i) => `${i === 0 ? "M" : "L"}${p.x.toFixed(1)},${p.y.toFixed(1)}`)
    .join(" ");
  const area = `${line} L${w},${h} L0,${h} Z`;

  const strokeColor = {
    primary: "hsl(var(--primary))",
    success: "hsl(var(--success))",
    warn: "hsl(var(--warning))",
    neutral: "hsl(var(--muted-foreground) / 0.55)"
  }[tone];
  const id = `spark-${Math.random().toString(36).slice(2, 8)}`;

  return (
    <svg
      viewBox={`0 0 ${w} ${h}`}
      preserveAspectRatio="none"
      className={cn("h-7 w-full", className)}
    >
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={strokeColor} stopOpacity="0.3" />
          <stop offset="100%" stopColor={strokeColor} stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={area} fill={`url(#${id})`} />
      <path
        d={line}
        fill="none"
        stroke={strokeColor}
        strokeWidth="1.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  );
}
