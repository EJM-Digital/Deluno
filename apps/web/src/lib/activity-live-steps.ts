/**
 * The three steps Activity lists from what Transfers reads live (#1115): files handed to the external processor
 * and back, download-health findings, and what the download clients reported. The backend's event history names
 * its own kinds; these three have rows of their own, so they are extra choices in the "Any step" picker and are
 * built here from the same sources Transfers used, not stored a second time.
 *
 * Not listed, on purpose: whether a client is online or paused. That is a state, not something that happened, and
 * it stays on Transfers and Connections.
 */
import type { DownloadHealthRecord, DownloadTelemetryOverview, ProcessorHandoffItem } from "./api";
import { clientHistoryOutcomeLabel } from "./download-telemetry";
import { handoffNeedsPerson, isWithProcessor } from "./needs-action";

export const LIVE_STEPS = [
  { value: "processing", label: "Processing" },
  { value: "download-health", label: "Download health" },
  { value: "download-client", label: "Download client" }
] as const;

export type LiveStep = (typeof LIVE_STEPS)[number]["value"];

export function isLiveStep(type: string): type is LiveStep {
  return LIVE_STEPS.some((step) => step.value === type);
}

export interface LiveStepEntry {
  key: string;
  step: LiveStep;
  status: "in-progress" | "done" | "failed" | "needs-decision";
  whenUtc: string;
  title: string;
  /** The line under the title. */
  stepLabel: string;
  detail: string;
  detailSub?: string;
  facts: { label: string; value: string; mono?: boolean }[];
  /** Set on a failed row that a person can clear from its drawer (#1218): the POST that dismisses it. */
  dismissPath?: string;
}

function handoffDetail(handoff: ProcessorHandoffItem): string {
  if (handoff.failureMessage) return handoff.failureMessage;
  if (isWithProcessor(handoff.status)) return "With the processor";
  if (handoff.outputPath) return "Processed and handed back";
  return "Hand-off finished";
}

export function processingEntries(handoffs: ProcessorHandoffItem[]): LiveStepEntry[] {
  return handoffs.map((handoff) => ({
    key: `processing-${handoff.id}`,
    step: "processing",
    status: handoffNeedsPerson(handoff) ? "failed" : isWithProcessor(handoff.status) ? "in-progress" : "done",
    whenUtc: handoff.updatedUtc,
    title: handoff.releaseName,
    stepLabel: handoff.processorName ?? "Processor",
    detail: handoffDetail(handoff),
    detailSub: handoff.outputPath ?? handoff.sourcePath,
    dismissPath: handoffNeedsPerson(handoff) ? `/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/dismiss` : undefined,
    facts: [
      { label: "Processor", value: handoff.processorName ?? "Processor" },
      { label: "Sent", value: handoff.sourcePath, mono: true },
      ...(handoff.outputPath ? [{ label: "Came back as", value: handoff.outputPath, mono: true }] : [])
    ]
  }));
}

export function downloadHealthEntries(records: DownloadHealthRecord[]): LiveStepEntry[] {
  return records.map((record) => ({
    key: `download-health-${record.clientId}-${record.queueItemId}-${record.kind}`,
    step: "download-health",
    status: record.severity === "critical" ? "failed" : "needs-decision",
    whenUtc: record.lastObservedUtc,
    title: record.releaseName,
    stepLabel: "Download health",
    detail: record.evidence,
    detailSub: record.strikeCount > 1 ? `Seen ${record.strikeCount} times` : undefined,
    facts: [
      { label: "Times seen", value: String(record.strikeCount) }
    ]
  }));
}

export function downloadClientEntries(telemetry: DownloadTelemetryOverview): LiveStepEntry[] {
  const entries: LiveStepEntry[] = [];
  for (const client of telemetry.clients) {
    for (const item of client.history) {
      const problem = Boolean(item.errorMessage || item.failure);
      entries.push({
        key: `download-client-${client.clientId}-${item.id}`,
        step: "download-client",
        status: problem ? "failed" : "done",
        whenUtc: item.completedUtc,
        title: item.title || item.releaseName,
        stepLabel: client.clientName,
        detail: item.errorMessage ?? item.failure?.summary ?? item.failure?.message ?? `The client reported: ${clientHistoryOutcomeLabel(item.outcome)}`,
        detailSub: item.indexerName || undefined,
        facts: [
          { label: "Client", value: client.clientName },
          ...(item.indexerName ? [{ label: "From", value: item.indexerName }] : []),
          { label: "Reported", value: clientHistoryOutcomeLabel(item.outcome) }
        ]
      });
    }
  }
  return entries;
}
