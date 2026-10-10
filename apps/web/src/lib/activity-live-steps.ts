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
  /** The JSON body that dismiss POST needs, when it needs one. */
  dismissBody?: unknown;
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

/** A finding a person dismissed (for a week, or until it happens again) stops counting (#1218). */
function findingIgnored(record: DownloadHealthRecord, now: number): boolean {
  return Boolean(record.ignoredUntilUtc) && new Date(record.ignoredUntilUtc as string).getTime() > now;
}

export function downloadHealthEntries(records: DownloadHealthRecord[], now: number = Date.now()): LiveStepEntry[] {
  return records.map((record) => {
    const ignored = findingIgnored(record, now);
    return {
      key: `download-health-${record.clientId}-${record.queueItemId}-${record.kind}`,
      step: "download-health" as const,
      status: ignored ? "done" as const : record.severity === "critical" ? "failed" as const : "needs-decision" as const,
      whenUtc: record.lastObservedUtc,
      title: record.releaseName,
      stepLabel: "Download health",
      detail: record.evidence,
      detailSub: ignored ? "Dismissed" : record.strikeCount > 1 ? `Seen ${record.strikeCount} times` : undefined,
      facts: [
        { label: "Times seen", value: String(record.strikeCount) }
      ],
      dismissPath: ignored ? undefined : `/api/download-clients/${encodeURIComponent(record.clientId)}/queue/${encodeURIComponent(record.queueItemId)}/health/${encodeURIComponent(record.kind)}/ignore`,
      dismissBody: { durationDays: 7 }
    };
  });
}

/** The key Deluno remembers a dismissed client-history row by (mirrors AttentionDismissalStore.ClientHistoryKey). */
export const clientHistoryKey = (clientId: string, itemId: string) => `client-history:${clientId}:${itemId}`;

export function downloadClientEntries(telemetry: DownloadTelemetryOverview, dismissed: ReadonlySet<string> = new Set()): LiveStepEntry[] {
  const entries: LiveStepEntry[] = [];
  for (const client of telemetry.clients) {
    for (const item of client.history) {
      const problem = Boolean(item.errorMessage || item.failure);
      const closed = problem && dismissed.has(clientHistoryKey(client.clientId, item.id));
      entries.push({
        key: `download-client-${client.clientId}-${item.id}`,
        step: "download-client",
        status: problem && !closed ? "failed" : "done",
        whenUtc: item.completedUtc,
        title: item.title || item.releaseName,
        stepLabel: client.clientName,
        detail: item.errorMessage ?? item.failure?.summary ?? item.failure?.message ?? `The client reported: ${clientHistoryOutcomeLabel(item.outcome)}`,
        detailSub: closed ? "Dismissed" : item.indexerName || undefined,
        dismissPath: problem && !closed ? `/api/download-clients/${encodeURIComponent(client.clientId)}/history/${encodeURIComponent(item.id)}/dismiss` : undefined,
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
