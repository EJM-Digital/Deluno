/**
 * Connections (#1098), from the Health row down on the right: a one-line summary, Test all, and every connection in
 * tinted groups with a Connection | Checked pair of columns, red, amber or green group badges, fixed-height rows and a
 * Test button on each row (shown on hover or focus). A row lights blue while it is tested, then green or red, and a
 * connection Deluno checked on its own (ConnectionChecked) lights the same way.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from "react";
import { Link } from "react-router-dom";
import type { ConnectionCheckedEvent } from "../../../lib/api/types/system-overview";
import { fetchJson } from "../../../lib/api";
import type { ConnectionCheckItem } from "../../../lib/api/types/operations";
import { authedFetch } from "../../../lib/use-auth";
import { useLiveEvent } from "../../../lib/use-signalr";
import { ago } from "../format";
import { ConnectionVersionBeside } from "../../../components/app/connection-version";
import { useConnectionVersions, versionKey, type ConnectionVersions } from "../../../lib/connection-versions";
import { connectionsLine, connKey, groupConnections, resultOfEvent, resultOfTest, type ConnResult, type ConnRow } from "./connections-model";

/** How many connections the legacy per-connection Test all runs at once (the server's own Test all does the same). */
const AT_ONCE = 4;
const PASS_MS = 1200;

export interface ConnectionTesting {
  results: ReadonlyMap<string, ConnResult>;
  checking: ReadonlySet<string>;
  passed: ReadonlySet<string>;
  test: (row: ConnRow) => Promise<void>;
  testAll: (rows: readonly ConnRow[]) => Promise<void>;
  allBusy: boolean;
  /** What each connection reports as its version, and the newer one when it is out (#1117). */
  versions?: ConnectionVersions;
}

/** The newest answer for each connection, who is being tested now, and who just passed. */
export function useConnectionTesting(): ConnectionTesting {
  const [results, setResults] = useState<ReadonlyMap<string, ConnResult>>(() => new Map());
  const [checking, setChecking] = useState<ReadonlySet<string>>(() => new Set());
  const [passed, setPassed] = useState<ReadonlySet<string>>(() => new Set());
  const [allBusy, setAllBusy] = useState(false);
  const { versions } = useConnectionVersions();
  const timers = useRef<Array<ReturnType<typeof setTimeout>>>([]);
  useEffect(() => () => timers.current.forEach(clearTimeout), []);

  const note = useCallback((key: string, result: ConnResult) => {
    setResults((current) => new Map(current).set(key, result));
    if (result.state === "down") return;
    setPassed((current) => new Set(current).add(key));
    timers.current.push(setTimeout(() => setPassed((current) => { const next = new Set(current); next.delete(key); return next; }), PASS_MS));
  }, []);

  // What Deluno last really checked, whoever asked (the regular check, Test all, a Test button), so a connection with
  // no reading of its own (a list, the metadata service) still shows its true "checked" time.
  useEffect(() => {
    let live = true;
    fetchJson<ConnectionCheckItem[]>("/api/connections/checks")
      .then((items) => {
        if (!live || !Array.isArray(items)) return;
        setResults((current) => {
          const next = new Map(current);
          for (const item of items) {
            const key = connKey(item.kind, item.id);
            const seen = next.get(key);
            if (!seen || Date.parse(item.checkedUtc) > Date.parse(seen.checkedUtc)) next.set(key, resultOfEvent(item));
          }
          return next;
        });
      })
      .catch(() => undefined);
    return () => { live = false; };
  }, []);

  // A result pushed by the server (the regular check, Test all, a Test button): the row shows it and stops lighting.
  useLiveEvent("ConnectionChecked", useCallback((event) => {
    const key = connKey(event.kind, event.id);
    note(key, resultOfEvent(event));
    setChecking((current) => { if (!current.has(key)) return current; const next = new Set(current); next.delete(key); return next; });
  }, [note]));

  const test = useCallback(async (row: ConnRow) => {
    if (!row.testPath) return;
    setChecking((current) => new Set(current).add(row.key));
    try {
      const response = await authedFetch(row.testPath, { method: "POST" });
      const body = (await response.json().catch(() => null)) as { healthStatus?: string; message?: string } | null;
      note(row.key, response.ok ? resultOfTest(body, new Date().toISOString()) : { state: "down", message: body?.message ?? "the test did not go through", checkedUtc: new Date().toISOString() });
    } catch {
      note(row.key, { state: "down", message: "Deluno could not run the test", checkedUtc: new Date().toISOString() });
    } finally {
      setChecking((current) => { const next = new Set(current); next.delete(row.key); return next; });
    }
  }, [note]);

  /**
   * Test all: the server tests every connection that is switched on, a few at a time, and pushes each answer as it
   * finishes (`POST /api/connections/test-all`); the rows it does not know (subtitle sources) are tested here one by
   * one. A server from before that endpoint is tested row by row, as before.
   */
  const testAll = useCallback(async (rows: readonly ConnRow[]) => {
    const queue = rows.filter((row) => row.state !== "off" && row.testPath);
    const serverKnows = rows.filter((row) => row.state !== "off" && row.kind !== "subtitles");
    setAllBusy(true);
    setChecking((current) => new Set([...current, ...serverKnows.map((row) => row.key)]));
    try {
      let tested = false;
      try {
        const response = await authedFetch("/api/connections/test-all", { method: "POST" });
        if (response.ok) {
          tested = true;
          const body = (await response.json().catch(() => null)) as { results?: ConnectionCheckedEvent[] } | null;
          for (const result of body?.results ?? []) note(connKey(result.kind, result.id), resultOfEvent(result));
        }
      } catch {
        tested = false;
      }
      setChecking((current) => { const next = new Set(current); for (const row of serverKnows) next.delete(row.key); return next; });
      const rest = tested ? queue.filter((row) => row.kind === "subtitles") : queue;
      for (let at = 0; at < rest.length; at += AT_ONCE) await Promise.all(rest.slice(at, at + AT_ONCE).map(test));
    } finally {
      setChecking((current) => { const next = new Set(current); for (const row of serverKnows) next.delete(row.key); return next; });
      setAllBusy(false);
    }
  }, [test, note]);

  return { results, checking, passed, test, testAll, allBusy, versions };
}

