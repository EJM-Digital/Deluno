import type { DispatchTimelineEvent } from "./api";

export interface FileProvenance {
  dispatchId: string | null;
  releaseNameEvidence: string | null;
  indexerName: string | null;
  downloadClientName: string | null;
  downloadClientItemId: string | null;
  sourcePath: string | null;
  destinationPath: string | null;
  rename: string | null;
  preferredTransferMode: string | null;
  transferModeUsed: string | null;
  usedFallback: boolean | null;
  importWorkflow: string | null;
  processorName: string | null;
  preferencePlanId: string | null;
  preferencePlanVersion: string | null;
  preferencePlanHash: string | null;
  preferenceStatus: string | null;
  matchedRuleNames: string | null;
  workflowCleanup: string | null;
}

/** Reads the newest complete file journey recorded for a dispatch. */
export function fileProvenanceFromTimeline(events: DispatchTimelineEvent[]): FileProvenance | null {
  const event = events.find((item) => item.eventType.toLowerCase() === "file_provenance_recorded");
  if (!event?.detailsJson) return null;

  try {
    const parsed = JSON.parse(event.detailsJson) as unknown;
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;
    const values = new Map(
      Object.entries(parsed as Record<string, unknown>)
        .map(([key, value]) => [key.toLowerCase(), value] as const)
    );
    const text = (key: string) => {
      const value = values.get(key.toLowerCase());
      return typeof value === "string" && value.trim() ? value.trim() : null;
    };
    const flag = (key: string) => {
      const value = values.get(key.toLowerCase());
      return typeof value === "boolean" ? value : null;
    };

    const result: FileProvenance = {
      dispatchId: text("dispatchId"),
      releaseNameEvidence: text("releaseNameEvidence"),
      indexerName: text("indexerName"),
      downloadClientName: text("downloadClientName"),
      downloadClientItemId: text("downloadClientItemId"),
      sourcePath: text("sourcePath"),
      destinationPath: text("destinationPath"),
      rename: text("rename"),
      preferredTransferMode: text("preferredTransferMode"),
      transferModeUsed: text("transferModeUsed"),
      usedFallback: flag("usedFallback"),
      importWorkflow: text("importWorkflow"),
      processorName: text("processorName"),
      preferencePlanId: text("preferencePlanId"),
      preferencePlanVersion: text("preferencePlanVersion"),
      preferencePlanHash: text("preferencePlanHash"),
      preferenceStatus: text("preferenceStatus"),
      matchedRuleNames: text("matchedRuleNames"),
      workflowCleanup: text("workflowCleanup")
    };

    return result.releaseNameEvidence || result.sourcePath || result.destinationPath ? result : null;
  } catch {
    return null;
  }
}
