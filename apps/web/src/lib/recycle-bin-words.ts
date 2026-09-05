/**
 * Saying what emptying the recycle bin would take, before it takes it.
 *
 * <p>DESIGN-007 decision 15: <i>"Enforce retention automatically, and show what
 * a manual empty takes"</i>. The empty used to delete first and count
 * afterwards, which is a report rather than a choice — and permanent deletion
 * is the one place a report after the fact is worth nothing.</p>
 *
 * <p>Its own module so the wording can be asserted. The awkward half — items
 * that have <em>not</em> expired going anyway because the bin is over its size
 * limit — is the half a single total would hide, and it is the only half
 * somebody might have wanted back.</p>
 */

/** Mirrors `Deluno.Platform.RecycleBinCleanupPreview`. */
export interface RecycleBinCleanupPreview {
  items: { id: string }[];
  expiredCount: number;
  overCapacityCount: number;
  bytesFreed: number;
}

export function describeCleanup(preview: RecycleBinCleanupPreview, formatBytes: (bytes: number) => string): string {
  if (!preview.items.length) {
    return "Nothing has passed its retention date, and the bin is within its size limit. Deluno would take nothing.";
  }

  const freed = formatBytes(preview.bytesFreed);
  const expired =
    preview.expiredCount === 1
      ? "1 item has passed its retention date"
      : `${preview.expiredCount} items have passed their retention date`;

  if (preview.overCapacityCount === 0) {
    return `${expired}. Emptying frees ${freed}. This cannot be undone.`;
  }

  // Named first, deliberately. These have not expired; they are going because
  // the bin is full, and burying that behind the routine number is how
  // somebody loses a file they were still deciding about.
  const early =
    preview.overCapacityCount === 1
      ? "1 item that has not expired yet will go too, because the bin is over its size limit"
      : `${preview.overCapacityCount} items that have not expired yet will go too, because the bin is over its size limit`;

  return `${early}. ${expired}. Emptying frees ${freed} in total. This cannot be undone.`;
}
