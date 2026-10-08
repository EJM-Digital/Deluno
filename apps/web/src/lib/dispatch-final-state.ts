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

export function dispatchStanding(dispatch: DownloadDispatchItem): DispatchStanding | null {
  const status = lower(dispatch.status);
  const grab = lower(dispatch.grabStatus);
  const importStatus = lower(dispatch.importStatus);

  if (importStatus === "imported" || importStatus === "completed" || dispatch.importCompletedUtc) {
    return {
      status: "done",
      pill: { tone: "ok", label: "Imported" },
      detail: dispatch.importedFilePath ? `Imported as ${dispatch.importedFilePath.split(/[\\/]/).pop()}` : "Imported into the library"
    };
  }
  if (status === "failed" || status === "importfailed" || importStatus === "failed" || grab === "failed") {
    return { status: "failed", pill: { tone: "bad", label: "Failed" } };
  }
  if (grab === "refused" || grab === "blocked" || grab === "rejected" || status === "refused" || status === "blocked") {
    return { status: "done", pill: { tone: "warn", label: "Refused" }, detail: dispatch.grabMessage ?? "The download client turned it down" };
  }
  if (status === "archived" || status === "removed" || status === "vanished" || status === "cancelled") {
    return { status: "done", pill: { tone: "idle", label: "Removed" }, detail: "Taken out of the download client" };
  }
  if (status === "sent") return { status: "in-progress", pill: { tone: "info", label: "In progress" } };
  if (status === "planned") return { status: "needs-decision", pill: { tone: "warn", label: "Needs a decision" } };
  return null;
}
