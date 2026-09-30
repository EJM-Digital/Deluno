/**
 * Speed over the last six hours (#1096): download and upload as the server has
 * stored them, plus the newest reading the summary carries, so the trace
 * reaches "now" and extends when a new reading arrives. It always has a
 * reading: with nothing moving it is a line at 0 MB/s. The trace draws itself
 * in once when the tab opens; the newest reading glows.
 */
import { useMemo } from "react";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import type { DownloadThroughputSample } from "../../../lib/api";
import { CountUp } from "../../../components/ui/count-up";
import { formatSpeed } from "../downloads-kit";
import { Panel } from "../parts";
import { useNow } from "./hooks";
import { linePath, shapeSpeedSeries } from "./series";
import { motionAllowed } from "../../../lib/page-motion";

const WIDTH = 400;
const HEIGHT = 100;
const HOURS = 6;

export function SpeedChart({ samples, summary }: { samples: DownloadThroughputSample[]; summary: DashboardSummary | undefined }) {
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

  return (
    <Panel title="Speed" count={<span className="whitespace-nowrap">last {HOURS} hours</span>} to="/queue" toLabel="Transfers">
      <div className="flex min-h-0 flex-1 flex-col px-[var(--card-pad-x)] pb-2 pt-2">
        <div className="mb-1 flex items-baseline gap-x-2 whitespace-nowrap">
          <span className="text-[length:var(--type-card-title)] font-semibold leading-none tabular-nums text-foreground"><CountUp value={live.down} format={formatSpeed} /></span>
          <span className="text-[length:var(--type-caption)] text-muted-foreground">
            {downloads?.hasReading === false ? "waiting for the first reading" : live.down > 0 ? `now, ${(downloads?.active ?? 0).toLocaleString("en-AU")} downloading` : "now, nothing moving"}
          </span>
          {live.up > 0 ? <span className="ml-auto text-[length:var(--type-caption)] tabular-nums text-success">up {formatSpeed(live.up)}</span> : null}
        </div>
        <div className="mb-1.5 flex flex-wrap items-center gap-x-4 text-[length:var(--type-caption)] text-muted-foreground">
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap"><span aria-hidden className="h-1.5 w-3 rounded-full bg-info" />Download peak <span className="font-medium tabular-nums text-foreground">{formatSpeed(series.peakDown)}</span></span>
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap"><span aria-hidden className="h-1.5 w-3 rounded-full bg-success" />Upload peak <span className="font-medium tabular-nums text-foreground">{formatSpeed(series.peakUp)}</span></span>
        </div>
        <div className="relative min-h-0 flex-1">
          <svg role="img" aria-label={summaryText} viewBox={`0 0 ${WIDTH} ${HEIGHT}`} preserveAspectRatio="none" className="absolute inset-0 h-full w-full overflow-visible">
            <defs>
              <linearGradient id="live-speed-fill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="hsl(var(--info))" stopOpacity="0.32" />
                <stop offset="100%" stopColor="hsl(var(--info))" stopOpacity="0" />
              </linearGradient>
            </defs>
            {[25, 50, 75].map((y) => (
              <line key={y} x1="0" x2={WIDTH} y1={y} y2={y} stroke="hsl(var(--hairline))" strokeWidth="1" vectorEffect="non-scaling-stroke" />
            ))}
            <path d={area} fill="url(#live-speed-fill)" />
            <path d={upPath} pathLength={1} fill="none" stroke="hsl(var(--success))" strokeWidth="1.5" strokeLinejoin="round" vectorEffect="non-scaling-stroke" className="live-trace" />
            <path d={downPath} pathLength={1} fill="none" stroke="hsl(var(--info))" strokeWidth="2" strokeLinejoin="round" vectorEffect="non-scaling-stroke" className="live-trace" />
          </svg>
          <span aria-hidden className="absolute right-0 h-2.5 w-2.5 -translate-y-1/2 translate-x-1/2" style={{ top: `${dotY}%` }}>
            {motionAllowed() ? <span className="live-beat absolute inset-0 rounded-full bg-info" /> : null}
            <span className="absolute inset-0 rounded-full bg-info" />
          </span>
        </div>
        <div className="mt-1 flex items-center justify-between gap-3 text-[length:var(--type-caption)] text-muted-foreground">
          <span>{HOURS} h ago</span>
          <span>now</span>
        </div>
      </div>
    </Panel>
  );
}
