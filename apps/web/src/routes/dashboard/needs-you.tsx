/**
 * Needs you (#1087): the real problems whose fix is about downloads, imports
 * and titles, each with its button. Every row is one the Live badge counts, so
 * the number and the list are the same thing.
 *
 * A problem a person has to settle shows the buttons that settle it here
 * (Retry, Give up, Search again). The System tab holds the connection,
 * storage and system problems with theirs.
 *
 * One quiet line under the list reaches what is worth a look but is not a
 * problem and is never counted: leftover folders and health suggestions.
 */
import type { ReactElement } from "react";
import { Link } from "react-router-dom";
import { Button } from "../../components/ui/button";
import { CountUp } from "../../components/ui/count-up";
import { StatusLed } from "../../components/ui/status-led";
import { dismissFailedJob } from "../../lib/failed-jobs";
import { canTryAgain, isVanished, type RecoveryCaseRow } from "../../lib/needs-action";
import { ActionButton, Panel, PanelNote, plural, send, useAction } from "./downloads-kit";
import { FitList } from "./parts";
import type { DashboardAttention } from "./attention";

export type Action = ReturnType<typeof useAction>;

/** What the Live board already shows, each with its own buttons: the downloads (as `clientId:id`) and the failed processor hand-offs. */
export interface OnBoard {
  queueKeys: ReadonlySet<string>;
  handoffIds: ReadonlySet<string>;
}

/** Draws the eye to a lane of the board for a moment, so "on the board" is shown and not only said. */
export function flashLane(stage: string): void {
  const lane = document.getElementById(`live-lane-${stage}`);
  if (!lane) return;
  lane.classList.add("live-lane-flash");
  window.setTimeout(() => lane.classList.remove("live-lane-flash"), 1800);
}

const row = "flex flex-wrap items-center gap-x-2 gap-y-1.5 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0";
const linkButton =
  "inline-flex h-[var(--control-height-sm)] items-center justify-center truncate rounded-[10px] border border-hairline px-3 text-[length:var(--button-font-size-sm)] font-medium text-foreground hover:bg-secondary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring";

/**
 * The same action area on every row: the main button in one column, "Give up"
 * in a second column of fixed width, so buttons line up down the list whatever
 * their words (the owner, 1 Oct: "make sure all the buttons align and are neat and
 * tidy depending on the scenario"). A row with one button spans both columns,
 * so every row ends at the same edge.
 */
export function RowActions({ primary, secondary }: { primary: ReactElement; secondary?: ReactElement }) {
  return (
    <span className="ml-4 grid w-[calc(100%-1rem)] shrink-0 grid-cols-[minmax(0,1fr)_4.75rem] gap-1.5 sm:ml-0 sm:w-[13.5rem] [&>span>*]:w-full [&>span>*]:min-w-0 [&>span>*]:justify-center">
      <span className={secondary ? "flex min-w-0" : "col-span-2 flex min-w-0"}>{primary}</span>
      {secondary ? <span className="flex min-w-0">{secondary}</span> : null}
    </span>
  );
}

function recoveryLabel(item: RecoveryCaseRow): string {
  return item.mediaType === "movie" ? "Movies" : "TV";
}

function RecoveryButtons({ item, action }: { item: RecoveryCaseRow; action: Action }) {
  const collection = item.mediaType === "movie" ? "movies" : "series";
  const base = `/api/${collection}/import-recovery/${encodeURIComponent(item.id)}`;
  return (
    <RowActions
      primary={canTryAgain(item) ? (
        <ActionButton
          label={isVanished(item) ? "Search again" : "Retry"}
          name={`${isVanished(item) ? "Search again for" : "Retry"} ${item.title}`}
          busyKey={`recovery-retry:${item.id}`}
          state={action}
          onClick={() => void action.run(`recovery-retry:${item.id}`, async () => (await send(`${base}/retry`, "That could not be tried again."))?.message ?? `${item.title} is being tried again.`)}
        />
      ) : (
        <Link to="/queue" aria-label={`Review ${item.title}`} className={linkButton}>Review</Link>
      )}
      secondary={<ActionButton
        label="Give up"
        name={`Give up on ${item.title}`}
        busyKey={`recovery-dismiss:${item.id}`}
        state={action}
        variant="ghost"
        onClick={() => void action.run(`recovery-dismiss:${item.id}`, async () => { await send(`${base}/dismiss`, "That case could not be dismissed."); return `${item.title} dismissed`; })}
      />}
    />
  );
}

