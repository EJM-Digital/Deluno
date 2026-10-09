import { localizeResetTimes } from "./reset-times";

/** The few fields of the update status the headline reads. */
export interface UpdateHeadlineInput {
  restartRequired: boolean;
  updateAvailable: boolean;
  state: string;
  message?: string | null;
}

/**
 * The headline over the update details. It follows the State beside it: a check that failed never reads
 * "Up to date" (it did not find that out). When the cause is known the server's own sentence is used, such as
 * "Couldn't check for updates (GitHub limit, retry after 9:21 PM)."
 */
export function updateHeadline(status: UpdateHeadlineInput): string {
  if (status.restartRequired) return "Restart to finish updating";
  if (status.state === "error") {
    const message = localizeResetTimes((status.message ?? "").trim()).replace(/\.$/, "");
    return message.startsWith("Couldn't check for updates") ? message : "Update needs attention";
  }
  if (status.updateAvailable) return "Update available";
  return "Up to date";
}

/**
 * What the "Latest version" row says (#1200). A failed check did not find out whether there is a newer version, so
 * it never reads "No update available"; it says the check could not run.
 */
export function latestVersionLabel(status: { state: string; latestVersion?: string | null }): string {
  if (status.latestVersion) return status.latestVersion;
  return status.state === "error" ? "Couldn't check" : "No update available";
}
