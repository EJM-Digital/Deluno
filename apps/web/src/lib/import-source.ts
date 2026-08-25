import type { DownloadQueueItem, LibraryItem } from "./api/types/resources";

/**
 * Resolves the only two supported import sources: the path reported by the
 * download client, or an explicit override on the matched library.
 */
export function resolveImportSourcePath(
  item: Pick<DownloadQueueItem, "sourcePath" | "libraryId">,
  libraries: ReadonlyArray<Pick<LibraryItem, "id" | "downloadsPath">>
) {
  return item.sourcePath || libraries.find((library) => library.id === item.libraryId)?.downloadsPath || null;
}
