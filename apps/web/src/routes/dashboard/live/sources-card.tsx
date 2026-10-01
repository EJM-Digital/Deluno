/**
 * Sources (#1098): everything Deluno talks to as one card, the approved mockup's tiles view. Indexers, download clients and
 * connections (processors, media servers, Seerr, subtitles, lists) are three groups divided by hairlines; every tile is the
 * same height and the height comes from the card's own. A tile flashes when Deluno talks to it, from the same pushed events
 * the map used: an indexer being asked (blue ring), answering (green) or failing (red), a connection used (green), a
 * processor taking a hand-off (glow), a client moving data (its dot pulses and its bar fills). Each tile is a link to where it
 * is managed, with its detail on hover and a name that is unique on the page.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import type { DownloadClientItem, IndexerItem } from "../../../lib/api";
import type { DashboardClientRow, DashboardSummary, LiveConnection } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { useLiveEvent } from "../../../lib/use-signalr";
import { ago } from "../format";
import { clientLights, clientsSummary } from "./clients";
import { connectionKey, connectionsSummary } from "./connections";
import { useElementSize } from "./hooks";
import { indexersSummary, pulseOf, type IndexerScoreboardToday } from "./indexers";
import { LiveCard as Panel } from "./live-card";
import { clientNodes, connectionNodes, indexerNodes, tileLayout, TILE_SNUG, TILE_TIGHT, type SourceGroup, type SourceNode } from "./source-tiles";
import "./sources-card.css";

const NO_ROWS: never[] = [];

const GROUP_TITLE: Record<SourceGroup, string> = { indexers: "Indexers", clients: "Download clients", connections: "Connections" };
/** How long an answer, a use or a hand-off stays lit; and the least a question stays lit, so a quick answer is still seen. */
const LIT_MS = 1600;
const ASK_MIN_MS = 600;
const ASK_MAX_MS = 15_000;
/** A client's bar is its speed against this, or against the fastest client now if that is more. */
const BAR_FULL_MBPS = 15;

type Flash = "asked" | "answered" | "failed" | "busy";

export interface SourcesProps {
  indexers: IndexerItem[];
  scoreboard?: IndexerScoreboardToday | null;
  summary: DashboardSummary | undefined;
  now: number;
  clientRows: DashboardClientRow[];
  clients: DownloadClientItem[];
  speeds: ReadonlyMap<string, number>;
  seeding: ReadonlyMap<string, number>;
  connections: readonly LiveConnection[] | null;
}

