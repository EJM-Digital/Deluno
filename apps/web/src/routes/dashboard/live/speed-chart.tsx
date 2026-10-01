/**
 * Speed (#1098), the first card of the top band: the clients' combined download
 * speed as a big figure with the upload beside it, and under it the last six hours
 * as the server stored them, plus the newest reading the summary carries, so the
 * trace reaches "now" and extends when a new reading arrives. It always has a
 * reading: with nothing moving it is a line at 0 MB/s, and the words never
 * contradict the trace. The trace draws itself in once when the tab opens; the
 * newest reading glows.
 */
import { useMemo } from "react";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import type { DownloadThroughputSample } from "../../../lib/api";
import { CountUp } from "../../../components/ui/count-up";
import { motionAllowed } from "../../../lib/page-motion";
import { formatSpeed } from "../downloads-kit";
import { BandCard, FigureUnit } from "./band-card";
import { useNow } from "./hooks";
import { linePath, shapeSpeedSeries } from "./series";

const WIDTH = 400;
const HEIGHT = 100;
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
  const top = Math.max(series.peakDown, series.peakUp, 1);
  const last = series.points[series.points.length - 1];
  const dotY = 100 - Math.min(1, last.down / top) * 100;
  const downPath = linePath(series.points, "down", series.start, series.end, top, WIDTH, HEIGHT);
  const upPath = linePath(series.points, "up", series.start, series.end, top, WIDTH, HEIGHT);
  const area = `${downPath} L${WIDTH} ${HEIGHT} L0 ${HEIGHT} Z`;
  const summaryText = `Download peaked at ${formatSpeed(series.peakDown)} and upload at ${formatSpeed(series.peakUp)} in the last ${HOURS} hours.`;
  const upArea = `${upPath} L${WIDTH} ${HEIGHT} L0 ${HEIGHT} Z`;
  const unit = speedParts(live.down).unit;
  const aside = speedAside(series.peakDown, series.peakUp, HOURS);

  return (
    <BandCard
      label="Speed"
      to="/queue"
      linkName="Transfers: Speed"
      aside={aside}
      figure={
        <>
          <CountUp value={live.down} format={(value) => speedParts(value).value} />
          <FigureUnit>{unit} down</FigureUnit>
          <span className="ml-auto shrink-0 text-[length:var(--type-body-sm)] font-semibold tracking-normal text-success">
            ↑ <CountUp value={live.up} format={formatSpeed} />
          </span>
        </>
      }
    >
      <div className="relative h-full min-h-[3rem]">
        <svg role="img" aria-label={summaryText} viewBox={`0 0 ${WIDTH} ${HEIGHT}`} preserveAspectRatio="none" className="absolute inset-0 h-full w-full overflow-visible">
          <defs>
            <linearGradient id="live-speed-fill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="hsl(var(--info))" stopOpacity="0.4" />
              <stop offset="100%" stopColor="hsl(var(--info))" stopOpacity="0" />
            </linearGradient>
            <linearGradient id="live-speed-up-fill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="hsl(var(--success))" stopOpacity="0.14" />
              <stop offset="100%" stopColor="hsl(var(--success))" stopOpacity="0" />
            </linearGradient>
          </defs>
          <line x1="0" x2={WIDTH} y1="50" y2="50" stroke="hsl(var(--hairline))" strokeWidth="1" vectorEffect="non-scaling-stroke" />
          <path d={area} fill="url(#live-speed-fill)" />
          <path d={upArea} fill="url(#live-speed-up-fill)" />
          <path d={upPath} pathLength={1} fill="none" stroke="hsl(var(--success))" strokeWidth="1" strokeLinejoin="round" vectorEffect="non-scaling-stroke" className="live-trace" />
          <path d={downPath} pathLength={1} fill="none" stroke="hsl(var(--info))" strokeWidth="2" strokeLinejoin="round" vectorEffect="non-scaling-stroke" className="live-trace" />
        </svg>
        <span aria-hidden className="absolute right-0 h-2.5 w-2.5 -translate-y-1/2 translate-x-1/2" style={{ top: `${dotY}%` }}>
          {motionAllowed() ? <span className="live-beat absolute inset-0 rounded-full bg-info" /> : null}
          <span className="absolute inset-0 rounded-full bg-info" />
        </span>
      </div>
    </BandCard>
  );
}
