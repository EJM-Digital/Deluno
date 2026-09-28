import type { DownloadClientTelemetrySnapshot } from "./api";

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
  importReady: "importReady",
  importQueued: "importQueued",
  imported: "imported",
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
      processing: "Processing",
      processed: "Processed",
      processingFailed: "Processing failed",
      importQueued: "Import queued",
      imported: "Imported",
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
