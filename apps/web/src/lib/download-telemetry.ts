import type { DownloadClientTelemetrySnapshot } from "./api";

export const downloadQueueStatuses = {
  downloading: "downloading",
  queued: "queued",
  completed: "completed",
  stalled: "stalled",
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
      stalled: "Stalled"
    } as Record<string, string>
  )[status] ?? status;
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
