/**
 * Sources (#1098, the approved mockup's option D, the Scoreboard, like for like): everything Deluno talks to as one card.
 *
 * - Indexers today: a table ranked by what each delivered today (Grabs, Results, Response, Status). The whole table scrolls
 *   with sticky headings; a row lights blue while Deluno asks that indexer and green or red when it answers.
 * - Download clients: one stacked speed bar with each client's name on its segment, and a line for each client under it.
 * - Connections today: pills with today's counts; a pill glows when Deluno uses it.
 *
 * The lights come from the pushed events (an indexer asked and answered, a connection used, a processor handed work), never
 * from asking anything while the screen waits. Every row and pill is a link to where it is managed, with its detail on hover
 * and a name that is unique on the page.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import { useNavigate } from "react-router-dom";
import type { DownloadClientItem, IndexerItem } from "../../../lib/api";
import type { DashboardClientRow, DashboardSummary, LiveConnection } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { useLiveEvent } from "../../../lib/use-signalr";
import { clientLights } from "./clients";
import { connectionKey, connectionsSummary, type LiveConnectionKind } from "./connections";
import { pulseOf, type IndexerScoreboardToday } from "./indexers";
import { LiveCard as Panel } from "./live-card";
import { clientBar, connectionPills, scoreRows, scoreSummary, STANDING_WORD } from "./scoreboard";
import { plural } from "../../../lib/plural";
import "./sources-card.css";

const NO_ROWS: never[] = [];

/** How long an answer stays lit on a row, and a use or a hand-off on a pill; and the least a question stays lit, so a quick answer is still seen. */
const LIT_MS = 1400;
const PILL_MS = 2500;
const ASK_MIN_MS = 600;
const ASK_MAX_MS = 15_000;

type Flash = "asked" | "answered" | "failed" | "busy";

