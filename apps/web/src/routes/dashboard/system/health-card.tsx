/**
 * Health (#1098), full width of the left column: a ribbon of areas (each sized to its name, a red, amber or green dot and
 * how many of its checks pass) over the problems, each with its area, how long it has been wrong and two equal-width
 * buttons whose short labels say what happens (the full sentence is on hover). Pressing an area lists all of its checks;
 * "N need you" on This instance scrolls here and rings the card.
 */
import { useEffect, useRef, useState, type CSSProperties, type JSX } from "react";
import { Link } from "react-router-dom";
import { FilterChips } from "../../../components/ui/filter-chips";
import { useLiveEvent } from "../../../lib/use-signalr";
import { ago } from "../format";
import { useAction, send, type ActionState } from "../downloads-kit";
import { areaName, plainCount, span, type HealthArea } from "./logic";
import type { HealthModel, HealthRow, ProblemButton } from "./problems";

const TONE_VAR = { bad: "var(--sy-danger)", warn: "var(--sy-warning)", ok: "var(--sy-success)" } as const;

function ProblemButtonView({ button, row, action }: { button: ProblemButton; row: HealthRow; action: ActionState }): JSX.Element {
  const name = `${button.label}: ${row.title}`;
  if (button.to) {
    return <Link className="sy-btn" to={button.to} title={button.note} aria-label={name}>{button.label}</Link>;
  }
  const post = button.post;
  const key = `${row.id}:${button.label}`;
  return (
    <button
      type="button"
      className="sy-btn"
      title={button.note}
      aria-label={name}
      aria-disabled={action.busy !== null}
      onClick={() => {
        if (!post || action.busy !== null) return;
        void action.run(key, async () => {
          await send(post.path, post.failure, { method: post.method, body: post.body });
          return post.done;
        });
      }}
    >
      {button.label}
    </button>
  );
}

function Row({ row, action, now }: { row: HealthRow; action: ActionState; now: number }): JSX.Element {
  const problem = row.tone === "bad" || row.tone === "warn";
  const tone = row.tone === "idle" ? "ok" : row.tone;
  const age = row.atUtc ? span(Math.max(0, now - Date.parse(row.atUtc))) : null;
  return (
    <li className={`sy-chk sy-${tone}`} style={row.tone === "idle" ? ({ opacity: 0.75 } satisfies CSSProperties) : undefined}>
      <span className="sy-d" style={row.tone === "idle" ? { background: "var(--sy-off)" } : undefined} />
      <span className="sy-t">
        <b title={row.title}>{row.title}</b>
        <span>{row.why}</span>
        {problem || row.note ? (
          <em className="sy-meta2">
            <i>{areaName(row.area)}</i>
            {row.note ?? (age ? `${row.tone === "bad" ? "for" : "since"} ${age}` : "")}
          </em>
        ) : null}
      </span>
      {row.buttons.length > 0 ? (
        <>
          <ProblemButtonView button={row.buttons[0]} row={row} action={action} />
          {row.buttons[1] ? <ProblemButtonView button={row.buttons[1]} row={row} action={action} /> : <span className="sy-gap" />}
        </>
      ) : row.tone === "ok" && row.atUtc ? (
        <span className="sy-when">checked {ago(row.atUtc, now)}</span>
      ) : null}
    </li>
  );
}

export function HealthCard({ model, hasChecks, jump, now }: { model: HealthModel; hasChecks: boolean; jump: number; now: number }): JSX.Element {
  const action = useAction();
  const [filter, setFilter] = useState<HealthArea | null>(null);
  const [flash, setFlash] = useState(false);
  const [sheen, setSheen] = useState(false);
  const card = useRef<HTMLElement>(null);
  const list = useRef<HTMLUListElement>(null);

  // "N need you" on This instance: show the problems, scroll here and ring the card for a moment.
  useEffect(() => {
    if (jump === 0) return;
    setFilter(null);
    if (list.current) list.current.scrollTop = 0;
    card.current?.scrollIntoView?.({ block: "nearest", behavior: "smooth" });
    setFlash(false);
    const start = setTimeout(() => setFlash(true), 0);
    const stop = setTimeout(() => setFlash(false), 1300);
    return () => { clearTimeout(start); clearTimeout(stop); };
  }, [jump]);

  // A health check re-ran: every area sheens once.
  useLiveEvent("HealthChanged", () => {
    setSheen(true);
    setTimeout(() => setSheen(false), 1400);
  });

  const rows = model.rows.filter((row) => (filter ? row.area === filter : row.tone === "bad" || row.tone === "warn" || row.id.startsWith("snoozed:")));
  const summary = hasChecks ? `${plainCount(model.pass)} of ${plainCount(model.total)} pass · ${plainCount(model.need)} need you` : "";

  return (
    <section id="system-health" ref={card} className={`sy-card sy-health${flash ? " sy-flash" : ""}`} aria-label="Health">
      <div className="sy-card-h">
        <h2>Health</h2>
        <span className="sy-muted sy-num">{summary}</span>
        <Link className="sy-link" to="/system" aria-label="Open all health checks">All checks →</Link>
      </div>
      <div className="sy-areas">
        <FilterChips<string>
          label="Health areas"
          size="sm"
          value={filter ?? ""}
          // Pressing the chosen area again goes back to the problems, as the ribbon always did.
          onValueChange={(key) => setFilter((current) => (current === key ? null : (key as HealthArea)))}
          options={model.tallies.map((tally) => ({
            value: tally.key,
            label: tally.name,
            count: `${tally.ok}/${tally.total}`,
            dot: TONE_VAR[tally.tone],
            title: `${tally.name}: ${tally.ok} of ${tally.total} pass`
          }))}
        />
      </div>
      <ul className="sy-checklist sy-scroll" ref={list}>
        {rows.map((row) => <Row key={row.id} row={row} action={action} now={now} />)}
        {rows.length === 0 ? <li className="sy-empty">{hasChecks ? (filter ? `Nothing to show for ${areaName(filter)}.` : "Nothing is wrong. Deluno checks everything every 15 minutes.") : "Waiting for the health checks."}</li> : null}
        {model.more > 0 && !filter ? <li className="sy-empty">and {plainCount(model.more)} more on the Health screen</li> : null}
      </ul>
    </section>
  );
}