const stateWords = { ok: "", slow: " sy-slow", down: " sy-down", off: " sy-off" } as const;

export function ConnectionsCard({ rows, testing, now }: { rows: ConnRow[]; testing: ConnectionTesting; now: number }): JSX.Element {
  const groups = useMemo(() => groupConnections(rows), [rows]);
  return (
    <section className="sy-card sy-conn" aria-label="Connections">
      <div className="sy-card-h sy-conn-h">
        <div>
          <h2>Connections</h2>
          <span className="sy-muted sy-num">{connectionsLine(rows)}</span>
        </div>
        <button type="button" className="sy-btn sy-tall" disabled={testing.allBusy || rows.length === 0} onClick={() => void testing.testAll(rows)} aria-label="Test all connections" title="Asks every connection that is switched on, a few at a time, and shows each answer here.">
          {testing.allBusy ? "Testing…" : "Test all"}
        </button>
        <Link className="sy-link" to="/indexers/indexers" aria-label="Manage connections">Manage →</Link>
      </div>
      <div className="sy-scroll" style={{ padding: "0 10px 12px" }}>
        <div className="sy-ct-head"><span /><span>Connection</span><span>Checked</span><span /></div>
        {groups.length === 0 ? <div className="sy-empty sy-muted">Nothing is connected yet. Add an indexer or a download client and it shows here.</div> : null}
        {groups.map((group) => (
          <div key={group.key}>
            <div className={`sy-ct-grp sy-${group.tone}`}><span>{group.title}</span><span>{group.badge}</span><span /></div>
            {group.rows.map((row) => {
              const busy = testing.checking.has(row.key);
              return (
                <div key={row.key} className={`sy-ct-row${stateWords[row.state]}${busy ? " sy-checking" : ""}${testing.passed.has(row.key) ? " sy-passed" : ""}`} title={`${row.name}: ${row.sub}`}>
                  <span className="sy-d" />
                  <span className="sy-nm"><b>{row.name}<ConnectionVersionBeside info={testing.versions?.get(versionKey(row.kind === "client" ? "downloadClient" : row.kind, row.id))} /></b><span>{row.sub}</span></span>
                  <span className="sy-r">{row.state === "off" ? "—" : busy ? "testing…" : row.checkedUtc ? ago(row.checkedUtc, now) : "not yet"}</span>
                  {row.state !== "off" && row.testPath ? (
                    <button type="button" className="sy-test" aria-label={`Test ${row.name}`} disabled={busy} onClick={() => void testing.test(row)}>{busy ? "…" : "Test"}</button>
                  ) : <span />}
                </div>
              );
            })}
          </div>
        ))}
      </div>
    </section>
  );
}

