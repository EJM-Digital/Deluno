/**
 * The buttons on an alert, defined once (#1098).
 *
 * The bell and System's Health show the same problem with the same buttons because both ask this file. A
 * problem has at most two buttons. Each label is short, says exactly what happens and fits one width; each has
 * a hover note saying what it does and what it will not touch. Where a stronger action exists (remove a
 * download and find another, search again) it is offered by name, never hidden behind a vaguer word.
 */
import type { DownloadQueueItem, JobQueueItem, NoReleaseRow, ProcessorHandoffItem } from "./api";
import { activityLinkForStatus } from "./activity-history";
import { SYSTEM_BACKGROUND_JOBS_LINK } from "./background-jobs";
import { connectionLink, dismissPath } from "./connection-updates";
import { canTryAgain, isProcessorFault, isVanished, plainFailure, type RecoveryCaseRow } from "./needs-action";

/** The longest label that fits the one button width the bell and Health share ("Open Transfers"). */
export const ALERT_LABEL_MAX = 14;

export interface AlertRequest {
  method: "POST" | "PUT" | "PATCH" | "DELETE";
  path: string;
  body?: unknown;
  /** What to say when it worked. */
  done: string;
  /** What to say when it did not, when the server gives no words of its own. */
  failure: string;
}

export interface AlertAction {
  /** Unique within a list; the button's busy key. */
  id: string;
  label: string;
  /** The hover note: what the button does, and what it will not touch. */
  hint: string;
  /** A quieter button (Dismiss, Snooze): same box, softer text. */
  quiet?: boolean;
  /** Runs a request. */
  request?: AlertRequest;
  /** Asks first, in these words, where the button is pressed in Activity's drawer. */
  confirm?: { title: string; description: string; confirmLabel: string };
  /** Or goes to a screen. */
  to?: string;
}

/** One or two buttons, never more. */
export type AlertActions = [AlertAction] | [AlertAction, AlertAction];

const NOTHING_DELETED = "Stop showing this. Nothing is deleted.";

/** "Dismiss": stop showing it; nothing is deleted. */
function dismiss(id: string, request: AlertRequest, hint = NOTHING_DELETED): AlertAction {
  return { id, label: "Dismiss", hint, quiet: true, request };
}

function snooze(problemId: string, hours: number): AlertAction {
  const label = hours === 24 ? "Snooze 1 day" : "Snooze 1 week";
  return {
    id: `snooze:${hours}:${problemId}`,
    label,
    hint: `Hides this for ${hours === 24 ? "a day" : "a week"}, then checks again. The problem itself is not fixed or changed.`,
    quiet: true,
    request: {
      method: "POST",
      path: `/api/system/health/${encodeURIComponent(problemId)}/snooze`,
      body: { hours },
      done: `Snoozed for ${hours === 24 ? "a day" : "a week"}`,
      failure: "That could not be snoozed."
    }
  };
}

function open(id: string, label: string, to: string, hint: string): AlertAction {
  return { id, label, hint, to };
}

/* ------------------------------------------------------------ your media */

/** What a stuck download's row names as its reason. */
export function stuckDownloadReason(item: DownloadQueueItem): string {
  const finding = item.healthFindings?.[0];
  if (item.errorMessage) return plainFailure(item.errorMessage, `Talking to ${item.clientName} about it`);
  if (finding?.summary) return finding.summary;
  if (item.status === "stalled") return `Not moving in ${item.clientName}.`;
  return `${item.clientName} reported a problem with it.`;
}

/**
 * A stuck download: the stronger fix is offered by name ("Find another" removes it, deletes its files,
 * blocklists that release and searches again); the quiet one is Dismiss, which silences the client's finding
 * for a week. A download with no finding to silence is looked at in Transfers instead.
 */
