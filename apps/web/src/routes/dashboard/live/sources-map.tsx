/**
 * Sources (#1098): everything Deluno talks to, as one network map. Deluno is in the middle; the indexers are on an arc across
 * the top, the download clients on the lower left and the connections (processors, media servers, Seerr, subtitles, lists) on
 * the lower right, each with a line to Deluno. A circle's colour is its status (green answering, amber slow, red not
 * answering, grey off). When Deluno asks an indexer a dot flies out and the answer flies back; a downloading client streams
 * particles in, faster as it speeds up; a connection that is used gets a pulse. Every movement is a real pushed event or
 * reading (#1097). Each node is a link, with its detail on hover, to where it is managed.
 */
import { useCallback, useEffect, useId, useMemo, useRef, useState, type MouseEvent } from "react";
import { useNavigate } from "react-router-dom";
import { AppMark } from "../../../components/shell/app-mark";
import type { DownloadClientItem, IndexerItem } from "../../../lib/api";
import type { DashboardClientRow, DashboardSummary, LiveConnection } from "../../../lib/api/types/operations";
import { useLiveEvent } from "../../../lib/use-signalr";
import { ago } from "../format";
import { clientLights, clientsSummary } from "./clients";
import { connectionKey, connectionsSummary } from "./connections";
import { useElementSize } from "./hooks";
import { indexersSummary, pulseOf, type IndexerScoreboardToday } from "./indexers";
import { LiveCard as Panel } from "./live-card";
import { MapFx, type DotTone } from "./map-fx";
import { clientNodes, connectionNodes, indexerNodes, layoutMap, type MapGroup, type PlacedNode } from "./map-model";

const NO_ROWS: never[] = [];

const GROUP_TITLE: Record<MapGroup, string> = { indexers: "Indexers", clients: "Download clients", connections: "Connections" };
const GROUP_COLOR: Record<MapGroup, string> = { indexers: "hsl(var(--stage-search))", clients: "hsl(var(--stage-grab))", connections: "hsl(var(--stage-import))" };
const RIPPLE_COLOR: Record<string, string> = { ok: "hsl(var(--success))", warn: "hsl(var(--warning))", bad: "hsl(var(--destructive))", ask: "hsl(var(--info))", work: "hsl(var(--stage-process))" };

/** How long a question takes to fly out, so an answer waits for it to land. */
const OUT_MS = 750;
const RIPPLE_MS = 1100;
/** Where nothing can be measured (a test): a map the size of a typical card. */
const UNMEASURED = { width: 480, height: 420 };

interface Ripple { id: number; key: string; tone: DotTone }

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

/** The Sources card: a header and the map, filling the room it is given. */
export function SourcesCard(props: SourcesProps) {
  const [bodyRef, size] = useElementSize<HTMLDivElement>();
  return (
    <Panel title="Sources" to="/indexers/indexers" toLabel="Manage" className="min-h-[18rem] flex-1" bodyClassName="relative">
      <div ref={bodyRef} className="relative min-h-0 flex-1">
        {size.width > 0 && size.height > 0 ? <SourcesMap {...props} width={size.width} height={size.height} /> : typeof ResizeObserver === "undefined" ? <SourcesMap {...props} width={UNMEASURED.width} height={UNMEASURED.height} /> : null}
      </div>
    </Panel>
  );
}

