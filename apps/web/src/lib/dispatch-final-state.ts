import type { DownloadDispatchItem } from "./api";
import type { Tone } from "./status-tones";

/**
 * Where a send to a download client stands on Activity (#1179). The send's own `status` stays "sent" after the
 * download is imported, refused or taken away, so reading only it left such rows "In progress" for good. The
 * import and the grab say how it ended, and are read first.
 */
export interface DispatchStanding {
  /** Still going, stopped, waiting for a decision, or finished (imported, refused or removed). */
  status: "in-progress" | "failed" | "needs-decision" | "done";
  pill: { tone: Tone; label: string };
  /** The ending in words, when it has ended. */
  detail?: string;
}

const lower = (value: string | null | undefined) => value?.trim().toLowerCase() ?? "";

const FAILED_IMPORT = ["failed", "importfailed"];

/** The import for this send ended badly: refused, failed or taken away. A failed import is never "Imported". */
export function dispatchImportFailed(dispatch: DownloadDispatchItem): boolean {
  return FAILED_IMPORT.includes(lower(dispatch.importStatus));
}

/**
 * The one rule for "imported", for every screen. The import time is set when an import ENDS, failed or not,
 * so it alone never means Imported: a failed import wins over it.
 */
export function dispatchWasImported(dispatch: DownloadDispatchItem): boolean {
  if (dispatchImportFailed(dispatch)) return false;
  const importStatus = lower(dispatch.importStatus);
  if (importStatus === "imported" || importStatus === "completed") return true;
  if (importStatus) return false;
  return Boolean(dispatch.importCompletedUtc)
    && !dispatch.importFailureCode && !dispatch.importFailureMessage
    && !FAILED_IMPORT.includes(lower(dispatch.status)) && lower(dispatch.grabStatus) !== "failed";
}

/** The import has ended one way or the other: nothing more is coming back for it. */
export function dispatchImportEnded(dispatch: DownloadDispatchItem | null | undefined): boolean {
  return Boolean(dispatch) && (dispatchImportFailed(dispatch!) || dispatchWasImported(dispatch!) || Boolean(dispatch!.importCompletedUtc));
}

/**
 * Another copy of the same release for the same title is already in the library: this send was contained (the
 * processor was handed the other copy, and nothing is owed to this one), so it is not still in progress (#1202).
 */
export function dispatchIsExtraCopy(dispatch: DownloadDispatchItem, all: readonly DownloadDispatchItem[]): boolean {
  if (dispatchWasImported(dispatch) || dispatchImportFailed(dispatch)) return false;
  const release = lower(dispatch.releaseName);
  return all.some((other) =>
    other.id !== dispatch.id
    && other.entityId === dispatch.entityId
    && lower(other.releaseName) === release
    && dispatchWasImported(other));
}

/** Deluno contained this send (another copy was handed over) and closed it for good, with its reason on the record (#1215). */
export const CONTAINED_SEND_CODE = "handed-over-elsewhere";

export function dispatchStanding(dispatch: DownloadDispatchItem, all: readonly DownloadDispatchItem[] = []): DispatchStanding | null {
  const status = lower(dispatch.status);
  const grab = lower(dispatch.grabStatus);
  const importStatus = lower(dispatch.importStatus);

  const reason = lower(`${dispatch.importFailureCode ?? ""} ${dispatch.importFailureMessage ?? ""}`);
  const refusedByImport = reason.includes("refus") || reason.includes("rejected");
  const removedByImport = reason.includes("removed") || reason.includes("vanished");
  // A failed import wins over a completion time left on the row: only a real, completed import reads Imported (#1179).
  // A failed send that was dismissed, or settled by something else, is archived: it is closed, not still failed (#1218).
  if (status === "archived" && (dispatchImportFailed(dispatch) || grab === "failed")) {
    return { status: "done", pill: { tone: "idle", label: "Closed" }, detail: dispatch.importFailureMessage ?? dispatch.grabMessage ?? "Dismissed after it failed" };
  }
  if (dispatchImportFailed(dispatch)) {
    if (refusedByImport) {
      return { status: "done", pill: { tone: "warn", label: "Refused" }, detail: dispatch.importFailureMessage ?? "The release was refused" };
    }
    if (removedByImport) {
      return { status: "done", pill: { tone: "idle", label: "Removed" }, detail: "Taken out of the download client" };
    }
    return { status: "failed", pill: { tone: "bad", label: "Failed" }, detail: dispatch.importFailureMessage ?? undefined };
  }
  if (dispatchWasImported(dispatch)) {
    return {
      status: "done",
      pill: { tone: "ok", label: "Imported" },
      detail: dispatch.importedFilePath ? `Imported as ${dispatch.importedFilePath.split(/[\\/]/).pop()}` : "Imported into the library"
    };
  }
  if (status === "failed" || status === "importfailed" || grab === "failed") {
    return { status: "failed", pill: { tone: "bad", label: "Failed" } };
  }
  if (grab === "refused" || grab === "blocked" || grab === "rejected" || status === "refused" || status === "blocked") {
    return { status: "done", pill: { tone: "warn", label: "Refused" }, detail: dispatch.grabMessage ?? "The download client turned it down" };
  }
  if (lower(dispatch.importFailureCode) === CONTAINED_SEND_CODE) {
    return { status: "done", pill: { tone: "idle", label: "Not needed" }, detail: dispatch.importFailureMessage ?? "Not needed: another copy was handed over" };
  }
  if (status === "archived" || status === "removed" || status === "vanished" || status === "cancelled") {
    return { status: "done", pill: { tone: "idle", label: "Removed" }, detail: "Taken out of the download client" };
  }
  if (status === "sent" && dispatchIsExtraCopy(dispatch, all)) {
    return { status: "done", pill: { tone: "idle", label: "Not needed" }, detail: "The same release was already in the library, so this extra copy was not used." };
  }
  if (status === "sent") return { status: "in-progress", pill: { tone: "info", label: "In progress" } };
  if (status === "planned") return { status: "needs-decision", pill: { tone: "warn", label: "Needs a decision" } };
  return null;
}
