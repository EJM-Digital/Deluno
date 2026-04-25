/**
 * Deluno AreaChart — hand-built SVG area/line chart.
 *
 * Zero external dependencies; uses the accent system via
 * `hsl(var(--primary))`. Supports:
 *   - Single or dual series (base + comparison)
 *   - Optional gradient fill
 *   - Hover crosshair with tooltip callback
 *   - Responsive (ResizeObserver) sizing
 *
 * Data points: `{ x: label, y: number }[]`. All series must share
 * length so the x-axis aligns.
 */

import * as React from "react";
import { cn } from "../../lib/utils";

export interface AreaPoint {
  x: string;
  y: number;
}

export interface AreaSeries {
  name: string;
  data: AreaPoint[];
  /** Tailwind-compatible tone keyword mapped to our palette. */
  tone?: "primary" | "info" | "success" | "warning" | "danger";
}

interface AreaChartProps {
  series: AreaSeries[];
  height?: number;
  /** Render a faint horizontal grid. @default true */
  grid?: boolean;
  /** Show x-axis labels derived from `.x`. @default true */
  xAxis?: boolean;
  /** Show y-axis min/mid/max labels. @default true */
  yAxis?: boolean;
  /** Fill the area under the line. @default true */
  fill?: boolean;
  /** Min/max hint (otherwise auto). */
  yDomain?: [number, number];
  className?: string;
  /** ARIA summary read by screen readers. */
  ariaLabel?: string;
}

const TONE_STOP: Record<NonNullable<AreaSeries["tone"]>, string> = {
  primary: "hsl(var(--primary))",
  info: "hsl(var(--info))",
  success: "hsl(var(--success))",
  warning: "hsl(var(--warning))",
  danger: "hsl(var(--destructive))"
};