/**
 * The Needs you rows for the problems about downloads, imports and titles, each
 * with its button. The Dashboard's Live tab and the bell's panel both draw these
 * rows, so they are one list.
 */
export function needsYouRows(attention: DashboardAttention, action: Action, onBoard?: OnBoard): ReactElement[] {
  const { recoveryCases, failedImportJobs, handoffs, queueAttention } = attention.downloads;
  // What the journey board already shows, with its own buttons, is not listed a second time: one line says so and points at it.
  const coveredDownloads = onBoard ? queueAttention.filter((entry) => onBoard.queueKeys.has(`${entry.clientId}:${entry.id}`)).length : 0;
  const collapseDownloads = queueAttention.length > 0 && coveredDownloads === queueAttention.length;
  let lineShown = false;
  // "Stuck" when every one has stopped; "need a look" when some only have a warning from their client.
  const allStalled = queueAttention.every((entry) => entry.status === "stalled");
  const lineTitle = allStalled ? `${queueAttention.length.toLocaleString("en-AU")} stuck ${queueAttention.length === 1 ? "download" : "downloads"}` : plural(queueAttention.length, "download needs a look", "downloads need a look");
  return attention.items
    .filter((item) => item.tab === "live")
    .flatMap((item) => {
      if (collapseDownloads && (item.id === "stalled" || item.id === "queue-warnings")) {
        if (lineShown) return [];
        lineShown = true;
        return [
          <li key="downloads-on-board" className={row}>
            <StatusLed tone="warn" pulse />
            <div className="min-w-0 flex-1 basis-[calc(100%-1.5rem)] leading-tight sm:basis-0">
              <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{lineTitle}</span>
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">On the board, each with its button.</span>
            </div>
            <RowActions primary={<Button type="button" variant="outline" size="sm" aria-label="Show the stuck downloads on the board" onClick={() => flashLane("stuck")}>Show them</Button>} />
          </li>
        ];
      }
      if (item.id === "recovery") {
        return recoveryCases.map((entry) => (
          <li key={`recovery:${entry.mediaType}:${entry.id}`} className={row}>
            <StatusLed tone="warn" pulse />
            <div className="min-w-0 flex-1 basis-[calc(100%-1.5rem)] leading-tight sm:basis-0">
              <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{entry.title}</span>
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{recoveryLabel(entry)}: {entry.summary}</span>
            </div>
            <RecoveryButtons item={entry} action={action} />
          </li>
        ));
      }
      if (item.id === "failed-imports") {
        return failedImportJobs.map((job, index) => (
          <li key={`import:${job.id}`} className={row}>
            <StatusLed tone="bad" pulse />
            <div className="min-w-0 flex-1 basis-[calc(100%-1.5rem)] leading-tight sm:basis-0">
              <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">An import didn{"’"}t finish</span>
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{job.lastError ?? "It stopped without saying why."}</span>
            </div>
            <RowActions
              primary={<ActionButton
              label="Retry"
              name={`Retry failed import ${index + 1}`}
              busyKey={`job-retry:${job.id}`}
              state={action}
              onClick={() => void action.run(`job-retry:${job.id}`, async () => { await send("/api/jobs/retry-failed", "The failed imports could not be tried again."); return "Failed imports requeued"; })}
            />}
              secondary={<ActionButton
              label="Give up"
              name={`Give up on failed import ${index + 1}`}
              busyKey={`job-dismiss:${job.id}`}
              state={action}
              variant="ghost"
              onClick={() => void action.run(`job-dismiss:${job.id}`, async () => { await dismissFailedJob(job.id); return "Import dismissed"; })}
            />}
            />
          </li>
        ));
      }
      if (item.id === "handoffs") {
        return handoffs.filter((handoff) => !onBoard?.handoffIds.has(handoff.id)).map((handoff) => (
          <li key={`handoff:${handoff.id}`} className={row}>
            <StatusLed tone="bad" pulse />
            <div className="min-w-0 flex-1 basis-[calc(100%-1.5rem)] leading-tight sm:basis-0">
              <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{handoff.releaseName}</span>
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{handoff.failureMessage ?? `${handoff.processorName ?? "The processor"} could not finish it.`}</span>
            </div>
            <RowActions
              primary={<ActionButton
              label="Retry"
              name={`Retry ${handoff.releaseName}`}
              busyKey={`handoff-retry:${handoff.id}`}
              state={action}
              onClick={() => void action.run(`handoff-retry:${handoff.id}`, async () => (await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/retry`, "That could not be tried again."))?.message ?? "Sent to the processor again")}
            />}
              secondary={<ActionButton
              label="Give up"
              name={`Give up on ${handoff.releaseName}`}
              busyKey={`handoff-dismiss:${handoff.id}`}
              state={action}
              variant="ghost"
              onClick={() => void action.run(`handoff-dismiss:${handoff.id}`, async () => { await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/dismiss`, "That could not be dismissed."); return "Dismissed"; })}
            />}
            />
          </li>
        ));
      }
      // A stalled download or one with a warning: the download is in Downloads now, the button is in Transfers.
      return [
        <li key={item.id} className={row}>
          <StatusLed tone={item.tone === "bad" ? "bad" : "warn"} pulse />
          <div className="min-w-0 flex-1 basis-[calc(100%-1.5rem)] leading-tight sm:basis-0">
            <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.title}</span>
            <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{item.text}</span>
          </div>
          <RowActions primary={<Link to={item.to} aria-label={`${item.action}: ${item.title}`} className={linkButton}>{item.action}</Link>} />
        </li>
      ];
    });
}