export function stuckDownloadActions(item: DownloadQueueItem): AlertActions {
  const key = `${item.clientId}:${item.id}`;
  const find: AlertAction = {
    id: `find-another:${key}`,
    label: "Find another",
    hint: `Removes it from ${item.clientName}, deletes its files, blocklists this release everywhere it turns up and searches for another. Other downloads and your library are not touched.`,
    request: {
      method: "POST",
      path: `/api/download-clients/${encodeURIComponent(item.clientId)}/queue/${encodeURIComponent(item.id)}/remove`,
      body: { reason: "failed" },
      done: "Removed. Deluno is searching for another.",
      failure: "That download could not be replaced."
    }
  };
  const finding = item.healthFindings?.find((candidate) => candidate.kind);
  if (!finding) {
    return [find, open(`open-transfers:${key}`, "Open Transfers", "/queue", "Opens Transfers so you can look at this download. Nothing is changed.")];
  }
  return [
    find,
    dismiss(
      `dismiss-finding:${key}`,
      {
        method: "POST",
        path: `/api/download-clients/${encodeURIComponent(item.clientId)}/queue/${encodeURIComponent(item.id)}/health/${encodeURIComponent(finding.kind)}/ignore`,
        body: { durationDays: 7 },
        done: "Dismissed for a week, or until it happens again",
        failure: "That could not be dismissed."
      },
      "Stop showing this for a week, or until it happens again. The download keeps going and nothing is deleted."
    )
  ];
}

/** The buttons of one import case, defined once; the bell and Activity's drawer both draw from these. */
function recoveryButtons(item: RecoveryCaseRow) {
  const collection = item.mediaType === "movie" ? "movies" : "series";
  const base = `/api/${collection}/import-recovery/${encodeURIComponent(item.id)}`;
  const dismissAction = dismiss(`recovery-dismiss:${item.id}`, { method: "POST", path: `${base}/dismiss`, done: `${item.title} dismissed`, failure: "That case could not be dismissed." });
  const retry: AlertAction = isVanished(item)
    ? {
        id: `recovery-search:${item.id}`,
        label: "Search now",
        hint: "The download client no longer lists it. Searches again for another copy of this title. Your library is not touched.",
        request: { method: "POST", path: `${base}/retry`, done: `Searching again for ${item.title}`, failure: "That could not be searched again." }
      }
    : {
        id: `recovery-retry:${item.id}`,
        label: "Retry",
        hint: "Tries to finish this import again from the same download. Nothing is deleted.",
        request: { method: "POST", path: `${base}/retry`, done: `${item.title} is being tried again.`, failure: "That could not be tried again." }
      };
  const stopWanting: AlertAction = {
    id: `recovery-stop-wanting:${item.id}`,
    label: "Stop wanting",
    hint: `Stops Deluno looking for ${item.title}. Your library and its files are not touched.`,
    request: { method: "POST", path: `${base}/stop-wanting`, done: `Deluno stopped wanting ${item.title}.`, failure: "That could not be stopped." },
    confirm: { title: `Stop wanting ${item.title}?`, description: "Deluno stops looking for it. Your library and its files are not touched.", confirmLabel: "Stop wanting" }
  };
  const importOriginal: AlertAction = {
    id: `recovery-import-original:${item.id}`,
    label: "Import original",
    hint: "Brings in the original download, without what the processor did to it.",
    request: { method: "POST", path: `${base}/import-original`, done: `${item.title} will be imported unprocessed.`, failure: "The original could not be imported." }
  };
  const searchAgain: AlertAction = {
    id: `recovery-search-again:${item.id}`,
    label: "Search again",
    hint: "Looks for another release of this title. This release is not blocklisted.",
    request: { method: "POST", path: `${base}/search-again`, done: `Searching for another release for ${item.title}.`, failure: "Deluno could not search for another release." }
  };
  return { dismissAction, retry, stopWanting, importOriginal, searchAgain };
}

/** An import that needs a look: retry it (or search again when the download has vanished), or dismiss it. */
export function recoveryActions(item: RecoveryCaseRow): AlertActions {
  const { dismissAction, retry } = recoveryButtons(item);
  if (!canTryAgain(item)) {
    return [open(`recovery-review:${item.id}`, "Review", "/queue", "Opens Transfers where this import can be settled by hand. Nothing is changed."), dismissAction];
  }
  return [retry, dismissAction];
}

/**
 * Every decision an import case offers where it is opened (Activity's drawer, #1114), by its failure kind.
 * Retry (Search now when the download has vanished) when Deluno can act on it; a vanished download also offers
 * Stop wanting; a processor fault also offers Import original and Search again; every case can be dismissed.
 * Stop wanting and Dismiss ask first.
 */
