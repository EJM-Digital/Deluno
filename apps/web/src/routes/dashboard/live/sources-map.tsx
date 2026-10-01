/**
 * Sources (#1098): everything Deluno talks to, as one readable map. Deluno is in the middle; the indexers are a list on the
 * left, the download clients and connections (processors, media servers, Seerr, subtitles, lists) are lists on the right, and a
 * curved line runs from each row's status dot into Deluno. A dot's colour is its status (green answering, amber slow, red not
 * answering, grey off). When Deluno asks an indexer a dot flies out along its line and the answer flies back; a downloading
 * client streams particles in, faster as it speeds up; a connection that is used gets a pulse. Every movement is a real pushed
 * event or reading (#1097). Each row is a link to where it is managed, with its detail on hover.
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
import { clientNodes, connectionNodes, indexerNodes, layoutMap, type MapGroup, type MapSide, type PlacedNode } from "./map-model";
import "./sources-map.css";

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
  // A phone is narrower than the app's tablet size; its two lists stack and the hub goes.
  const phone = typeof window !== "undefined" && window.innerWidth < 640 && width < 460;
  const [scroll, setScroll] = useState<Record<MapSide, number>>({ left: 0, right: 0, stack: 0 });
  const layout = useMemo(() => layoutMap(width, height, specs, scroll, phone), [width, height, specs, scroll, phone]);
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
    fx.current?.setCurves(new Map(layout.mode === "fan" ? layout.nodes.map((node) => [node.key, node.curve]) : []));
    for (const node of layout.nodes) if (node.group === "clients") fx.current?.setFlow(node.key, layout.mode === "fan" ? node.speedMbps : 0);
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
  const clipId = useId();
  const moving = layout.nodes.some((node) => node.speedMbps > 0);

  const go = (node: PlacedNode) => (event: MouseEvent) => {
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
    event.preventDefault();
    navigate(node.to);
  };

  const { hub } = layout;
  const fan = layout.mode === "fan";
  const hubBusy = busy || moving;
  const litKeys = new Set(ripples.map((item) => item.key));
  return (
    <>
      {layout.columns.map((column) => (
        <div
          key={column.side}
          className="map-col"
          data-testid={`map-list-${column.side}`}
          style={{ left: column.left, top: column.top, width: column.width, height: column.height }}
          onScroll={(event) => {
            const top = Math.round(event.currentTarget.scrollTop);
            setScroll((current) => (current[column.side] === top ? current : { ...current, [column.side]: top }));
          }}
        >
          <div className="map-inner" data-narrow={column.narrow ? "" : undefined} style={{ height: column.innerHeight }}>
            {column.heads.map((head) => (
              <div key={head.group} className="map-head" style={{ top: head.top, height: column.headH }}>
                <span className="map-head-title" style={{ color: GROUP_COLOR[head.group] }}>
                  <i />
                  <span className="text-foreground">{GROUP_TITLE[head.group]}</span>
                </span>
                <span className="map-head-count">{counts[head.group]}</span>
              </div>
            ))}
            {layout.nodes.filter((node) => node.side === column.side).map((node) => (
              <a
                key={node.key}
                href={node.to}
                aria-label={node.label}
                title={[node.name, ...node.detail].join("\n")}
                className="map-row"
                style={{ top: node.top, height: node.height }}
                data-tone={node.tone}
                data-off={node.off ? "" : undefined}
                data-group={node.group}
                data-side={column.side === "left" ? "left" : "right"}
                data-compact={node.compact ? "" : undefined}
                data-hot={hover === node.key || litKeys.has(node.key) ? "" : undefined}
                onClick={go(node)}
                onMouseEnter={() => setHover(node.key)}
                onMouseLeave={() => setHover((current) => (current === node.key ? null : current))}
                onFocus={() => setHover(node.key)}
                onBlur={() => setHover((current) => (current === node.key ? null : current))}
              >
                <span className="map-dot">
                  {ripples.filter((item) => item.key === node.key).map((item) => (
                    <span key={item.id} className="map-ripple" style={{ ["--ripple" as string]: RIPPLE_COLOR[item.tone] }} />
                  ))}
                </span>
                <span className="map-text">
                  <span className="map-name">{node.name}</span>
                  <span className="map-sub">{node.sub}</span>
                </span>
              </a>
            ))}
          </div>
        </div>
      ))}
      <svg width={layout.width} height={layout.height} viewBox={`0 0 ${layout.width} ${layout.height}`} role="group" aria-label="Sources map" className="pointer-events-none absolute inset-0 block">
        <defs>
          <radialGradient id={`${clipId}-glow`}>
            <stop offset="0%" stopColor="hsl(var(--primary))" stopOpacity="0.38" />
            <stop offset="100%" stopColor="hsl(var(--primary))" stopOpacity="0" />
          </radialGradient>
          <clipPath id={`${clipId}-hub`}>
            <circle cx={hub.x} cy={hub.y} r={hub.r} />
          </clipPath>
        </defs>
        {fan ? layout.nodes.map((node) => {
          const { from, control, to } = node.curve;
          const active = node.speedMbps > 0 || hover === node.key || litKeys.has(node.key);
          return (
            <path
              key={`line-${node.key}`}
              className="map-line"
              d={`M${from.x.toFixed(1)} ${from.y.toFixed(1)} Q${control.x.toFixed(1)} ${control.y.toFixed(1)} ${to.x.toFixed(1)} ${to.y.toFixed(1)}`}
              stroke={node.off ? "hsl(var(--muted-foreground))" : GROUP_COLOR[node.group]}
              strokeOpacity={node.off ? (active ? 0.5 : 0.22) : active ? 0.9 : 0.3}
              strokeWidth={active ? 2 : 1.2}
              strokeDasharray={node.off ? "3 4" : undefined}
            />
          );
        }) : null}
        <g data-testid="map-dots" ref={layer} pointerEvents="none" />
        {fan ? (
          <g pointerEvents="none">
            <circle cx={hub.x} cy={hub.y} r={hub.r * 2.7} fill={`url(#${clipId}-glow)`} />
            <circle className="map-hub-ring" data-busy={hubBusy ? "" : undefined} cx={hub.x} cy={hub.y} r={hub.r + 2} />
            <g clipPath={`url(#${clipId}-hub)`}>
              <g transform={`translate(${hub.x - hub.r} ${hub.y - hub.r})`}>
                <AppMark size={hub.r * 2} />
              </g>
            </g>
            <circle cx={hub.x} cy={hub.y} r={hub.r} fill="none" stroke="hsl(var(--primary))" strokeOpacity={hubBusy ? 0.9 : 0.5} strokeWidth="1.5" />
            <text className="map-hub-label" x={hub.x} y={hub.y + hub.r + 17} textAnchor="middle">Deluno</text>
          </g>
        ) : null}
      </svg>
    </>
  );
}