export function AreaChart({
  series,
  height = 180,
  grid = true,
  xAxis = true,
  yAxis = true,
  fill = true,
  yDomain,
  className,
  ariaLabel
}: AreaChartProps) {
  const wrapRef = React.useRef<HTMLDivElement>(null);
  const [w, setW] = React.useState(600);
  const [hoverIndex, setHoverIndex] = React.useState<number | null>(null);

  React.useEffect(() => {
    if (!wrapRef.current) return;
    const ro = new ResizeObserver((entries) => {
      for (const e of entries) setW(Math.max(120, Math.floor(e.contentRect.width)));
    });
    ro.observe(wrapRef.current);
    return () => ro.disconnect();
  }, []);

  const len = series[0]?.data.length ?? 0;
  if (len < 2) {
    return (
      <div
        ref={wrapRef}
        className={cn("flex items-center justify-center text-[11px] text-muted-foreground", className)}
        style={{ height }}
      >
        Not enough data yet
      </div>
    );
  }

  const padX = 12;
  const padTop = 8;
  const padBottom = xAxis ? 22 : 8;
  const chartH = height - padTop - padBottom;
  const chartW = Math.max(40, w - padX * 2);

  const allY = series.flatMap((s) => s.data.map((p) => p.y));
  const [dMin, dMax] = yDomain ?? [Math.min(...allY), Math.max(...allY)];
  const minY = Math.min(dMin, 0);
  const maxY = Math.max(dMax, minY + 1);

  const xFor = (i: number) => padX + (i / (len - 1)) * chartW;
  const yFor = (v: number) => padTop + chartH - ((v - minY) / (maxY - minY)) * chartH;

  // Smooth path using monotone cubic approximation — simpler: Catmull-Rom → bezier
  function pathFor(points: { x: number; y: number }[], closeArea = false) {
    if (points.length < 2) return "";
    const segs: string[] = [`M ${points[0].x.toFixed(2)} ${points[0].y.toFixed(2)}`];
    for (let i = 0; i < points.length - 1; i++) {
      const p0 = points[Math.max(0, i - 1)];
      const p1 = points[i];
      const p2 = points[i + 1];
      const p3 = points[Math.min(points.length - 1, i + 2)];
      const t = 0.2;
      const c1x = p1.x + (p2.x - p0.x) * t;
      const c1y = p1.y + (p2.y - p0.y) * t;
      const c2x = p2.x - (p3.x - p1.x) * t;
      const c2y = p2.y - (p3.y - p1.y) * t;
      segs.push(
        `C ${c1x.toFixed(2)} ${c1y.toFixed(2)} ${c2x.toFixed(2)} ${c2y.toFixed(2)} ${p2.x.toFixed(2)} ${p2.y.toFixed(2)}`
      );
    }
    if (closeArea) {
      const last = points[points.length - 1];
      const first = points[0];
      segs.push(`L ${last.x.toFixed(2)} ${(padTop + chartH).toFixed(2)}`);
      segs.push(`L ${first.x.toFixed(2)} ${(padTop + chartH).toFixed(2)}`);
      segs.push("Z");
    }
    return segs.join(" ");
  }

  const gridLines = 4;
  const gridYs = Array.from({ length: gridLines + 1 }, (_, i) => padTop + (i / gridLines) * chartH);

  const xLabels = xAxis
    ? series[0].data.map((p, i) => {
        if (len > 8 && i !== 0 && i !== len - 1 && i !== Math.floor(len / 2)) return null;
        return (
          <text
            key={`xl-${i}`}
            x={xFor(i)}
            y={height - 6}
            textAnchor={i === 0 ? "start" : i === len - 1 ? "end" : "middle"}
            className="fill-muted-foreground"
            style={{ fontSize: 10 }}
          >
            {p.x}
          </text>
        );
      })
    : null;

  return (
    <div
      ref={wrapRef}
      className={cn("relative w-full", className)}
      style={{ height }}
      role="img"
      aria-label={ariaLabel ?? series.map((s) => s.name).join(", ")}
      onMouseMove={(e) => {
        const rect = wrapRef.current!.getBoundingClientRect();
        const rx = e.clientX - rect.left - padX;
        const idx = Math.round((rx / chartW) * (len - 1));
        if (idx >= 0 && idx < len) setHoverIndex(idx);
      }}
      onMouseLeave={() => setHoverIndex(null)}
    >
      <svg width={w} height={height} className="block">
        <defs>
          {series.map((s, si) => {
            const color = TONE_STOP[s.tone ?? "primary"];
            return (
              <linearGradient
                key={`grad-${si}`}
                id={`area-grad-${si}-${s.name.replace(/\s+/g, "-")}`}
                x1="0"
                y1="0"
                x2="0"
                y2="1"
              >
                <stop offset="0%" stopColor={color} stopOpacity="0.35" />
                <stop offset="100%" stopColor={color} stopOpacity="0" />
              </linearGradient>
            );
          })}
        </defs>

        {grid
          ? gridYs.map((gy, i) => (
              <line
                key={`gl-${i}`}
                x1={padX}
                x2={padX + chartW}
                y1={gy}
                y2={gy}
                stroke="hsl(var(--hairline))"
                strokeDasharray="2 3"
                strokeWidth={1}
                opacity={i === gridLines ? 0.9 : 0.5}
              />
            ))
          : null}

        {yAxis ? (
          <>
            <text
              x={padX}
              y={padTop + 10}
              className="fill-muted-foreground"
              style={{ fontSize: 10 }}
              opacity={0.7}
            >
              {formatCompact(maxY)}
            </text>
            <text
              x={padX}
              y={padTop + chartH - 2}
              className="fill-muted-foreground"
              style={{ fontSize: 10 }}
              opacity={0.7}
            >
              {formatCompact(minY)}
            </text>
          </>
        ) : null}

        {series.map((s, si) => {
          const pts = s.data.map((p, i) => ({ x: xFor(i), y: yFor(p.y) }));
          const color = TONE_STOP[s.tone ?? "primary"];
          const gradId = `area-grad-${si}-${s.name.replace(/\s+/g, "-")}`;
          return (
            <g key={`series-${si}`}>
              {fill ? <path d={pathFor(pts, true)} fill={`url(#${gradId})`} /> : null}
              <path d={pathFor(pts, false)} stroke={color} strokeWidth={2} fill="none" strokeLinecap="round" />
            </g>
          );
        })}

        {hoverIndex !== null ? (
          <g>
            <line
              x1={xFor(hoverIndex)}
              x2={xFor(hoverIndex)}
              y1={padTop}
              y2={padTop + chartH}
              stroke="hsl(var(--primary))"
              strokeWidth={1}
              strokeDasharray="3 3"
              opacity={0.6}
            />
            {series.map((s, si) => {
              const color = TONE_STOP[s.tone ?? "primary"];
              const cx = xFor(hoverIndex);
              const cy = yFor(s.data[hoverIndex].y);
              return (
                <circle
                  key={`dot-${si}`}
                  cx={cx}
                  cy={cy}
                  r={4}
                  fill="hsl(var(--background))"
                  stroke={color}
                  strokeWidth={2}
                />
              );
            })}
          </g>
        ) : null}

        {xLabels}
      </svg>

      {hoverIndex !== null ? (
        <div
          className="pointer-events-none absolute top-0 z-10 -translate-x-1/2 rounded-lg border border-hairline bg-card/95 px-2.5 py-1.5 text-[11px] shadow-lg backdrop-blur dark:border-white/[0.06]"
          style={{ left: xFor(hoverIndex) }}
        >
          <p className="font-semibold text-foreground">{series[0].data[hoverIndex].x}</p>
          {series.map((s) => (
            <p key={s.name} className="mt-0.5 flex items-center gap-1.5 tabular text-muted-foreground">
              <span
                className="h-1.5 w-1.5 rounded-full"
                style={{ background: TONE_STOP[s.tone ?? "primary"] }}
              />
              <span className="text-foreground">{formatCompact(s.data[hoverIndex].y)}</span>
              <span>{s.name}</span>
            </p>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function formatCompact(n: number): string {
  if (Math.abs(n) >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return Math.round(n * 10) / 10 + "";
}