export function recoveryDecisions(item: RecoveryCaseRow): AlertAction[] {
  const { dismissAction, retry, stopWanting, importOriginal, searchAgain } = recoveryButtons(item);
  const decisions: AlertAction[] = [];
  if (canTryAgain(item)) decisions.push(retry);
  if (isVanished(item)) decisions.push(stopWanting);
  if (isProcessorFault(item)) decisions.push(importOriginal, searchAgain);
  // Whatever else it offers, a case always says where it can be put right by hand (#1151).
  decisions.push(open(`recovery-fix:${item.id}`, "Fix it in Transfers", "/queue", "Opens Transfers, where this download can be looked at, sent again or replaced. Nothing is changed."));
  decisions.push({ ...dismissAction, confirm: { title: `Dismiss ${item.title}?`, description: "Deluno stops showing this. Nothing is deleted.", confirmLabel: "Dismiss" } });
  return decisions;
}

/** A failed import job: try it again, or dismiss it. */
export function failedImportActions(job: Pick<JobQueueItem, "id">): AlertActions {
  return [
    {
      id: `job-retry:${job.id}`,
      label: "Try again",
      hint: "Tries this import again. Files already in your library are not touched.",
      request: { method: "POST", path: `/api/jobs/${encodeURIComponent(job.id)}/retry`, done: "Import tried again", failure: "That import could not be tried again." }
    },
    dismiss(`job-dismiss:${job.id}`, { method: "POST", path: `/api/jobs/${encodeURIComponent(job.id)}/dismiss`, done: "Import dismissed", failure: "That import could not be dismissed." })
  ];
}

/**
 * Whether sending the finished download to the processor again would do anything (#1151). Once the hand-off has its
 * processed file, an import job or an imported outcome, it is closed: the server answers 409 and the button is not offered.
 */
export function handoffCanBeSentAgain(handoff: Partial<Pick<ProcessorHandoffItem, "outputPath" | "importJobId" | "outcome" | "status">>): boolean {
  // A download that was gone has nothing left to send (#1186).
  return handoff.status?.toLowerCase() !== "source-gone" && !handoff.outputPath?.trim() && !handoff.importJobId?.trim() && handoff.outcome?.outcome !== "imported";
}

/** A refused hand-off to the processor: send it again (unless it already has its processed file), or dismiss it. */
export function handoffActions(handoff: Pick<ProcessorHandoffItem, "id" | "releaseName"> & Partial<Pick<ProcessorHandoffItem, "outputPath" | "importJobId" | "outcome" | "status">>): AlertActions {
  const base = `/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}`;
  const dismissal = dismiss(`handoff-dismiss:${handoff.id}`, { method: "POST", path: `${base}/dismiss`, done: "Dismissed", failure: "That could not be dismissed." });
  // The download was gone before the processor reached it (#1186): a new copy or nothing, never a retry.
  if (handoff.status?.toLowerCase() === "source-gone") {
    return [
      {
        id: `handoff-search-another:${handoff.id}`,
        label: "Find another",
        hint: "Searches for another copy of this title now, as your own search would. Nothing is blocklisted.",
        request: { method: "POST", path: `${base}/search-again`, done: `Searching for another copy of ${handoff.releaseName}.`, failure: "Deluno could not search for another copy." }
      },
      dismissal
    ];
  }
  if (!handoffCanBeSentAgain(handoff)) return [dismissal];
  return [
    {
      id: `handoff-retry:${handoff.id}`,
      label: "Retry",
      hint: "Sends the finished download to the processor again. The download and your library are not touched.",
      request: { method: "POST", path: `${base}/retry`, done: "Sent to the processor again", failure: "That could not be tried again." }
    },
    dismissal
  ];
}

