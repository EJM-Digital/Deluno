/**
 * The buttons on an alert, defined once (#1098).
 *
 * The bell and System's Health show the same problem with the same buttons because both ask this file. A
 * problem has at most two buttons. Each label is short, says exactly what happens and fits one width; each has
 * a hover note saying what it does and what it will not touch. Where a stronger action exists (remove a
 * download and find another, search again) it is offered by name, never hidden behind a vaguer word.
 */
import type { DownloadQueueItem, JobQueueItem, NoReleaseRow, ProcessorHandoffItem } from "./api";
import { canTryAgain, isVanished, type RecoveryCaseRow } from "./needs-action";

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
  if (item.errorMessage) return item.errorMessage;
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

/** An import that needs a look: retry it (or search again when the download has vanished), or dismiss it. */
export function recoveryActions(item: RecoveryCaseRow): AlertActions {
  const collection = item.mediaType === "movie" ? "movies" : "series";
  const base = `/api/${collection}/import-recovery/${encodeURIComponent(item.id)}`;
  const dismissAction = dismiss(`recovery-dismiss:${item.id}`, { method: "POST", path: `${base}/dismiss`, done: `${item.title} dismissed`, failure: "That case could not be dismissed." });
  if (!canTryAgain(item)) {
    return [open(`recovery-review:${item.id}`, "Review", "/queue", "Opens Transfers where this import can be settled by hand. Nothing is changed."), dismissAction];
  }
  return isVanished(item)
    ? [
        {
          id: `recovery-search:${item.id}`,
          label: "Search now",
          hint: "The download client no longer lists it. Searches again for another copy of this title. Your library is not touched.",
          request: { method: "POST", path: `${base}/retry`, done: `Searching again for ${item.title}`, failure: "That could not be searched again." }
        },
        dismissAction
      ]
    : [
        {
          id: `recovery-retry:${item.id}`,
          label: "Retry",
          hint: "Tries to finish this import again from the same download. Nothing is deleted.",
          request: { method: "POST", path: `${base}/retry`, done: `${item.title} is being tried again.`, failure: "That could not be tried again." }
        },
        dismissAction
      ];
}

/** A failed import job: try it again, or dismiss it. */
export function failedImportActions(job: Pick<JobQueueItem, "id">): AlertActions {
  return [
    {
      id: `job-retry:${job.id}`,
      label: "Retry",
      hint: "Tries the failed imports again. Files already in your library are not touched.",
      request: { method: "POST", path: "/api/jobs/retry-failed", done: "Failed imports requeued", failure: "The failed imports could not be tried again." }
    },
    dismiss(`job-dismiss:${job.id}`, { method: "POST", path: `/api/jobs/${encodeURIComponent(job.id)}/dismiss`, done: "Import dismissed", failure: "That import could not be dismissed." })
  ];
}

/** A refused hand-off to the processor: send it again, or dismiss it. */
export function handoffActions(handoff: Pick<ProcessorHandoffItem, "id" | "releaseName">): AlertActions {
  const base = `/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}`;
  return [
    {
      id: `handoff-retry:${handoff.id}`,
      label: "Retry",
      hint: "Sends the finished download to the processor again. The download and your library are not touched.",
      request: { method: "POST", path: `${base}/retry`, done: "Sent to the processor again", failure: "That could not be tried again." }
    },
    dismiss(`handoff-dismiss:${handoff.id}`, { method: "POST", path: `${base}/dismiss`, done: "Dismissed", failure: "That could not be dismissed." })
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
    request: { method: "POST", path: `/api/connections/${kind}/${encodeURIComponent(id)}/test`, done: `${name} tested`, failure: `${name} could not be tested.` }
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

export interface HealthProblemLike {
  id: string;
  category?: string;
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
    if (kind === "indexer" || kind === "downloadClient") return connectionDownActions(kind, connectionId, "this connection");
    return [testAgain(kind, connectionId, "this connection"), snooze(id, 24)];
  }

  const screen = screenButton(link);
  return [open(`open:${id}`, screen.label, link, screen.hint), snooze(id, 24)];
}

/** An update is ready: look at it. (A pending update is not a problem to snooze.) */
export function updateReadyActions(): AlertActions {
  return [open("open-updates", "Open Updates", "/system/updates", "Opens Updates, where you choose when to install. Nothing is installed until you say so.")];
}
