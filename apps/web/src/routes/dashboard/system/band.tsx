/**
 * The top row of System (#1098), the approved mockup's three cards from one template: This instance (the health ring and
 * twelve fact tiles), This computer (CPU, memory and disk with traces and the machine's own tags) and Network (the
 * adapter's in and out with a trace, beside an aligned table of checks). Every figure is a reading from
 * GET /api/system/overview; one that could not be read is left out.
 */
import { useCallback, useId, useLayoutEffect, useRef, useState, type CSSProperties, type JSX } from "react";
import type { SystemOverview } from "../../../lib/api/types/system-overview";
import type { DashboardSummary } from "../../../lib/api";
import { formatBytes } from "../../../lib/utils";
import { useNow } from "../live/hooks";
import { ChartLayer, quarterMax, rateText, traceHitAt } from "../chart-scale";
import { instanceFacts } from "./facts";
import { countdown, mbps, plainCount, sizeText, tracePaths, upText, wholeRows } from "./logic";

const RING = 201.1;
const FACT_ROW_PX = 42;
const FACT_GAP_PX = 5;

export function Trace({ values, max, colour }: { values: readonly number[]; max: number; colour: string }): JSX.Element {
  const id = useId().replace(/[^a-zA-Z0-9]/g, "");
  const paths = tracePaths(values, max);
  return (
    <svg viewBox="0 0 200 50" preserveAspectRatio="none" aria-hidden="true">
      {paths ? (
        <>
          <defs>
            <linearGradient id={`g${id}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0" style={{ stopColor: colour, stopOpacity: 0.4 }} />
              <stop offset="1" style={{ stopColor: colour, stopOpacity: 0 }} />
            </linearGradient>
          </defs>
          <path d={paths.area} fill={`url(#g${id})`} />
          <path d={paths.line} fill="none" strokeWidth="1.5" style={{ stroke: colour }} />
        </>
      ) : null}
    </svg>
  );
}

/** What a trace's scale and reading are in: a percentage (top 100) or a rate that arrives in bytes a second. */
export type TraceScale = { kind: "percent"; noun?: string } | { kind: "rate" };

/** The top of a trace's scale in its own units: 100 for a percentage, else a round number of MB/s just above the highest reading. */
export function traceTop(values: readonly number[], scale: TraceScale): number {
  if (scale.kind === "percent") return 100;
  return quarterMax(Math.max(0, ...values.filter((value) => Number.isFinite(value))) / 1e6);
}

/**
 * A trace with the reading under the pointer (#1105), in the box its card gives it. No scale is drawn: the figure above it
 * says the reading now, and pointing at the trace says what it was then (James, 2 Oct 2026). Live's Speed chart keeps its scale.
 */
export function TraceChart({ values, scale, colour }: { values: readonly number[]; scale: TraceScale; colour: string }): JSX.Element {
  const top = traceTop(values, scale);
  const percent = scale.kind === "percent";
  const text = useCallback((value: number) => (percent ? `${Math.round(value)}%${scale.noun ? ` ${scale.noun}` : ""}` : rateText(value / 1e6)), [percent, scale]);
  const resolve = useCallback((at: number) => traceHitAt(values, at, Date.now(), text), [values, text]);
  return (
    <div className="cs-plot">
      <Trace values={values} max={percent ? top : top * 1e6} colour={colour} />
      <ChartLayer
        ticks={[]}
        resolve={resolve}
      />
    </div>
  );
}

/* ---------------------------------------------------------- this instance */

export interface HealthFigures {
  pass: number;
  total: number;
  need: number;
  anyBad: boolean;
}

export function InstanceCard({ instance, summary, health, short, onJump }: { instance: SystemOverview["instance"] | null; summary: DashboardSummary | undefined; health: HealthFigures; short: boolean; onJump: () => void }): JSX.Element {
  const now = useNow(1000);
  const facts = instanceFacts(instance, summary, now);
  const started = instance ? Date.parse(instance.startedUtc) : NaN;
  const uptime = Number.isFinite(started) ? `up ${countdown(now - started)}` : summary?.system ? upText(summary.system.uptimeSeconds) : "";
  const fraction = health.total > 0 ? health.pass / health.total : 0;
  const room = useRef<HTMLDivElement>(null);
  const [height, setHeight] = useState<number | undefined>(undefined);
  // Whole rows only, never a sliver of the next: the box is as tall as the rows that fit, and scrolls a row at a time to the rest.
  useLayoutEffect(() => {
    const el = room.current;
    if (!el) return;
    const fit = () => {
      const rows = wholeRows(el.clientHeight, FACT_ROW_PX, FACT_GAP_PX);
      setHeight(rows * FACT_ROW_PX + (rows - 1) * FACT_GAP_PX);
    };
    fit();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(fit);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return (
    <section className="sy-card sy-tile sy-inst" aria-label="This instance">
      <div className="sy-top">
        <span className="sy-label">This instance</span>
        <span className="sy-faint sy-num">{uptime}</span>
      </div>
      <div className="sy-inst-body">
        <div className="sy-inst-l">
          <div className="sy-ring">
            <div className="sy-rc">
              <svg viewBox="0 0 76 76" aria-hidden="true">
                <circle className="sy-track" cx="38" cy="38" r="32" />
                <circle className="sy-val" cx="38" cy="38" r="32" strokeDasharray={RING} style={{ strokeDashoffset: RING * (1 - fraction), stroke: health.anyBad ? "var(--sy-warning)" : "var(--sy-success)" }} />
              </svg>
              <b className="sy-num">{health.pass}</b>
            </div>
          </div>
          <small className="sy-rcap">{short ? `of ${plainCount(health.total)} checks` : `of ${plainCount(health.total)} health checks pass`}</small>
          {health.need > 0 ? (
            <a className="sy-needs" href="#system-health" onClick={(event) => { event.preventDefault(); onJump(); }}>
              {plainCount(health.need)} need you →
            </a>
          ) : (
            <span className="sy-needs sy-good">All good</span>
          )}
        </div>
        <div className="sy-inst-r" ref={room}>
          <div className="sy-facts" style={height ? ({ height } satisfies CSSProperties) : undefined} tabIndex={0} role="group" aria-label="Facts about this instance">
            {facts.map((fact) => (
              <div key={fact.label}>
                <span>{fact.label}</span>
                <b>{fact.value}</b>
                <em className={fact.tone ? `sy-${fact.tone}` : undefined}>{fact.sub}</em>
              </div>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}

/* ---------------------------------------------------------- this computer */

function Column({ colour, label, value, unit, sub, values, scale }: { colour: string; label: string; value: string; unit: string; sub: string; values: readonly number[]; scale: TraceScale }): JSX.Element {
  return (
    <div className="sy-col" style={{ "--m": colour } as CSSProperties}>
      <span className="sy-mh" title={label}><i />{label}</span>
      <span className="sy-v">{value}<small>{unit}</small></span>
      <span className="sy-sub" title={sub}>{sub}</span>
      <TraceChart values={values} scale={scale} colour={colour} />
    </div>
  );
}

const GREEN = "hsl(var(--success))";
const BLUE = "hsl(var(--info))";
const VIOLET = "hsl(262 83% 72%)";
const CYAN = "hsl(186 85% 55%)";

export function ComputerCard({ overview }: { overview: SystemOverview | null }): JSX.Element {
  const machine = overview?.machine;
  const instance = overview?.instance;
  const history = overview?.history;
  const tags: string[] = [];
  if (machine) {
    if (machine.os) tags.push(machine.os);
    const up = upText(machine.uptimeSeconds);
    if (up) tags.push(up);
    if (machine.rebootPending !== null) tags.push(machine.rebootPending ? "reboot pending" : "no reboot pending");
    if (machine.cpuTemperatureC !== null) tags.push(`CPU ${Math.round(machine.cpuTemperatureC)} °C`);
  }
  return (
    <section className="sy-card sy-tile sy-pc" aria-label="This computer">
      <div className="sy-top">
        <span className="sy-label">This computer</span>
        <span className="sy-faint">last 10 minutes</span>
      </div>
      {machine ? (
        <div className="sy-cols3">
          <Column colour={GREEN} label="CPU" value={String(Math.round(machine.cpuPercent))} unit="%" sub={[`${machine.cores} cores`, instance ? `Deluno ${Math.round(instance.cpuPercent)}%` : null].filter(Boolean).join(" · ")} values={history?.cpu ?? []} scale={{ kind: "percent" }} />
          <Column colour={BLUE} label="Memory" value={formatBytes(machine.memoryUsedBytes).split(" ")[0]} unit={` ${formatBytes(machine.memoryUsedBytes).split(" ")[1] ?? ""}`} sub={[`of ${formatBytes(machine.memoryTotalBytes)}`, instance ? `Deluno ${sizeText(instance.memoryBytes)}` : null].filter(Boolean).join(" · ")} values={history?.memory ?? []} scale={{ kind: "percent" }} />
          <Column colour={VIOLET} label="Disk" value={mbps(machine.diskReadBytesPerSec + machine.diskWriteBytesPerSec)} unit=" MB/s" sub={`read ${mbps(machine.diskReadBytesPerSec)} · write ${mbps(machine.diskWriteBytesPerSec)} · ${plainCount(machine.diskIops)} IOPS`} values={history?.disk ?? []} scale={{ kind: "percent", noun: "busy" }} />
        </div>
      ) : (
        <div className="sy-empty sy-muted">Deluno has not read this computer yet. The first reading arrives within a few seconds.</div>
      )}
      <div className="sy-machine">{tags.map((tag) => <span key={tag}>{tag}</span>)}</div>
    </section>
  );
}

/* ---------------------------------------------------------------- network */

function linkText(mbps: number | null): string | null {
  if (mbps === null || !Number.isFinite(mbps) || mbps <= 0) return null;
  return mbps >= 1000 ? `${mbps / 1000} Gb/s` : `${Math.round(mbps)} Mb/s`;
}

interface Check {
  name: string;
  value: string;
  ok: boolean;
}

export function networkChecks(network: SystemOverview["network"]): Check[] {
  const checks: Check[] = [{ name: "Internet", value: network.internet ? "reachable" : "not reachable", ok: network.internet }];
  if (network.dnsLookupMs !== null) checks.push({ name: "DNS lookup", value: `${Math.round(network.dnsLookupMs)} ms`, ok: true });
  if (network.nasMs !== null) checks.push({ name: network.nasName || "Your NAS", value: `${Math.round(network.nasMs)} ms`, ok: true });
  if (network.metadataMs !== null) checks.push({ name: "Metadata service", value: `${Math.round(network.metadataMs)} ms`, ok: network.metadataMs < 2000 });
  if (network.clockInSync !== null) checks.push({ name: "Clock", value: network.clockInSync ? "in sync" : "out of sync", ok: network.clockInSync });
  return checks;
}

export function NetworkCard({ overview }: { overview: SystemOverview | null }): JSX.Element {
  const network = overview?.network;
  // The whole line is the tooltip of the cut one: the adapter name alone told nothing of the rest (#1168).
  const networkLine = network ? [`↑ ${mbps(network.outBytesPerSec)} out`, linkText(network.linkMbps), network.ip].filter(Boolean).join(" · ") : "";
  return (
    <section className="sy-card sy-tile sy-net" aria-label="Network">
      <div className="sy-top">
        <span className="sy-label">Network</span>
        <span className="sy-faint">last 10 minutes</span>
      </div>
      {network ? (
        <div className="sy-net2">
          <div className="sy-col" style={{ "--m": CYAN } as CSSProperties}>
            <span className="sy-mh" title={network.adapter ?? "Network"}><i />{network.adapter ?? "Network"}</span>
            <span className="sy-v">{mbps(network.inBytesPerSec)}<small> MB/s in</small></span>
            <span className="sy-sub" title={networkLine}>{networkLine}</span>
            <TraceChart values={overview?.history.netIn ?? []} scale={{ kind: "rate" }} colour={CYAN} />
          </div>
          <div className="sy-col sy-checks2">
            <span className="sy-mh">Checks</span>
            <div className="sy-reach">
              {networkChecks(network).map((check) => (
                <span key={check.name} className={check.ok ? undefined : "sy-warn"}>
                  <em>{check.ok ? "✓" : "!"}</em>
                  <i>{check.name}</i>
                  <b>{check.value}</b>
                </span>
              ))}
            </div>
          </div>
        </div>
      ) : (
        <div className="sy-empty sy-muted">Deluno has not read the network yet.</div>
      )}
    </section>
  );
}