/**
 * The connection, storage and system problems, one row each with a button that
 * goes to the place they are cleared (System, or the screen a health
 * problem names). Their own buttons (Test again, Resume, Snooze) sit in the
 * System panels; the bell lists them here so every problem is reachable
 * from every screen.
 */
export function setupProblemRows(attention: DashboardAttention): ReactElement[] {
  return attention.items
    .filter((item) => item.tab === "system")
    .map((item) => (
      <li key={item.id} className={row}>
        <StatusLed tone={item.tone === "bad" ? "bad" : "warn"} pulse />
        <div className="min-w-0 flex-1 basis-[calc(100%-1.5rem)] leading-tight sm:basis-0">
          <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.title}</span>
          <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{item.text}</span>
        </div>
        <RowActions primary={<Link to={item.to} aria-label={`${item.action}: ${item.title}`} className={linkButton}>{item.action}</Link>} />
      </li>
    ));
}

/**
 * The one quiet line for what is worth a look but is not a problem and is never
 * counted: leftover folders and health suggestions. Nothing when there are none.
 */
export function AlsoWorthALook({ attention }: { attention: DashboardAttention }) {
  const { leftovers, suggestions } = attention.alsoWorthALook;
  const quiet = [
    leftovers > 0 ? (
      <Link key="leftovers" to="/queue" className="text-primary hover:underline">{plural(leftovers, "leftover folder")}</Link>
    ) : null,
    suggestions > 0 ? (
      <Link key="suggestions" to="/system" className="text-primary hover:underline">{plural(suggestions, "suggestion")}</Link>
    ) : null
  ].filter(Boolean);
  if (quiet.length === 0) return null;
  return (
    <span className="flex flex-wrap items-center gap-x-1.5">
      <span>Also worth a look:</span>
      {quiet.map((node, index) => (
        <span key={index} className="inline-flex items-center gap-1.5">{index > 0 ? <span aria-hidden>·</span> : null}{node}</span>
      ))}
    </span>
  );
}

export function NeedsYou({ attention, action, onBoard }: { attention: DashboardAttention; action: Action; onBoard?: OnBoard }) {
  const total = attention.counts.live;
  const rows = needsYouRows(attention, action, onBoard);
  const { leftovers, suggestions } = attention.alsoWorthALook;

  return (
    <Panel
      title="Needs you"
      count={total > 0 ? <CountUp value={total} /> : undefined}
      to="/queue"
      linkLabel="Transfers"
      className="min-h-0"
      footer={leftovers + suggestions > 0 ? <AlsoWorthALook attention={attention} /> : undefined}
    >
      {rows.length === 0 ? (
        <PanelNote>Nothing needs you. Anything that does will be listed here with a button that fixes it.</PanelNote>
      ) : (
        <FitList moreTo="/queue" moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more on Transfers`} rows={rows} />
      )}
    </Panel>
  );
}
