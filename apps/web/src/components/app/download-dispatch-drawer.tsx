import { useState } from "react";
import type { DownloadDispatchItem } from "../../lib/api";
import { dispatchStanding } from "../../lib/dispatch-final-state";
import { dismissFailedDispatch } from "../../lib/failed-jobs";
import { Button } from "../ui/button";
import { toast } from "../shell/toaster";
import { formatDateTime, useDisplayPreferences } from "../../lib/display-preferences";
import { getDispatchStatusLabel as formatDispatchStatus } from "../../lib/job-status-constants";
import { CHOSEN_ON_SEARCH_LABEL, isChosenOnSearch } from "../../lib/dispatch-target";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../ui/drawer";
import { BlockReleaseAction } from "./block-release-action";
import { RemoveDownloadAction } from "./remove-download-action";

export function DownloadDispatchDrawer({
  dispatch,
  onClose,
  onBlocked
}: {
  dispatch: DownloadDispatchItem | null;
  onClose: () => void;
  /** Revalidate whatever list this drawer was opened from, once a release is blocked (#925) or removed (#924). */
  onBlocked?: () => void;
}) {
  const { preferences } = useDisplayPreferences();
  const nextEligibleUtc = dispatch ? dispatchNextEligibleUtc(dispatch) : null;
  const [dismissing, setDismissing] = useState(false);
  // A failed send is counted under Activity's Failed, so its drawer can clear it (#1218). Lifting a block stays apart.
  const canDismiss = dispatch !== null && dispatchStanding(dispatch)?.status === "failed";

  async function dismissFailed() {
    if (!dispatch) return;
    setDismissing(true);
    try {
      await dismissFailedDispatch(dispatch.id);
      toast.success(`${dispatch.releaseName} dismissed`);
      onClose();
      onBlocked?.();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That download could not be dismissed.");
    } finally {
      setDismissing(false);
    }
  }

  return (
    <Drawer
      open={dispatch !== null}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
      title={dispatch?.releaseName ?? "Download handoff"}
      description={dispatch ? `${dispatch.downloadClientName} · ${formatDispatchStatus(dispatch.status)}` : undefined}
      footer={
        <DrawerFooter
          state="clean"
          readOnly
          saveLabel="Close"
          onCancel={onClose}
          extraAction={dispatch ? (
            <>
              {canDismiss ? (
                <Button type="button" variant="outline" size="sm" disabled={dismissing} onClick={() => void dismissFailed()} aria-label={`Dismiss the failed download ${dispatch.releaseName}`} title="Stops counting this as failed. Nothing is deleted, and any block on the release stays until you lift it.">
                  Dismiss
                </Button>
              ) : null}
              <RemoveDownloadAction
                releaseName={dispatch.releaseName}
                indexerName={dispatch.indexerName}
                hash={dispatch.torrentHashOrItemId}
                mediaType={dispatch.mediaType}
                entityId={dispatch.entityId}
                title={dispatch.releaseName}
                onRemoved={() => {
                  onClose();
                  onBlocked?.();
                }}
              />
              <BlockReleaseAction
                releaseName={dispatch.releaseName}
                indexerName={dispatch.indexerName}
                hash={dispatch.torrentHashOrItemId}
                mediaType={dispatch.mediaType}
                entityId={dispatch.entityId}
                onBlocked={() => {
                  onClose();
                  onBlocked?.();
                }}
              />
            </>
          ) : null}
        />
      }
    >
      {dispatch ? (
        <>
          <DrawerSection title="Handoff" aside={formatDispatchStatus(dispatch.status)}>
            <DrawerFacts items={[
              { label: "Indexer", value: dispatch.indexerName },
              { label: "Download client", value: dispatch.downloadClientName },
              { label: "Sent", value: formatDateTime(dispatch.createdUtc, preferences) },
              { label: "Attempts", value: String(dispatch.attemptCount ?? (dispatch.grabAttemptedUtc ? 1 : 0)) }
            ]} />
          </DrawerSection>

          <DrawerSection title="Journey">
            <DrawerFacts items={[
              { label: "Grab", value: formatDispatchStage(dispatch.grabStatus, dispatch.status) },
              { label: "Detected by client", value: dispatch.detectedUtc ? formatDateTime(dispatch.detectedUtc, preferences) : "Not detected" },
              { label: "Import", value: formatDispatchStage(dispatch.importStatus, "Not started") },
              { label: "Imported file", value: dispatch.importedFilePath ?? "—", mono: Boolean(dispatch.importedFilePath) }
            ]} />
          </DrawerSection>

          {dispatch.failure ? (
            <DrawerSection title="Why it failed" aside={integrationRetryLabel(dispatch.failure.retryState)}>
              <p className="text-(length:--type-body-sm) leading-relaxed text-destructive">{dispatch.failure.summary}</p>
              <p className="mt-2 text-(length:--type-caption) leading-relaxed text-muted-foreground">
                {dispatch.failure.serviceName} · {formatOperation(dispatch.failure.operation)}
              </p>
              {dispatch.failure.upstreamDetail ? (
                <p className="mt-2 whitespace-pre-wrap text-(length:--type-caption) leading-relaxed text-muted-foreground">
                  Client detail: {dispatch.failure.upstreamDetail}
                </p>
              ) : null}
              <p className="mt-3 text-(length:--type-body-sm) leading-relaxed text-foreground">{dispatch.failure.nextAction}</p>
              {nextEligibleUtc ? (
                <p className="mt-2 text-(length:--type-caption) text-muted-foreground">
                  Next eligible attempt: {formatDateTime(nextEligibleUtc, preferences)}
                </p>
              ) : null}
            </DrawerSection>
          ) : dispatch.importFailureMessage ? (
            <DrawerSection title="Why import failed">
              <p className="whitespace-pre-wrap text-(length:--type-body-sm) leading-relaxed text-destructive">{dispatch.importFailureMessage}</p>
            </DrawerSection>
          ) : null}

          <DrawerSection title="Trace">
            <DrawerFacts items={[
              { label: "Deluno dispatch", value: dispatch.id, mono: true },
              { label: "Client item", value: dispatch.failure?.externalId ?? dispatch.torrentHashOrItemId ?? "Not reported", mono: true },
              isChosenOnSearch(dispatch.entityType)
                ? { label: "Media", value: CHOSEN_ON_SEARCH_LABEL }
                : { label: "Media", value: `${dispatch.entityType} · ${dispatch.entityId}`, mono: true },
              { label: "Library", value: dispatch.libraryId, mono: true }
            ]} />
          </DrawerSection>
        </>
      ) : null}
    </Drawer>
  );
}

function formatDispatchStage(value: string | null | undefined, fallback: string) {
  return value ? formatOperation(value) : fallback;
}

function formatOperation(value: string) {
  const spaced = value.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[._-]/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function integrationRetryLabel(retryState: string) {
  switch (retryState) {
    case "Retrying": return "Retrying";
    case "RetryScheduled": return "Retry scheduled";
    case "CircuitOpen": return "Paused after failures";
    case "ManualAction": return "Needs action";
    case "NotRetryable": return "Will not retry";
    default: return formatOperation(retryState || "Retry status unknown");
  }
}

function dispatchNextEligibleUtc(dispatch: DownloadDispatchItem) {
  return dispatch.failure?.retryAfterUtc ?? dispatch.circuitOpenUntilUtc ?? null;
}