/** A wanted title no search could satisfy: search again now, or dismiss it. */
export function noReleaseActions(row: NoReleaseRow): AlertActions {
  const collection = row.mediaType === "movie" ? "movies" : "series";
  return [
    {
      id: `search-now:${row.mediaType}:${row.id}`,
      label: "Search now",
      hint: `Searches every indexer for ${row.title} again right now. Nothing is downloaded unless a release you would accept turns up.`,
      request: {
        method: "POST",
        path: `/api/${collection}/${encodeURIComponent(row.id)}/search${row.libraryId ? `?libraryId=${encodeURIComponent(row.libraryId)}` : ""}`,
        done: `Searching for ${row.title}`,
        failure: "That search could not be started."
      }
    },
    dismiss(`dismiss-no-release:${row.mediaType}:${row.id}`, { method: "POST", path: `/api/attention/no-release/${encodeURIComponent(row.mediaType)}/${encodeURIComponent(row.id)}/dismiss`, done: `${row.title} dismissed`, failure: "That could not be dismissed." },
      `${NOTHING_DELETED} The title stays wanted and keeps being searched for.`)
  ];
}

/* ------------------------------------------------------------- Deluno */

export type ConnectionKind = "indexer" | "downloadClient" | "processor" | "mediaServer" | "requests";

/** The Health id prefix of a connection ("indexer:<id>") as the kind the test endpoint takes. */
const KIND_OF_PREFIX: Record<string, ConnectionKind> = {
  indexer: "indexer",
  "download-client": "downloadClient",
  processor: "processor",
  "media-server": "mediaServer",
  seerr: "requests"
};

export function testAgain(kind: ConnectionKind, id: string, name: string): AlertAction {
  return {
    id: `test-again:${kind}:${id}`,
    label: "Test again",
    hint: `Asks ${name} for a real answer now and shows the result. Nothing is changed.`,
    request: { method: "POST", path: `/api/connections/${kind}/${encodeURIComponent(id)}/test`, done: `Tested again: ${name}`, failure: `Could not test ${name}.` }
  };
}

/** An indexer or download client that is not answering: test it again, or switch it off until it is fixed. */
export function connectionDownActions(kind: "indexer" | "downloadClient", id: string, name: string): AlertActions {
  const path = kind === "indexer" ? `/api/indexers/${encodeURIComponent(id)}` : `/api/download-clients/${encodeURIComponent(id)}`;
  return [
    testAgain(kind, id, name),
    {
      id: `switch-off:${kind}:${id}`,
      label: "Switch off",
      hint: `Stops Deluno using ${name} until you switch it back on in Connections. Nothing is deleted and its settings are kept.`,
      quiet: true,
      request: { method: "PUT", path, body: { isEnabled: false }, done: `${name} switched off`, failure: `${name} could not be switched off.` }
    }
  ];
}

/** The Health id prefix of a start-up step that did not finish; it can be tried again. */
export const STARTUP_PROBLEM_PREFIX = "system:background-problem:startup:";

export interface HealthProblemLike {
  id: string;
  category?: string;
  /** What the connection is called, for the hover notes; "this connection" when not given. */
  name?: string;
  /** The screen that clears it, as a web path. */
  link?: string;
}

/** What each health category's own screen is called on the "open it" button. */
function screenButton(link: string): { label: string; hint: string } {
  if (link.startsWith("/settings")) return { label: "Open Settings", hint: "Opens the settings that clear this. Nothing is changed until you save." };
  if (link.startsWith("/indexers")) return { label: "Open Services", hint: "Opens the screen where this connection is set up. Nothing is changed." };
  if (link.startsWith("/queue")) return { label: "Open Transfers", hint: "Opens Transfers. Nothing is changed." };
  return { label: "Open System", hint: "Opens System's Health, where this problem is explained. Nothing is changed." };
}

/**
 * A Health problem, by what it is. The Machine checks have their own buttons; a connection is tested again;
 * the rest go to the screen that clears them. Every one has Snooze, except where a switch-off is the better
 * quiet choice.
 */
