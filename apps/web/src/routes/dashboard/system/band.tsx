/**
 * The top row of System (#1098), the approved mockup's three cards from one template: This instance (the health ring and
 * twelve fact tiles), This computer (CPU, memory and disk with traces and the machine's own tags) and Network (the
 * adapter's in and out with a trace, beside an aligned table of checks). Every figure is a reading from
 * GET /api/system/overview; one that could not be read is left out.
 */
import { useId, useLayoutEffect, useRef, useState, type CSSProperties, type JSX } from "react";
import type { SystemOverview } from "../../../lib/api/types/system-overview";
import type { DashboardSummary } from "../../../lib/api";
import { useNow } from "../live/hooks";
import { instanceFacts } from "./facts";
import { countdown, mbps, plainCount, sizeText, tracePaths, traceMax, upText, wholeRows } from "./logic";

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

function Column({ colour, label, value, unit, sub, values, max }: { colour: string; label: string; value: string; unit: string; sub: string; values: readonly number[]; max: number }): JSX.Element {
  return (
    <div className="sy-col" style={{ "--m": colour } as CSSProperties}>
      <span className="sy-mh"><i />{label}</span>
      <span className="sy-v">{value}<small>{unit}</small></span>
      <span className="sy-sub" title={sub}>{sub}</span>
      <Trace values={values} max={max} colour={colour} />
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
          <Column colour={GREEN} label="CPU" value={String(Math.round(machine.cpuPercent))} unit="%" sub={[`${machine.cores} cores`, instance ? `Deluno ${Math.round(instance.cpuPercent)}%` : null].filter(Boolean).join(" · ")} values={history?.cpu ?? []} max={100} />
          <Column colour={BLUE} label="Memory" value={(machine.memoryUsedBytes / 1e9).toFixed(1)} unit=" GB" sub={[`of ${(machine.memoryTotalBytes / 1e9).toFixed(1)} GB`, instance ? `Deluno ${sizeText(instance.memoryBytes)}` : null].filter(Boolean).join(" · ")} values={history?.memory ?? []} max={traceMax(history?.memory ?? [], undefined, 1.5)} />
          <Column colour={VIOLET} label="Disk" value={mbps(machine.diskReadBytesPerSec + machine.diskWriteBytesPerSec)} unit=" MB/s" sub={`read ${mbps(machine.diskReadBytesPerSec)} · write ${mbps(machine.diskWriteBytesPerSec)} · ${plainCount(machine.diskIops)} IOPS`} values={history?.disk ?? []} max={traceMax(history?.disk ?? [], undefined, 1.4)} />
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
  return (
    <section className="sy-card sy-tile sy-net" aria-label="Network">
      <div className="sy-top">
        <span className="sy-label">Network</span>
        <span className="sy-faint">last 10 minutes</span>
      </div>
      {network ? (
        <div className="sy-net2">
          <div className="sy-col" style={{ "--m": CYAN } as CSSProperties}>
            <span className="sy-mh"><i />{network.adapter ?? "Network"}</span>
            <span className="sy-v">{mbps(network.inBytesPerSec)}<small> MB/s in</small></span>
            <span className="sy-sub" title={network.adapter ?? undefined}>{[`↑ ${mbps(network.outBytesPerSec)} out`, linkText(network.linkMbps), network.ip].filter(Boolean).join(" · ")}</span>
            <Trace values={overview?.history.netIn ?? []} max={traceMax(overview?.history.netIn ?? [], undefined, 1.4)} colour={CYAN} />
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
