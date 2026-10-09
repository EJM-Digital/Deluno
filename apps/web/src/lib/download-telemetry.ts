import type { DownloadClientTelemetrySnapshot, DownloadTelemetryOverview } from "./api";

export const downloadQueueStatuses = {
  downloading: "downloading",
  queued: "queued",
  completed: "completed",
  stalled: "stalled",
  // Verifying a hash or relocating finished files: not moving bytes on the
  // wire, but not a problem either (#660).
  notTransferring: "notTransferring",
  // Stopped by a person, distinct from queued (#660/#661).
  paused: "paused",
  processing: "processing",
  processed: "processed",
  processingFailed: "processingFailed",
  waitingForProcessor: "waitingForProcessor",
  // Finished, and not yet handed to the processor: a display state the screen derives, never sent by the server.
  handingOver: "handingOver",
  // Another copy of the same release holds the hand-off (#1155): nothing is owed to this one.
  handedOverElsewhere: "handedOverElsewhere",
  // Finished a few minutes ago with no hand-off of its own and nothing explaining it (#1155).
  notHandedOver: "notHandedOver",
  importReady: "importReady",
  importQueued: "importQueued",
  imported: "imported",
  // Imported earlier, but the title no longer holds the file and nothing will import it again (#1182): history.
  importedFileLeft: "importedFileLeft",
  // The files are gone: the client still lists the download but they are not on disk, or its hand-off ended because
  // they were gone and was dismissed (#1200). Never ready to import.
  filesGone: "filesGone",
  importFailed: "importFailed"
} as const;

export function isImportReadyStatus(status: string) {
  return status === downloadQueueStatuses.importReady || status === downloadQueueStatuses.completed;
}

export function isProcessingStatus(status: string) {
  const processingStatuses: string[] = [
    downloadQueueStatuses.processing,
    downloadQueueStatuses.processed,
    downloadQueueStatuses.processingFailed,
    downloadQueueStatuses.waitingForProcessor,
    downloadQueueStatuses.handingOver,
    downloadQueueStatuses.notHandedOver,
    downloadQueueStatuses.importQueued
  ];
  return processingStatuses.includes(status);
}

export function queueStatusLabel(status: string) {
  return (
    {
      downloading: "Downloading",
      queued: "Queued",
      completed: "Import ready",
      importReady: "Import ready",
      waitingForProcessor: "Waiting for processor",
      handingOver: "Handing over",
      handedOverElsewhere: "Another copy handed over",
      notHandedOver: "Not handed over",
      processing: "Processing",
      processed: "Processed",
      processingFailed: "Processing failed",
      importQueued: "Import queued",
      imported: "Imported",
      importedFileLeft: "Imported earlier, but the file has since left the library",
      filesGone: "Files are gone",
      importFailed: "Import failed",
      stalled: "Stalled",
      notTransferring: "Not transferring",
      paused: "Paused"
    } as Record<string, string>
  )[status] ?? status;
}

/**
 * Every value `DownloadClientHistoryItem.Outcome` can carry, in plain words.
 *
 * <p>Mirrors `Deluno.Integrations.DownloadClients.DownloadClientHistoryOutcomes`
 * (the client's own reports: duplicate, needsAttention, scriptFailed,
 * scriptWarning) plus the generic values `DownloadClientTelemetryService`
 * derives from a client's queue or Deluno's own dispatch record (failed,
 * completed, imported, importReady, sent, unknown). Anything outside that set
 * falls back to the code itself, same as {@link queueStatusLabel} and
 * `reasonWords` — a code you can search for beats a word that tells you
 * nothing (#948).</p>
 */
export const CLIENT_HISTORY_OUTCOMES = [
  "duplicate",
  "needsAttention",
  "scriptFailed",
  "scriptWarning",
  "failed",
  "completed",
  "imported",
  "importReady",
  "sent",
  "unknown"
] as const;

const CLIENT_HISTORY_OUTCOME_WORDS: Record<string, string> = {
  duplicate: "Already had it",
  needsAttention: "Needs your attention",
  scriptFailed: "The client's own script failed",
  scriptWarning: "The client's own script warned",
  failed: "Failed",
  completed: "Finished downloading",
  imported: "Imported",
  importReady: "Ready to import",
  sent: "Sent to the client",
  unknown: "Unknown"
};

export function clientHistoryOutcomeLabel(outcome: string): string {
  return CLIENT_HISTORY_OUTCOME_WORDS[outcome] ?? outcome;
}

export function telemetryCapabilityChips(client: DownloadClientTelemetrySnapshot) {
  const caps = client.capabilities;
  return [
    { label: caps.supportsQueue ? "Queue telemetry" : "No queue telemetry", enabled: caps.supportsQueue },
    { label: caps.supportsHistory ? "History" : "History limited", enabled: caps.supportsHistory },
    { label: caps.supportsPauseResume ? "Pause/resume" : "No pause/resume", enabled: caps.supportsPauseResume },
    { label: caps.supportsRemove ? "Remove" : "No remove", enabled: caps.supportsRemove },
    { label: caps.supportsRecheck ? "Recheck" : "No recheck", enabled: caps.supportsRecheck },
    { label: caps.supportsImportPath ? "Import path" : "Path limited", enabled: caps.supportsImportPath },
    { label: authModeLabel(caps.authMode), enabled: caps.authMode !== "unknown" }
  ];
}

export function authModeLabel(mode: string) {
  switch (mode) {
    case "api-key":
      return "API key";
    case "basic":
      return "Basic auth";
    case "basic-token":
      return "Token auth";
    case "form":
      return "Web login";
    case "password":
      return "Password";
    default:
      return "Auth unknown";
  }
}

/**
 * A pushed progress reading for one download, written into the client telemetry the Download
 * Clients tab shows (#1179): that item's progress and speed change, and the client's and the
 * overall speed move by the difference. Returns the same object when the download is not in the
 * overview, so the caller can leave the cache alone.
 */
export function patchTelemetryProgress(
  overview: DownloadTelemetryOverview | null | undefined,
  reading: { id: string; progress: number; speedMbps: number }
): DownloadTelemetryOverview | null | undefined {
  if (!overview) return overview;
  let speedChange = 0;
  let found = false;
  const clients = overview.clients.map((client) => {
    const index = client.queue.findIndex((item) => item.id === reading.id);
    if (index < 0) return client;
    found = true;
    const before = client.queue[index]!;
    const delta = reading.speedMbps - before.speedMbps;
    speedChange += delta;
    const queue = client.queue.map((item, position) => (position === index ? { ...item, progress: reading.progress, speedMbps: reading.speedMbps } : item));
    return { ...client, queue, summary: { ...client.summary, totalSpeedMbps: Math.max(0, client.summary.totalSpeedMbps + delta) } };
  });
  if (!found) return overview;
  return { ...overview, clients, summary: { ...overview.summary, totalSpeedMbps: Math.max(0, overview.summary.totalSpeedMbps + speedChange) } };
}
