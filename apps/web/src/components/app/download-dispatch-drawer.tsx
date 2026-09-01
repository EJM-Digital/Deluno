import type { DownloadDispatchItem } from "../../lib/api";
import { formatDateTime, useDisplayPreferences } from "../../lib/display-preferences";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../ui/drawer";

export function DownloadDispatchDrawer({
  dispatch,
  onClose
}: {
  dispatch: DownloadDispatchItem | null;
  onClose: () => void;
}) {
  const { preferences } = useDisplayPreferences();
  const nextEligibleUtc = dispatch ? dispatchNextEligibleUtc(dispatch) : null;

  return (
    <Drawer
      open={dispatch !== null}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
      title={dispatch?.releaseName ?? "Download handoff"}
      description={dispatch ? `${dispatch.downloadClientName} · ${formatDispatchStatus(dispatch.status)}` : undefined}
      footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={onClose} />}
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
              <p className="text-[length:var(--type-body-sm)] leading-relaxed text-destructive">{dispatch.failure.summary}</p>
              <p className="mt-2 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">
                {dispatch.failure.serviceName} · {formatOperation(dispatch.failure.operation)}
              </p>
              {dispatch.failure.upstreamDetail ? (
                <p className="mt-2 whitespace-pre-wrap text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">
                  Client detail: {dispatch.failure.upstreamDetail}
                </p>
              ) : null}
              <p className="mt-3 text-[length:var(--type-body-sm)] leading-relaxed text-foreground">{dispatch.failure.nextAction}</p>
              {nextEligibleUtc ? (
                <p className="mt-2 text-[length:var(--type-caption)] text-muted-foreground">
                  Next eligible attempt: {formatDateTime(nextEligibleUtc, preferences)}
                </p>
              ) : null}
            </DrawerSection>
          ) : dispatch.importFailureMessage ? (
            <DrawerSection title="Why import failed">
              <p className="whitespace-pre-wrap text-[length:var(--type-body-sm)] leading-relaxed text-destructive">{dispatch.importFailureMessage}</p>
            </DrawerSection>
          ) : null}

          <DrawerSection title="Trace">
            <DrawerFacts items={[
              { label: "Deluno dispatch", value: dispatch.id, mono: true },
              { label: "Client item", value: dispatch.failure?.externalId ?? dispatch.torrentHashOrItemId ?? "Not reported", mono: true },
              { label: "Media", value: `${dispatch.entityType} · ${dispatch.entityId}`, mono: true },
              { label: "Library", value: dispatch.libraryId, mono: true }
            ]} />
          </DrawerSection>
        </>
      ) : null}
    </Drawer>
  );
}

function formatDispatchStatus(status: string) {
  switch (status) {
    case "sent": return "Sent";
    case "failed": return "Failed";
    case "planned": return "Needs URL";
    case "imported": return "Imported";
    case "importFailed": return "Import failed";
    default: return formatOperation(status);
  }
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
  return dispatch.failure?.retryAfterUtc ?? dispatch.nextRetryEligibleUtc ?? dispatch.circuitOpenUntilUtc ?? null;
}
