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
import { StatusLed } from "../../components/ui/status-led";
import { dismissFailedJob } from "../../lib/failed-jobs";
import { canTryAgain, isVanished, type RecoveryCaseRow } from "../../lib/needs-action";
import { ActionButton, plural, send, UNIFORM_BUTTON, useAction } from "./downloads-kit";
import type { DashboardAttention } from "./attention";

export type Action = ReturnType<typeof useAction>;

const linkButton = `inline-flex ${UNIFORM_BUTTON} items-center whitespace-nowrap rounded-[10px] border border-hairline text-[length:var(--button-font-size-sm)] font-medium text-foreground hover:bg-secondary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring`;

/**
 * One Needs-you row, the same on the Dashboard and in the bell: status dot, bold
 * one-line title, muted one-line reason (both whole in the tooltip), then the two
 * fixed action columns. One padding and a hairline divider on every row; on a phone
 * the buttons drop under the text.
 */
export function NeedsYouRow({ tone, title, text, actions }: { tone: "bad" | "warn"; title: string; text: string; actions: ReactElement }) {
  return (
    <li className="grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-2.5 gap-y-1.5 border-b border-hairline px-[var(--card-pad-x)] py-2.5 last:border-b-0 sm:grid-cols-[auto_minmax(0,1fr)_auto]">
      <StatusLed tone={tone} pulse />
      <div className="min-w-0 leading-tight">
        <span title={title} className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{title}</span>
        <span title={text} className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{text}</span>
      </div>
      <div className="col-start-2 sm:col-start-auto">{actions}</div>
    </li>
  );
}

/**
 * The same action area on every row: two fixed columns of one width, so every
 * row's first button lines up with every other row's first button and the second
 * with the second, whatever their words. A row with one action puts it in the
 * first column and leaves the second empty, so every row ends at the same edge.
 */
export function RowActions({ primary, secondary }: { primary: ReactElement; secondary?: ReactElement }) {
  return (
    <span className="grid grid-cols-[7.5rem_7.5rem] gap-1.5" data-row-actions>
      <span className="flex">{primary}</span>
      <span className="flex">{secondary ?? null}</span>
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
          uniform
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
        uniform
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
export function needsYouRows(attention: DashboardAttention, action: Action): ReactElement[] {
  const { recoveryCases, failedImportJobs, handoffs } = attention.downloads;
  return attention.items
    .filter((item) => item.tab === "live")
    .flatMap((item) => {
      if (item.id === "recovery") {
        return recoveryCases.map((entry) => (
          <NeedsYouRow key={`recovery:${entry.mediaType}:${entry.id}`} tone="warn" title={entry.title} text={`${recoveryLabel(entry)}: ${entry.summary}`} actions={<RecoveryButtons item={entry} action={action} />} />
        ));
      }
      if (item.id === "failed-imports") {
        return failedImportJobs.map((job, index) => (
          <NeedsYouRow key={`import:${job.id}`} tone="bad" title="An import didn’t finish" text={job.lastError ?? "It stopped without saying why."} actions={<RowActions
              primary={<ActionButton
          uniform
              label="Retry"
              name={`Retry failed import ${index + 1}`}
              busyKey={`job-retry:${job.id}`}
              state={action}
              onClick={() => void action.run(`job-retry:${job.id}`, async () => { await send("/api/jobs/retry-failed", "The failed imports could not be tried again."); return "Failed imports requeued"; })}
            />}
              secondary={<ActionButton
        uniform
              label="Give up"
              name={`Give up on failed import ${index + 1}`}
              busyKey={`job-dismiss:${job.id}`}
              state={action}
              variant="ghost"
              onClick={() => void action.run(`job-dismiss:${job.id}`, async () => { await dismissFailedJob(job.id); return "Import dismissed"; })}
            />}
            />} />
        ));
      }
      if (item.id === "handoffs") {
        return handoffs.map((handoff) => (
          <NeedsYouRow key={`handoff:${handoff.id}`} tone="bad" title={handoff.releaseName} text={handoff.failureMessage ?? `${handoff.processorName ?? "The processor"} could not finish it.`} actions={<RowActions
              primary={<ActionButton
          uniform
              label="Retry"
              name={`Retry ${handoff.releaseName}`}
              busyKey={`handoff-retry:${handoff.id}`}
              state={action}
              onClick={() => void action.run(`handoff-retry:${handoff.id}`, async () => (await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/retry`, "That could not be tried again."))?.message ?? "Sent to the processor again")}
            />}
              secondary={<ActionButton
        uniform
              label="Give up"
              name={`Give up on ${handoff.releaseName}`}
              busyKey={`handoff-dismiss:${handoff.id}`}
              state={action}
              variant="ghost"
              onClick={() => void action.run(`handoff-dismiss:${handoff.id}`, async () => { await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/dismiss`, "That could not be dismissed."); return "Dismissed"; })}
            />}
            />} />
        ));
      }
      // A stalled download or one with a warning: the download is in Downloads now, the button is in Transfers.
      return [
        <NeedsYouRow key={item.id} tone={item.tone === "bad" ? "bad" : "warn"} title={item.title} text={item.text} actions={<RowActions primary={<Link to={item.to} aria-label={`${item.action}: ${item.title}`} className={linkButton}>{item.action}</Link>} />} />
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
      <NeedsYouRow key={item.id} tone={item.tone === "bad" ? "bad" : "warn"} title={item.title} text={item.text} actions={<RowActions primary={<Link to={item.to} aria-label={`${item.action}: ${item.title}`} className={linkButton}>{item.action}</Link>} />} />
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