export function healthProblemActions(problem: HealthProblemLike): AlertActions {
  const { id } = problem;
  const link = problem.link || "/system";

  if (id.startsWith(STARTUP_PROBLEM_PREFIX)) {
    const key = id.slice("system:background-problem:".length);
    return [
      {
        id: `try-again:${id}`,
        label: "Try again",
        hint: "Runs the step that did not finish again now. Nothing else is changed.",
        request: { method: "POST", path: `/api/system/health/background-problems/${encodeURIComponent(key)}/retry`, done: "Tried again", failure: "Deluno could not try that again. Check its logs." }
      },
      snooze(id, 24)
    ];
  }
  if (id === "machine:sleep") {
    return [
      {
        id: `keep-awake:${id}`,
        label: "Keep awake",
        hint: "Deluno asks Windows not to sleep while Deluno is running. Your Windows power settings are not changed, and it stops when you switch it off in Settings.",
        request: { method: "PATCH", path: "/api/settings", body: { keepComputerAwake: true }, done: "Deluno is keeping this computer awake", failure: "Deluno could not keep the computer awake." }
      },
      snooze(id, 24)
    ];
  }
  if (id.startsWith("machine:drive:")) {
    return [
      {
        id: `back-up-now:${id}`,
        label: "Back up now",
        hint: "Saves a backup of Deluno's own data now. It does not copy your media files or touch the drive.",
        request: { method: "POST", path: "/api/backups", done: "Backup started", failure: "The backup could not be started." }
      },
      snooze(id, 24)
    ];
  }
  if (id === "machine:reboot" || id === "machine:temperature") {
    return [snooze(id, 24), snooze(id, 24 * 7)];
  }
  if (id === "system:disk-space" || id.endsWith(":free-space")) {
    return [open(`clean-up:${id}`, "Clean up", "/queue", "Opens Transfers where leftover download folders can be cleared. Nothing is deleted until you confirm."), snooze(id, 24)];
  }

  const colon = id.indexOf(":");
  const kind = colon > 0 ? KIND_OF_PREFIX[id.slice(0, colon)] : undefined;
  if (kind && colon > 0 && id.slice(colon + 1).length > 0 && !id.slice(colon + 1).includes(":")) {
    const connectionId = id.slice(colon + 1);
    if (kind === "indexer" || kind === "downloadClient") return connectionDownActions(kind, connectionId, problem.name ?? "this connection");
    return [testAgain(kind, connectionId, problem.name ?? "this connection"), snooze(id, 24)];
  }

  const screen = screenButton(link);
  return [open(`open:${id}`, screen.label, link, screen.hint), snooze(id, 24)];
}

/**
 * Jobs that gave up: put them back in the queue, or look at which and why. Background jobs (not about a title) are
 * looked at on System; a job about a title in Activity (#1114), so the Review button opens the screen that clears them.
 */
export function jobsGaveUpActions(kind: "background" | "title" = "background"): AlertActions {
  return [
    {
      id: "jobs-retry",
      label: "Retry",
      hint: "Puts the jobs that gave up back in the queue to run again. Nothing already done is undone.",
      request: { method: "POST", path: "/api/jobs/retry-failed", done: "Failed jobs requeued", failure: "The failed jobs could not be requeued." }
    },
    kind === "title"
      ? open("jobs-review", "Review", activityLinkForStatus("failed"), "Opens Activity's failed jobs to see which failed and why. Nothing is changed.")
      : open("jobs-review", "Review", SYSTEM_BACKGROUND_JOBS_LINK, "Opens System's background jobs to see which failed and why. Nothing is changed.")
  ];
}

/** A row whose one button goes to the screen that deals with it ("Open Updates", "Collections"). */
export function screenLinkActions(label: string, to: string, id = `open:${to}`): AlertActions {
  return [open(id, label, to, `Opens ${label.replace(/^Open /, "")}, where this is dealt with. Nothing is changed.`)];
}

/** An update is ready: look at it. (A pending update is not a problem to snooze.) */
export function updateReadyActions(): AlertActions {
  return [open("open-updates", "Open Updates", "/system/updates", "Opens Updates, where you choose when to install. Nothing is installed until you say so.")];
}

/**
 * A newer version of a connected app (#1118): open that app's connection (release notes, and Update now where
 * Deluno can do it safely), or dismiss this version. Dismissing nothing is installed or changed.
 */
export function connectionUpdateActions(kind: string, id: string): AlertActions {
  return [
    open(`open-update:${kind}:${id}`, "Open", connectionLink(kind, id), "Opens this connection, where the release notes are and, when Deluno can do it safely, Update now. Nothing is installed."),
    dismiss(`dismiss-update:${kind}:${id}`, { method: "POST", path: dismissPath(kind, id), done: "Dismissed this version", failure: "That could not be dismissed." }, "Stops showing this version. It comes back only for a newer one. Nothing is installed.")
  ];
}