export function SourcesCard({ indexers, scoreboard, summary, now, clientRows, clients, speeds, seeding, connections }: SourcesProps) {
  const navigate = useNavigate();
  const [bodyRef, size] = useElementSize<HTMLDivElement>();
  const rows = Array.isArray(scoreboard?.indexers) ? scoreboard.indexers : null;
  const todayById = useMemo(() => new Map((rows ?? NO_ROWS).map((row) => [row.id, row])), [rows]);
  const lights = useMemo(() => clientLights(clientRows, clients, speeds, seeding), [clientRows, clients, speeds, seeding]);
  const groups = useMemo<Record<SourceGroup, SourceNode[]>>(
    () => ({
      indexers: indexerNodes(indexers, todayById, now, (iso) => ago(iso, now)),
      clients: clientNodes(lights),
      connections: connectionNodes(connections ?? NO_ROWS)
    }),
    [indexers, todayById, now, lights, connections]
  );
  const counts: Record<SourceGroup, string> = {
    indexers: indexers.length === 0 ? "none set up" : indexersSummary(indexers, todayById, summary?.indexers?.queriesToday),
    clients: lights.length === 0 ? "none set up" : clientsSummary(lights),
    connections: connectionsSummary(connections ?? NO_ROWS)
  };

  // The tile height: one for every group, from the card's measured height (the mockup's rule).
  const { columns, tile } = tileLayout(size.width, size.height, [groups.indexers.length, groups.clients.length, groups.connections.length]);
  const tight = tile < TILE_TIGHT;
  const snug = tile < TILE_SNUG;
  const fastest = Math.max(BAR_FULL_MBPS, ...groups.clients.map((node) => node.speedMbps));

  // Flashes: the tile's own state for a moment, set by real events ----------------------------------------------------
  const [flash, setFlash] = useState<ReadonlyMap<string, Flash>>(() => new Map());
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  const askedAt = useRef(new Map<string, number>());
  useEffect(() => {
    const live = timers.current;
    return () => {
      for (const timer of live.values()) clearTimeout(timer);
      live.clear();
    };
  }, []);
  const light = useCallback((key: string, state: Flash, ms: number | null = LIT_MS) => {
    const pending = timers.current.get(key);
    if (pending) clearTimeout(pending);
    setFlash((current) => new Map(current).set(key, state));
    timers.current.set(key, setTimeout(() => {
      timers.current.delete(key);
      setFlash((current) => {
        if (!current.has(key)) return current;
        const next = new Map(current);
        next.delete(key);
        return next;
      });
    }, ms ?? ASK_MAX_MS));
  }, []);

  const processors = useRef<readonly { key: string; name: string }[]>([]);
  processors.current = (connections ?? NO_ROWS).filter((connection) => connection.kind === "processor").map((connection) => ({ key: connectionKey(connection), name: connection.name.toLowerCase() }));

  useLiveEvent("IndexerQueryRecorded", useCallback((event) => {
    const key = `indexer:${event.indexerId}`;
    if (event.phase === "asked") {
      askedAt.current.set(key, performance.now());
      light(key, "asked", null);
      return;
    }
    const wait = Math.max(0, ASK_MIN_MS - (performance.now() - (askedAt.current.get(key) ?? -ASK_MIN_MS)));
    askedAt.current.delete(key);
    const failed = pulseOf(event).tone === "bad";
    setTimeout(() => light(key, failed ? "failed" : "answered"), wait);
  }, [light]));
  useLiveEvent("ConnectionUsed", useCallback((event) => light(connectionKey(event), "answered"), [light]));
  useLiveEvent("ProcessorHandoffChanged", useCallback((event) => {
    const name = (event.processorName ?? "").toLowerCase();
    const target = processors.current.find((processor) => name !== "" && (processor.name.includes(name) || name.includes(processor.name))) ?? (processors.current.length === 1 ? processors.current[0] : undefined);
    if (target) light(target.key, "busy");
  }, [light]));
  useLiveEvent("ClientStateChanged", useCallback((event) => light(`client:${event.clientId}`, event.reachable ? "answered" : "failed"), [light]));

  const go = (node: SourceNode) => (event: MouseEvent) => {
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
    event.preventDefault();
    navigate(node.to);
  };

  const tileClass = (node: SourceNode, base: "ls-ix" | "ls-cl") =>
    cn(
      base,
      node.group === "connections" && "ls-cn",
      node.off ? "off" : node.tone === "bad" ? "down" : node.tone === "warn" ? "slow" : "",
      flash.get(node.key) ?? (node.working ? "busy" : ""),
      node.group === "clients" && node.speedMbps > 0 && "moving"
    );
  const link = (node: SourceNode, base: "ls-ix" | "ls-cl", body: ReactNode) => (
    <a key={node.key} href={node.to} aria-label={node.label} title={[node.name, ...node.detail].join("\n")} className={tileClass(node, base)} data-tone={node.tone} data-off={node.off ? "" : undefined} onClick={go(node)}>
      <span className="ls-n"><span className="ls-d" /><span>{node.name}</span></span>
      {body}
    </a>
  );

  return (
    <Panel title="Sources" to="/indexers/indexers" toLabel="Manage" bare className={cn("ls h-full min-h-0 flex-1", tight && "tight", snug && "snug")}>
      <div ref={bodyRef} className="ls-body" style={{ ["--ls-tile-h" as string]: `${tile}px`, ["--ls-cols" as string]: columns }}>
        {(Object.keys(GROUP_TITLE) as SourceGroup[]).map((group) => (
          <section key={group} className="ls-sg" data-group={group}>
            <div className="ls-sg-h"><span className="ls-label">{GROUP_TITLE[group]}</span><span className="ls-num" title={counts[group]}>{counts[group]}</span></div>
            <div className="ls-grid">
              {groups[group].map((node) => {
                if (group === "clients") {
                  const speed = /^([\d.,]+) (MB\/s)(.*)$/.exec(node.sub);
                  return link(node, "ls-cl", (
                    <>
                      <span className="ls-v">{speed ? <><b>{speed[1]}</b> {speed[2]}{speed[3]}</> : node.sub}</span>
                      <span className="ls-sb"><i style={{ width: `${Math.min(100, (node.speedMbps / fastest) * 100)}%` }} /></span>
                    </>
                  ));
                }
                return link(node, "ls-ix", <span className="ls-m">{node.sub}</span>);
              })}
            </div>
          </section>
        ))}
      </div>
    </Panel>
  );
}