const MANAGE: Record<LiveConnectionKind, string> = {
  processor: "/indexers/companion-tools",
  mediaServer: "/indexers/media-servers",
  requests: "/indexers/media-servers",
  subtitles: "/indexers/subtitle-providers",
  lists: "/settings/lists"
};

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
  const rows = Array.isArray(scoreboard?.indexers) ? scoreboard.indexers : null;
  const todayById = useMemo(() => new Map((rows ?? NO_ROWS).map((row) => [row.id, row])), [rows]);
  const lights = useMemo(() => clientLights(clientRows, clients, speeds, seeding), [clientRows, clients, speeds, seeding]);
  const table = useMemo(() => scoreRows(indexers, todayById, now), [indexers, todayById, now]);
  const bar = useMemo(() => clientBar(lights), [lights]);
  const pills = useMemo(() => connectionPills(connections ?? NO_ROWS), [connections]);
  const connectionByKey = useMemo(() => new Map((connections ?? NO_ROWS).map((connection) => [connectionKey(connection), connection])), [connections]);

  // Flashes: a row's or pill's own state for a moment, set by real events ----------------------------------------------
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
  useLiveEvent("ConnectionUsed", useCallback((event) => light(connectionKey(event), "answered", PILL_MS), [light]));
  useLiveEvent("ProcessorHandoffChanged", useCallback((event) => {
    const name = (event.processorName ?? "").toLowerCase();
    const target = processors.current.find((processor) => name !== "" && (processor.name.includes(name) || name.includes(processor.name))) ?? (processors.current.length === 1 ? processors.current[0] : undefined);
    if (target) light(target.key, "busy", PILL_MS);
  }, [light]));

  const go = (to: string) => (event: MouseEvent) => {
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
    event.preventDefault();
    navigate(to);
  };

  return (
    <Panel title="Sources" to="/indexers/indexers" toLabel="Manage" className="ls h-full min-h-0 flex-1">
      <div className="ls-body">
        <section className="ls-sa" aria-label="Indexers today">
          <div className="ls-sa-h">
            <span className="ls-label">Indexers today</span>
            <span className="ls-num" title={indexers.length === 0 ? undefined : scoreSummary(indexers, todayById, summary?.indexers?.queriesToday)}>{indexers.length === 0 ? "none set up" : scoreSummary(indexers, todayById, summary?.indexers?.queriesToday)}</span>
          </div>
          <div className="ls-scroll" data-testid="live-scoreboard">
            <div className="ls-head" aria-hidden>
              <span />
              <span>Indexer</span>
              <span title="Releases Deluno took from this indexer today">Grabs</span>
              <span title="Matching releases this indexer returned across today's searches">Results</span>
              <span>Response</span>
              <span>Status</span>
            </div>
            <div className="ls-rows">
              {table.map((row) => (
                <a
                  key={row.key}
                  href={row.to}
                  title={row.title}
                  aria-label={`${row.name}: ${STANDING_WORD[row.standing]}, ${plural(row.grabs, "grab")}, ${plural(row.results, "result")}`}
                  className={cn("ls-row", row.standing !== "good" && row.standing, flash.get(row.key))}
                  data-indexer={row.id}
                  onClick={go(row.to)}
                >
                  <span className="ls-d" />
                  <span className="ls-nm">{row.name}</span>
                  <span className={cn("ls-n", row.grabs === 0 && "zero")}>{row.grabs.toLocaleString("en-AU")}</span>
                  <span className="ls-n">{row.results.toLocaleString("en-AU")}</span>
                  <span className="ls-ms">
                    {row.ms === null ? "—" : `${row.ms.toLocaleString("en-AU")} ms`}
                    <i><b style={{ width: `${row.speed}%` }} /></i>
                  </span>
                  <span className={cn("ls-st", row.standing)} title={row.standingTitle}>{STANDING_WORD[row.standing]}</span>
                </a>
              ))}
            </div>
          </div>
        </section>

        <section className="ls-sa" aria-label="Download clients">
          <div className="ls-sa-h">
            <span className="ls-label">Download clients</span>
            <span className="ls-num" title={bar.summary}>{bar.summary}</span>
          </div>
          <div className="ls-stack" role="img" aria-label={bar.segments.length === 0 ? "No download clients" : `Download speed by client: ${bar.summary}`}>
            {bar.segments.map((segment) => (
              <i key={segment.id} style={{ width: `${segment.share * 100}%`, background: segment.colour }} title={`${segment.name} · ${segment.speedMbps.toFixed(1)} MB/s`}>
                <span>{segment.named ? segment.name : ""}</span>
              </i>
            ))}
          </div>
          <div className="ls-legend">
            {bar.segments.map((segment) => (
              <a key={segment.id} href="/indexers/download-clients" aria-label={`${segment.name}: ${segment.words}`} onClick={go("/indexers/download-clients")}>
                <em style={{ background: segment.colour }} />
                <b>{segment.name}</b> {segment.words}
              </a>
            ))}
          </div>
        </section>

        <section className="ls-sa" aria-label="Connections today">
          <div className="ls-sa-h">
            <span className="ls-label">Connections today</span>
            <span className="ls-num" title={connectionsSummary(connections ?? NO_ROWS)}>{connectionsSummary(connections ?? NO_ROWS)}</span>
          </div>
          <div className="ls-pills">
            {pills.map((pill) => {
              const connection = connectionByKey.get(pill.key);
              const to = connection ? MANAGE[connection.kind] : "/indexers/companion-tools";
              const state = flash.get(pill.key);
              return (
                <a
                  key={pill.key}
                  href={to}
                  title={pill.title}
                  aria-label={`${pill.title}, ${pill.count.toLocaleString("en-AU")} today`}
                  className={cn("ls-pill", pill.off && "off", pill.down && "down", state === "answered" && "lit", (state === "busy" || (pill.working && !state)) && "busy")}
                  onClick={go(to)}
                >
                  <i />
                  {pill.name} <b>{pill.count.toLocaleString("en-AU")}</b>
                </a>
              );
            })}
          </div>
        </section>
      </div>
    </Panel>
  );
}