export function SourcesMap({ indexers, scoreboard, summary, now, clientRows, clients, speeds, seeding, connections, width, height }: SourcesProps & { width: number; height: number }) {
  const navigate = useNavigate();
  const rows = Array.isArray(scoreboard?.indexers) ? scoreboard.indexers : null;
  const todayById = useMemo(() => new Map((rows ?? NO_ROWS).map((row) => [row.id, row])), [rows]);
  const lights = useMemo(() => clientLights(clientRows, clients, speeds, seeding), [clientRows, clients, speeds, seeding]);
  const specs = useMemo(
    () => [
      ...indexerNodes(indexers, todayById, now, (iso) => ago(iso, now)),
      ...clientNodes(lights),
      ...connectionNodes(connections ?? NO_ROWS)
    ],
    [indexers, todayById, now, lights, connections]
  );
  const layout = useMemo(() => layoutMap(width, height, specs), [width, height, specs]);
  const counts = useMemo<Record<MapGroup, string>>(
    () => ({
      indexers: indexers.length === 0 ? "none set up" : indexersSummary(indexers, todayById, summary?.indexers?.queriesToday),
      clients: lights.length === 0 ? "none set up" : clientsSummary(lights),
      connections: connectionsSummary(connections ?? NO_ROWS)
    }),
    [indexers, todayById, summary?.indexers?.queriesToday, lights, connections]
  );

  // The moving dots and the ripples ----------------------------------------------------------------------------------
  const layer = useRef<SVGGElement>(null);
  const fx = useRef<MapFx | null>(null);
  const [busy, setBusy] = useState(false);
  const [ripples, setRipples] = useState<readonly Ripple[]>([]);
  const nextId = useRef(0);
  const asked = useRef(new Map<string, number>());
  const keys = useRef(new Set<string>());
  keys.current = new Set(layout.nodes.map((node) => node.key));
  const processors = useRef<readonly { key: string; name: string }[]>([]);
  processors.current = (connections ?? NO_ROWS).filter((connection) => connection.kind === "processor").map((connection) => ({ key: connectionKey(connection), name: connection.name.toLowerCase() }));

  useEffect(() => {
    const engine = new MapFx();
    fx.current = engine;
    engine.attach(layer.current, setBusy);
    return () => {
      engine.detach();
      fx.current = null;
    };
  }, []);
  useEffect(() => {
    fx.current?.setCurves(new Map(layout.nodes.map((node) => [node.key, node.curve])));
    for (const node of layout.nodes) if (node.group === "clients") fx.current?.setFlow(node.key, node.speedMbps);
  }, [layout]);

  const ripple = useCallback((key: string, tone: DotTone) => {
    if (!keys.current.has(key)) return;
    const id = (nextId.current += 1);
    setRipples((current) => [...current.slice(-12), { id, key, tone }]);
    setTimeout(() => setRipples((current) => current.filter((item) => item.id !== id)), RIPPLE_MS);
  }, []);

  useLiveEvent("IndexerQueryRecorded", useCallback((event) => {
    const key = `indexer:${event.indexerId}`;
    if (!keys.current.has(key)) return;
    if (event.phase === "asked") {
      asked.current.set(key, performance.now());
      fx.current?.trip(key, "out", "ask", 0, OUT_MS);
      return;
    }
    const pulse = pulseOf(event);
    const tone: DotTone = pulse.tone === "bad" ? "bad" : pulse.tone === "warn" ? "warn" : "ok";
    const wait = Math.max(0, OUT_MS - (performance.now() - (asked.current.get(key) ?? 0)));
    asked.current.delete(key);
    // A failure never comes back: the circle flashes red where the question stopped. An answer flies home once the question has landed.
    if (tone === "bad") setTimeout(() => ripple(key, "bad"), wait);
    else {
      fx.current?.trip(key, "back", tone, wait, 650);
      setTimeout(() => ripple(key, tone), wait);
    }
  }, [ripple]));

  useLiveEvent("ConnectionUsed", useCallback((event) => {
    const key = connectionKey(event);
    fx.current?.trip(key, "out", "work", 0, 700);
    setTimeout(() => ripple(key, "work"), 650);
  }, [ripple]));

  useLiveEvent("ProcessorHandoffChanged", useCallback((event) => {
    const name = (event.processorName ?? "").toLowerCase();
    const target = processors.current.find((processor) => name !== "" && (processor.name.includes(name) || name.includes(processor.name))) ?? (processors.current.length === 1 ? processors.current[0] : undefined);
    if (!target) return;
    fx.current?.trip(target.key, "out", "work", 0, 700);
    setTimeout(() => ripple(target.key, "work"), 650);
  }, [ripple]));

  useLiveEvent("ClientStateChanged", useCallback((event) => {
    ripple(`client:${event.clientId}`, event.reachable ? "ok" : "bad");
  }, [ripple]));

  // Hover / focus ---------------------------------------------------------------------------------------------------------
  const [hover, setHover] = useState<string | null>(null);
  const hovered = hover ? layout.nodes.find((node) => node.key === hover) : undefined;
  const clipId = useId();
  const moving = layout.nodes.some((node) => node.speedMbps > 0);

  const go = (node: PlacedNode) => (event: MouseEvent) => {
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
    event.preventDefault();
    navigate(node.to);
  };

  const { hub } = layout;
  const hubBusy = busy || moving;
  return (
    <>
      <svg width={layout.width} height={layout.height} viewBox={`0 0 ${layout.width} ${layout.height}`} role="group" aria-label="Sources map" className="absolute inset-0 block">
        <defs>
          <radialGradient id={`${clipId}-glow`}>
            <stop offset="0%" stopColor="hsl(var(--primary))" stopOpacity="0.38" />
            <stop offset="100%" stopColor="hsl(var(--primary))" stopOpacity="0" />
          </radialGradient>
          <clipPath id={`${clipId}-hub`}>
            <circle cx={hub.x} cy={hub.y} r={hub.r} />
          </clipPath>
        </defs>
        {/* Lines first, so the circles sit on them. */}
        {layout.nodes.map((node) => {
          const { from, control, to } = node.curve;
          const flowing = node.speedMbps > 0;
          return (
            <path
              key={`line-${node.key}`}
              className="map-line"
              d={`M${from.x.toFixed(1)} ${from.y.toFixed(1)} Q${control.x.toFixed(1)} ${control.y.toFixed(1)} ${to.x.toFixed(1)} ${to.y.toFixed(1)}`}
              stroke={node.off ? "hsl(var(--muted-foreground))" : GROUP_COLOR[node.group]}
              strokeOpacity={node.off ? 0.28 : flowing ? 0.85 : 0.3}
              strokeWidth={flowing ? 1.8 : 1}
              strokeDasharray={node.off ? "3 4" : undefined}
            />
          );
        })}
        <g data-testid="map-dots" ref={layer} pointerEvents="none" />
        {/* Deluno. */}
        <circle cx={hub.x} cy={hub.y} r={hub.r * 2.7} fill={`url(#${clipId}-glow)`} pointerEvents="none" />
        <circle className="map-hub-ring" data-busy={hubBusy ? "" : undefined} cx={hub.x} cy={hub.y} r={hub.r + 2} pointerEvents="none" />
        <g clipPath={`url(#${clipId}-hub)`} pointerEvents="none">
          <g transform={`translate(${hub.x - hub.r} ${hub.y - hub.r})`}>
            <AppMark size={hub.r * 2} />
          </g>
        </g>
        <circle cx={hub.x} cy={hub.y} r={hub.r} fill="none" stroke="hsl(var(--primary))" strokeOpacity={hubBusy ? 0.9 : 0.5} strokeWidth="1.5" pointerEvents="none" />
        {/* Group labels. */}
        {layout.groups.map((group) => (
          <g key={group.group} pointerEvents="none">
            <circle cx={group.anchor === "start" ? group.x + 4 : group.x - 4} cy={group.y - 4} r="3.5" fill={GROUP_COLOR[group.group]} />
            <text className="map-group" x={group.anchor === "start" ? group.x + 14 : group.x - 14} y={group.y} textAnchor={group.anchor}>{GROUP_TITLE[group.group]}</text>
            <text className="map-count" x={group.anchor === "start" ? group.x + 14 : group.x - 14} y={group.y + 14} textAnchor={group.anchor}>{counts[group.group]}</text>
          </g>
        ))}
        {/* The nodes. */}
        {layout.nodes.map((node) => (
          <a
            key={node.key}
            href={node.to}
            aria-label={node.label}
            className="map-node"
            data-tone={node.tone}
            data-off={node.off ? "" : undefined}
            data-group={node.group}
            onClick={go(node)}
            onMouseEnter={() => setHover(node.key)}
            onMouseLeave={() => setHover((current) => (current === node.key ? null : current))}
            onFocus={() => setHover(node.key)}
            onBlur={() => setHover((current) => (current === node.key ? null : current))}
          >
            <circle className="map-glow" cx={node.x} cy={node.y} r={node.r * (node.speedMbps > 0 ? 1.9 : 1.5)} />
            <circle className="map-disc" cx={node.x} cy={node.y} r={node.r} />
            <circle className="map-focus" cx={node.x} cy={node.y} r={node.r + 4} />
            {ripples.filter((item) => item.key === node.key).map((item) => (
              <circle key={item.id} className="map-ripple" style={{ ["--ripple" as string]: RIPPLE_COLOR[item.tone] }} cx={node.x} cy={node.y} r={node.r} />
            ))}
            <text transform={`rotate(${node.text.rotate.toFixed(1)} ${node.text.x.toFixed(1)} ${node.text.y.toFixed(1)})`} x={node.text.x} y={node.text.y} dy="0.35em" textAnchor={node.text.anchor}>
              {node.text.numberFirst ? <tspan className="map-num">{node.text.number} </tspan> : null}
              <tspan className="map-name">{node.text.name}</tspan>
              {node.text.numberFirst ? null : <tspan className="map-num" dx="5">{node.text.number}</tspan>}
            </text>
            {/* A larger target than the circle and its words, so the node is easy to hit. */}
            <circle cx={node.x} cy={node.y} r={node.r + 6} fill="transparent" />
          </a>
        ))}
      </svg>
      {hovered ? (
        <div
          role="presentation"
          aria-hidden
          className="pointer-events-none absolute z-10 w-max max-w-[16rem] -translate-x-1/2 rounded-lg border border-hairline bg-popover px-2.5 py-1.5 text-[length:var(--type-caption)] leading-snug text-popover-foreground shadow-md"
          style={{
            left: Math.max(100, Math.min(layout.width - 100, hovered.x)),
            top: hovered.y > layout.height / 2 ? undefined : hovered.y + hovered.r + 8,
            bottom: hovered.y > layout.height / 2 ? layout.height - hovered.y + hovered.r + 8 : undefined
          }}
        >
          <span className="block font-semibold text-foreground">{hovered.name}</span>
          {hovered.detail.map((line) => <span key={line} className="block text-muted-foreground">{line}</span>)}
        </div>
      ) : null}
    </>
  );
}
