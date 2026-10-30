/**
 * Speed (#1098), the first card of the top band: the clients' combined download
 * speed as a big figure with the upload beside it, and under it the last six hours
 * as the server stored them, plus the newest reading the summary carries, so the
 * trace reaches "now" and extends when a new reading arrives. It always has a
 * reading: with nothing moving it is a line at 0 MB/s, and the words never
 * contradict the trace. The trace draws itself in once when the tab opens; the
 * newest reading glows.
 */
import { useCallback, useMemo } from "react";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import type { DownloadThroughputSample } from "../../../lib/api";
import { CountUp } from "../../../components/ui/count-up";
import { formatSpeed } from "../downloads-kit";
import { BandCard, FigureUnit } from "./band-card";
import { useNow } from "./hooks";
import { linePath, shapeSpeedSeries, type SpeedSeries } from "./series";
import { ChartLayer, clockAt, nearestIndex, quarterMax, quarterTicks, rateText, type ChartHit } from "../chart-scale";

/** The mockup's chart box: 600 by 110, the trace kept 6 units off the bottom and 10 off the top. */
const WIDTH = 600;
const HEIGHT = 110;
const PAD_TOP = 10;
const PLOT = HEIGHT - PAD_TOP - 6;
const HOURS = 6;

/** The figure's number and its unit apart, so the unit can be small: "6.2" and "MB/s". */
function speedParts(mbps: number): { value: string; unit: string } {
  const [value, ...unit] = formatSpeed(mbps).split(" ");
  return { value, unit: unit.join(" ") };
}

/**
 * What the quiet note beside "Speed" says: the peak of what the big figure measures (download), and the upload's peak
 * when the chart draws one, so the words and the lines always agree. Nothing downloading with upload moving says so
 * rather than "peak 0".
 */
export function speedAside(peakDown: number, peakUp: number, hours: number): string {
  if (peakDown > 0 && peakUp > 0) return `peak ${formatSpeed(peakDown)} down · ${formatSpeed(peakUp)} up`;
  if (peakDown > 0) return `peak ${formatSpeed(peakDown)} · last ${hours} hours`;
  if (peakUp > 0) return `nothing downloading · up peaked ${formatSpeed(peakUp)}`;
  return `nothing moved in ${hours} hours`;
}

/** The reading nearest a fraction across the six hours: its time and both speeds ("2:14 pm", "6.2 MB/s down · 1.1 MB/s up"). */
export function speedHitAt(series: SpeedSeries, at: number): ChartHit | null {
  if (series.points.length === 0) return null;
  const span = Math.max(1, series.end - series.start);
  const fractions = series.points.map((point) => Math.min(1, Math.max(0, (point.t - series.start) / span)));
  const point = series.points[nearestIndex(fractions, at)];
  const down = `${rateText(point.down)} down`;
  const up = `${rateText(point.up)} up`;
  return { x: Math.min(1, Math.max(0, (point.t - series.start) / span)), time: clockAt(point.t), text: `${down} · ${up}`, parts: [{ text: down, tone: "down" }, { text: up, tone: "up" }] };
}

export function SpeedCell({ samples, summary }: { samples: DownloadThroughputSample[]; summary: DashboardSummary | undefined }) {
  // The hold-to-now edge moves on; once a minute is as often as a six hour trace shows.
  const now = useNow(30_000);
  const downloads = summary?.downloads;
  const readingAt = Date.parse(downloads?.readingUtc ?? summary?.asOfUtc ?? "");
  const live = useMemo(
    () => ({ atMs: Number.isFinite(readingAt) ? readingAt : now, down: downloads?.downloadMbps ?? 0, up: downloads?.uploadMbps ?? 0 }),
    [readingAt, downloads?.downloadMbps, downloads?.uploadMbps, now]
  );
  const series = useMemo(() => shapeSpeedSeries(Array.isArray(samples) ? samples : [], live, now, HOURS), [samples, live, now]);
  const top = quarterMax(Math.max(series.peakDown, series.peakUp));
  const resolve = useCallback((at: number) => speedHitAt(series, at), [series]);
  const last = series.points[series.points.length - 1];
  const dotY = 100 - Math.min(1, last.down / top) * 100;
  const downPath = linePath(series.points, "down", series.start, series.end, top, WIDTH, PLOT);
  const upPath = linePath(series.points, "up", series.start, series.end, top, WIDTH, PLOT);
  const area = `${downPath} L${WIDTH} ${HEIGHT - PAD_TOP} L0 ${HEIGHT - PAD_TOP} Z`;
  const summaryText = `Download peaked at ${formatSpeed(series.peakDown)} and upload at ${formatSpeed(series.peakUp)} in the last ${HOURS} hours.`;
  const upArea = `${upPath} L${WIDTH} ${HEIGHT - PAD_TOP} L0 ${HEIGHT - PAD_TOP} Z`;
  const unit = speedParts(live.down).unit;
  const aside = speedAside(series.peakDown, series.peakUp, HOURS);

  return (
    <BandCard
      label="Speed"
      to="/queue"
      linkName="In progress: Speed"
      aside={aside}
      figure={
        <>
          {/* Both figures read the same way, an arrow and the speed in the colour of its line, so the chart needs no
              legend: ↓ download blue, ↑ upload green (the owner, 2 Oct 2026). */}
          <span className="lb-down"><span aria-hidden>↓ </span><CountUp value={live.down} format={(value) => speedParts(value).value} /></span>
          <FigureUnit><span className="lb-down">{unit}</span><span className="sr-only"> download</span></FigureUnit>
          <span className="lb-side">
            <span aria-hidden>↑ </span><CountUp value={live.up} format={formatSpeed} /><span className="sr-only"> upload</span>
          </span>
        </>
      }
    >
      <svg role="img" aria-label={summaryText} viewBox={`0 0 ${WIDTH} ${HEIGHT}`} preserveAspectRatio="none" className="lb-chart">
        <defs>
          <linearGradient id="gd" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor="hsl(var(--info))" stopOpacity=".45" />
            <stop offset="1" stopColor="hsl(var(--info))" stopOpacity="0" />
          </linearGradient>
          <linearGradient id="gu" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor="hsl(var(--success))" stopOpacity=".3" />
            <stop offset="1" stopColor="hsl(var(--success))" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g transform={`translate(0 ${PAD_TOP})`}>
          <path d={area} fill="url(#gd)" />
          <path d={upArea} fill="url(#gu)" />
          <path d={downPath} pathLength={1} fill="none" stroke="hsl(var(--info))" strokeWidth="2" strokeLinejoin="round" vectorEffect="non-scaling-stroke" className="live-trace" />
          <path d={upPath} pathLength={1} fill="none" stroke="hsl(var(--success))" strokeWidth="1.6" strokeLinejoin="round" vectorEffect="non-scaling-stroke" className="live-trace" />
        </g>
      </svg>
      <ChartLayer
        ticks={quarterTicks(top, "MB/s", (PAD_TOP / HEIGHT) * 100, ((PAD_TOP + PLOT) / HEIGHT) * 100)}
        startLabel={`${HOURS} h ago`}
        endLabel="now"
        resolve={resolve}
      />
      <span aria-hidden className="lb-dot" style={{ top: `${((PAD_TOP + dotY * (PLOT / 100)) / HEIGHT) * 100}%` }} />
    </BandCard>
  );
}
