/**
 * What removing a title does to its download. One source for the single-title
 * dialog, the bulk dialog and both toasts, so the words cannot drift from the
 * server: removing a title cancels its download in the client and deletes the
 * partly downloaded data with it (a title that has no download in flight has
 * nothing to cancel).
 */
export const removalDownloadSentence =
  "If it has a download in progress, that download is cancelled in your download client and its data is deleted.";

export const removalDownloadToastSentence =
  "Any download in progress was cancelled in your download client and its data deleted.";
