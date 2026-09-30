/**
 * Needs you (#1087): the real problems whose fix is about downloads, imports
 * and titles, each with its button. Every row is one the Now badge counts, so
 * the number and the list are the same thing.
 *
 * A problem a person has to settle shows the buttons that settle it here
 * (Retry, Give up, Search again). The Setup health tab holds the connection,
 * storage and system problems with theirs.
 *
 * One quiet line under the list reaches what is worth a look but is not a
 * problem and is never counted: leftover folders and health suggestions.
 */
import { Link } from "react-router-dom";
import { StatusLed } from "../../components/ui/status-led";
import { dismissFailedJob } from "../../lib/failed-jobs";
import { canTryAgain, isVanished, type RecoveryCaseRow } from "../../lib/needs-action";
import { ActionButton, Panel, PanelNote, plural, send, useAction } from "./downloads-kit";
import { FitList } from "./parts";
import type { DashboardAttention } from "./attention";

type Action = ReturnType<typeof useAction>;

const row = "flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0";
const linkButton =
  "inline-flex h-[var(--control-height-sm)] items-center rounded-[10px] border border-hairline px-3 text-[length:var(--button-font-size-sm)] font-medium text-foreground hover:bg-secondary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring";

function recoveryLabel(item: RecoveryCaseRow): string {
  return item.mediaType === "movie" ? "Movies" : "TV";
}

function RecoveryButtons({ item, action }: { item: RecoveryCaseRow; action: Action }) {
  const collection = item.mediaType === "movie" ? "movies" : "series";
  const base = `/api/${collection}/import-recovery/${encodeURIComponent(item.id)}`;
  return (
    <span className="flex shrink-0 items-center gap-1.5">
      {canTryAgain(item) ? (
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
      <ActionButton
        label="Give up"
        name={`Give up on ${item.title}`}
        busyKey={`recovery-dismiss:${item.id}`}
        state={action}
        variant="ghost"
        onClick={() => void action.run(`recovery-dismiss:${item.id}`, async () => { await send(`${base}/dismiss`, "That case could not be dismissed."); return `${item.title} dismissed`; })}
      />
    </span>
  );
}

export function NeedsYou({ attention, action }: { attention: DashboardAttention; action: Action }) {
  const { recoveryCases, failedImportJobs, handoffs } = attention.downloads;
  const total = attention.counts.now;
  const { leftovers, suggestions } = attention.alsoWorthALook;

  const rows = attention.items
    .filter((item) => item.tab === "now")
    .flatMap((item) => {
      if (item.id === "recovery") {
        return recoveryCases.map((entry) => (
          <li key={`recovery:${entry.mediaType}:${entry.id}`} className={row}>
            <StatusLed tone="warn" />
            <div className="min-w-0 flex-1 leading-tight">
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
            <StatusLed tone="bad" />
            <div className="min-w-0 flex-1 leading-tight">
              <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">An import didn{"’"}t finish</span>
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{job.lastError ?? "It stopped without saying why."}</span>
            </div>
            <ActionButton
              label="Retry"
              name={`Retry failed import ${index + 1}`}
              busyKey={`job-retry:${job.id}`}
              state={action}
              onClick={() => void action.run(`job-retry:${job.id}`, async () => { await send("/api/jobs/retry-failed", "The failed imports could not be tried again."); return "Failed imports requeued"; })}
            />
            <ActionButton
              label="Give up"
              name={`Give up on failed import ${index + 1}`}
              busyKey={`job-dismiss:${job.id}`}
              state={action}
              variant="ghost"
              onClick={() => void action.run(`job-dismiss:${job.id}`, async () => { await dismissFailedJob(job.id); return "Import dismissed"; })}
            />
          </li>
        ));
      }
      if (item.id === "handoffs") {
        return handoffs.map((handoff) => (
          <li key={`handoff:${handoff.id}`} className={row}>
            <StatusLed tone="bad" />
            <div className="min-w-0 flex-1 leading-tight">
              <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{handoff.releaseName}</span>
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{handoff.failureMessage ?? `${handoff.processorName ?? "The processor"} could not finish it.`}</span>
            </div>
            <ActionButton
              label="Retry"
              name={`Retry ${handoff.releaseName}`}
              busyKey={`handoff-retry:${handoff.id}`}
              state={action}
              onClick={() => void action.run(`handoff-retry:${handoff.id}`, async () => (await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/retry`, "That could not be tried again."))?.message ?? "Sent to the processor again")}
            />
            <ActionButton
              label="Give up"
              name={`Give up on ${handoff.releaseName}`}
              busyKey={`handoff-dismiss:${handoff.id}`}
              state={action}
              variant="ghost"
              onClick={() => void action.run(`handoff-dismiss:${handoff.id}`, async () => { await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/dismiss`, "That could not be dismissed."); return "Dismissed"; })}
            />
          </li>
        ));
      }
      // A stalled download or one with a warning: the download is in Downloads now, the button is in Transfers.
      return [
        <li key={item.id} className={row}>
          <StatusLed tone={item.tone === "bad" ? "bad" : "warn"} />
          <div className="min-w-0 flex-1 leading-tight">
            <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.title}</span>
            <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{item.text}</span>
          </div>
          <Link to={item.to} aria-label={`${item.action}: ${item.title}`} className={linkButton}>{item.action}</Link>
        </li>
      ];
    });

  const quiet = [
    leftovers > 0 ? (
      <Link key="leftovers" to="/queue" className="text-primary hover:underline">{plural(leftovers, "leftover folder")}</Link>
    ) : null,
    suggestions > 0 ? (
      <Link key="suggestions" to="/system" className="text-primary hover:underline">{plural(suggestions, "suggestion")}</Link>
    ) : null
  ].filter(Boolean);

  return (
    <Panel
      title="Needs you"
      count={total > 0 ? String(total) : undefined}
      to="/queue"
      linkLabel="Transfers"
      className="min-h-0"
      footer={
        quiet.length > 0 ? (
          <span className="flex flex-wrap items-center gap-x-1.5">
            <span>Also worth a look:</span>
            {quiet.map((node, index) => (
              <span key={index} className="inline-flex items-center gap-1.5">{index > 0 ? <span aria-hidden>·</span> : null}{node}</span>
            ))}
          </span>
        ) : undefined
      }
    >
      {rows.length === 0 ? (
        <PanelNote>Nothing needs you. Anything that does will be listed here with a button that fixes it.</PanelNote>
      ) : (
        <FitList moreTo="/queue" moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more on Transfers`} rows={rows} />
      )}
    </Panel>
  );
}
