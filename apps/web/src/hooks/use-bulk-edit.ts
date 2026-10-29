import { useEffect, useState, type Dispatch, type SetStateAction } from "react";
import { fetchJson, type LibraryItem, type QualityProfileItem } from "../lib/api";
import type { MediaItem } from "../lib/media-types";
import { authedFetch } from "../lib/use-auth";
import { toast } from "../components/shell/toaster";
import { moveRefusalLines, toastMoveRefusals, type MoveResult } from "../lib/move-refusals";
import { describeRequestFailure, RequestFailedError } from "../lib/search-reasons";
import { plural } from "../lib/plural";

export type BulkWorkflowOperation = "monitoring" | "quality" | "minimumAvailability" | "reassignLibrary" | "tags" | "search" | "renamePreview";

export type BulkTagMode = "add" | "remove" | "replace";

export interface BulkRenamePreviewItem {
  itemId: string;
  title: string;
  year: number | null;
  template: string;
  proposedName: string;
}

interface BulkHistoryEntry {
  label: string;
  undoLabel: string;
  redoLabel: string;
  undo: () => Promise<void>;
  redo: () => Promise<void>;
}

export function useBulkEdit({
  libraryItems, setLibraryItems, variant
}: {
  libraryItems: MediaItem[];
  setLibraryItems: Dispatch<SetStateAction<MediaItem[]>>;
  variant: "movies" | "shows";
}) {
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [isBulkUpdating, setIsBulkUpdating] = useState(false);
  const [isBulkToolsOpen, setIsBulkToolsOpen] = useState(false);
  const [isRemovalConfirmationOpen, setIsRemovalConfirmationOpen] = useState(false);
  const [bulkOperation, setBulkOperation] = useState<BulkWorkflowOperation>("monitoring");
  const [bulkMonitored, setBulkMonitored] = useState(true);
  const [bulkQualityProfileId, setBulkQualityProfileId] = useState("");
  const [bulkMinimumAvailability, setBulkMinimumAvailability] = useState("released");
  const [bulkTargetLibraryId, setBulkTargetLibraryId] = useState("");
  // Reassigning a library only re-points the record by default (#787): files
  // stay under the old root unless this is explicitly turned on, so a bulk
  // move can never orphan a title's files as a side effect of tidying up
  // where it is filed.
  const [bulkMoveFiles, setBulkMoveFiles] = useState(false);
  const [bulkTagsInput, setBulkTagsInput] = useState("");
  const [bulkTagsMode, setBulkTagsMode] = useState<BulkTagMode>("add");
  const [bulkRenameTemplate, setBulkRenameTemplate] = useState("");
  const [bulkRenamePreview, setBulkRenamePreview] = useState<BulkRenamePreviewItem[]>([]);
  const [isApplyingRename, setIsApplyingRename] = useState(false);
  const [bulkConfirming, setBulkConfirming] = useState(false);
  const [bulkError, setBulkError] = useState<string | null>(null);
  const [bulkLibraries, setBulkLibraries] = useState<LibraryItem[]>([]);
  const [bulkQualityProfiles, setBulkQualityProfiles] = useState<QualityProfileItem[]>([]);
  const [bulkOptionsLoading, setBulkOptionsLoading] = useState(false);
  const [undoStack, setUndoStack] = useState<BulkHistoryEntry[]>([]);
  const [redoStack, setRedoStack] = useState<BulkHistoryEntry[]>([]);
  // "Select all" is a standing intent, not a one-time snapshot of whatever
  // happened to be loaded when the button was clicked (#789). While this is
  // true, every id `libraryItems` holds is selected — including ones the
  // background fill appends after the click, and the ones a narrower filter
  // swaps in later. It goes false the moment a single row is toggled by hand,
  // or the whole selection is cleared, so a partial pick is never mistaken
  // for "everything".
  const [selectAllActive, setSelectAllActive] = useState(false);

  useEffect(() => {
    if (!isBulkToolsOpen) return;
    let cancelled = false;
    setBulkOptionsLoading(true);
    setBulkError(null);
    Promise.all([fetchJson<LibraryItem[]>("/api/libraries"), fetchJson<QualityProfileItem[]>("/api/quality-profiles")])
      .then(([libraries, profiles]) => {
        if (cancelled) return;
        const mediaType = variant === "movies" ? "movies" : "tv";
        const matchingLibraries = libraries.filter((item) => item.mediaType.toLowerCase() === mediaType);
        const matchingProfiles = profiles.filter((item) => item.mediaType.toLowerCase() === mediaType);
        setBulkLibraries(matchingLibraries);
        setBulkQualityProfiles(matchingProfiles);
        setBulkTargetLibraryId((current) => current || matchingLibraries[0]?.id || "");
      })
      .catch((error) => { if (!cancelled) setBulkError(error instanceof Error ? error.message : "Could not load bulk operation options."); })
      .finally(() => { if (!cancelled) setBulkOptionsLoading(false); });
    return () => { cancelled = true; };
  }, [isBulkToolsOpen, variant]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === "Escape" && selectedIds.length > 0) {
        event.preventDefault();
        setSelectedIds([]);
        setSelectAllActive(false);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [selectedIds.length]);

  // The shelf keeps loading after the click (background fill, or a follow-up
  // filter narrowing the same query) and this is what keeps "select all"
  // meaning all of it rather than "all of it as of a moment ago". Every time
  // the loaded set changes while the intent is active, the selection is
  // resynced to match it exactly.
  useEffect(() => {
    if (!selectAllActive) return;
    setSelectedIds(libraryItems.map((item) => item.id));
  }, [libraryItems, selectAllActive, setSelectedIds]);

  async function applyMonitoring(ids: string[], monitored: boolean) {
    const response = await authedFetch(
      variant === "movies" ? "/api/movies/monitoring" : "/api/series/monitoring",
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          variant === "movies"
            ? { movieIds: ids, monitored }
            : { seriesIds: ids, monitored }
        )
      }
    );

    if (!response.ok) {
      throw new Error("bulk-monitoring-failed");
    }
  }

  async function applyQualityProfile(ids: string[], qualityProfileId: string) {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/quality-profile"
      : "/api/series/bulk/quality-profile";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, qualityProfileId }
          : { seriesIds: ids, qualityProfileId }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-quality-failed");
    }
  }

  async function applyTags(ids: string[], tags: string[], mode: BulkTagMode = "add") {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/tags"
      : "/api/series/bulk/tags";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, tags: tags.join(", "), mode }
          : { seriesIds: ids, tags: tags.join(", "), mode }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-tags-failed");
    }
  }

  function mergeTagsForMode(currentTags: string[], requestedTags: string[], mode: BulkTagMode): string[] {
    if (mode === "replace") {
      return requestedTags;
    }

    if (mode === "remove") {
      const removed = new Set(requestedTags.map((tag) => tag.toLowerCase()));
      return currentTags.filter((tag) => !removed.has(tag.toLowerCase()));
    }

    const merged = [...currentTags];
    for (const tag of requestedTags) {
      if (!merged.some((existing) => existing.toLowerCase() === tag.toLowerCase())) {
        merged.push(tag);
      }
    }
    return merged;
  }

  async function applyReassignLibrary(ids: string[], fromLibraryId: string, toLibraryId: string, moveFiles = false) {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/reassign-library"
      : "/api/series/bulk/reassign-library";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, fromLibraryId, toLibraryId, moveFiles }
          : { seriesIds: ids, fromLibraryId, toLibraryId, moveFiles }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-reassign-failed");
    }

    return await response.json() as MoveResult;
  }

  async function applyMinimumAvailability(ids: string[], minimumAvailability: string) {
    const response = await authedFetch("/api/movies/bulk/minimum-availability", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ movieIds: ids, minimumAvailability })
    });

    if (!response.ok) {
      throw new Error("bulk-minimum-availability-failed");
    }
  }

  async function applySearchNow(ids: string[]) {
    const endpoint = variant === "movies" ? "/api/movies/bulk/search" : "/api/series/bulk/search";
    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(variant === "movies" ? { movieIds: ids } : { seriesIds: ids })
    });

    if (!response.ok) {
      throw new RequestFailedError(response, "bulk-search-failed");
    }
  }

  async function loadQualityProfileByIdMap(ids: string[]): Promise<Map<string, string | null>> {
    const endpointRoot = variant === "movies" ? "/api/movies" : "/api/series";
    const entries = await Promise.all(
      ids.map(async (id) => {
        const response = await authedFetch(`${endpointRoot}/${id}`);
        if (!response.ok) {
          return [id, null] as const;
        }

        const detail = await response.json() as { qualityProfileId?: string | null };
        return [id, detail.qualityProfileId ?? null] as const;
      })
    );

    return new Map(entries);
  }

  function normalizeBulkTags(rawTags: string): string[] {
    return rawTags
      .split(/[,\n;]/g)
      .map((tag) => tag.trim())
      .filter((tag) => tag.length > 0)
      .filter((tag, index, arr) =>
        arr.findIndex((candidate) => candidate.toLowerCase() === tag.toLowerCase()) === index
      );
  }

  function pushHistory(entry: BulkHistoryEntry) {
    setUndoStack((current) => [...current, entry]);
    setRedoStack([]);
  }

  async function handleBulkMonitoring(monitored: boolean) {
    if (!selectedIds.length) return;
    setIsBulkUpdating(true);
    try {
      const response = await authedFetch(
        variant === "movies" ? "/api/movies/monitoring" : "/api/series/monitoring",
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(
            variant === "movies"
              ? { movieIds: selectedIds, monitored }
              : { seriesIds: selectedIds, monitored }
          )
        }
      );
      if (!response.ok) throw new Error("bulk-monitoring-failed");
      setLibraryItems((current) =>
        current.map((item) =>
          selectedIds.includes(item.id)
            ? {
                ...item,
                // Monitoring is a policy choice; availability is a separate fact.
                // Never turn an un-downloaded title into "downloaded" just because
                // someone starts monitoring it (or vice versa).
                monitored
              }
            : item
        )
      );
      toast.success(
        monitored
          ? `${plural(selectedIds.length, "title")} now monitored`
          : `${plural(selectedIds.length, "title")} unmonitored`
      );
      setSelectedIds([]);
    } catch {
      toast.error("Bulk update failed", {
        description: "Something went wrong while reaching the Deluno API."
      });
    } finally {
      setIsBulkUpdating(false);
    }
  }

  async function handleBulkSearchNow() {
    const selectedItems = libraryItems.filter((item) => selectedIds.includes(item.id));
    if (!selectedItems.length) {
      toast.info("No titles selected for search.");
      return;
    }
    setIsBulkUpdating(true);
    const loadingId = toast.loading(
      `Searching ${plural(selectedItems.length, "title")}…`
    );
    try {
      await applySearchNow(selectedIds);
      toast.success(
        `Manual search dispatched for ${plural(selectedItems.length, "title")}`,
        { id: loadingId }
      );
      setSelectedIds([]);
    } catch (searchError) {
      const explained = await describeRequestFailure(null, searchError, {
        action: "start that search",
      });
      toast.error(explained.title, { id: loadingId, description: explained.description });
    } finally {
      setIsBulkUpdating(false);
    }
  }

  async function runUndo() {
    const entry = undoStack[undoStack.length - 1];
    if (!entry || isBulkUpdating) {
      return;
    }

    setIsBulkUpdating(true);
    try {
      await entry.undo();
      setUndoStack((current) => current.slice(0, -1));
      setRedoStack((current) => [...current, entry]);
      toast.success(entry.undoLabel);
    } catch {
      toast.error("Undo failed.");
    } finally {
      setIsBulkUpdating(false);
    }
  }

  async function runRedo() {
    const entry = redoStack[redoStack.length - 1];
    if (!entry || isBulkUpdating) {
      return;
    }

    setIsBulkUpdating(true);
    try {
      await entry.redo();
      setRedoStack((current) => current.slice(0, -1));
      setUndoStack((current) => [...current, entry]);
      toast.success(entry.redoLabel);
    } catch {
      toast.error("Redo failed.");
    } finally {
      setIsBulkUpdating(false);
    }
  }

  function openBulkTools(operation: BulkWorkflowOperation = "monitoring", monitored = true) {
    if (selectedIds.length === 0) {
      toast.info("Select at least one title first.");
      return;
    }

    setBulkOperation(operation);
    setBulkMonitored(monitored);
    setBulkTagsMode("add");
    setBulkMoveFiles(false);
    setBulkConfirming(false);
    setBulkError(null);
    setBulkRenamePreview([]);
    setIsBulkToolsOpen(true);
  }

  async function executeBulkToolsOperation() {
    if (selectedIds.length === 0) {
      setBulkError("Select at least one title.");
      return;
    }

    if (!bulkConfirming && bulkOperation !== "renamePreview") {
      setBulkConfirming(true);
      return;
    }

    setBulkError(null);
    const selectedItems = libraryItems.filter((item) => selectedIds.includes(item.id));

    try {
      setIsBulkUpdating(true);
      if (bulkOperation === "monitoring") {
        const previousTrue = selectedItems.filter((item) => item.monitored).map((item) => item.id);
        const previousFalse = selectedItems.filter((item) => !item.monitored).map((item) => item.id);
        const label = bulkMonitored ? "Monitor selected titles" : "Unmonitor selected titles";

        await applyMonitoring(selectedIds, bulkMonitored);
        setLibraryItems((current) =>
          current.map((item) => selectedIds.includes(item.id) ? { ...item, monitored: bulkMonitored } : item)
        );
        pushHistory({
          label,
          undoLabel: "Monitoring change reverted",
          redoLabel: "Monitoring change re-applied",
          undo: async () => {
            if (previousTrue.length > 0) {
              await applyMonitoring(previousTrue, true);
            }
            if (previousFalse.length > 0) {
              await applyMonitoring(previousFalse, false);
            }
            setLibraryItems((current) =>
              current.map((item) =>
                previousTrue.includes(item.id) ? { ...item, monitored: true }
                : previousFalse.includes(item.id) ? { ...item, monitored: false }
                : item
              )
            );
          },
          redo: async () => {
            await applyMonitoring(selectedIds, bulkMonitored);
            setLibraryItems((current) =>
              current.map((item) => selectedIds.includes(item.id) ? { ...item, monitored: bulkMonitored } : item)
            );
          }
        });
        toast.success(label);
      }
      else if (bulkOperation === "quality") {
        if (!bulkQualityProfileId.trim()) {
      setBulkError("Choose a quality profile first.");
          return;
        }

        const previousProfiles = await loadQualityProfileByIdMap(selectedIds);
        await applyQualityProfile(selectedIds, bulkQualityProfileId.trim());
        const profileName = bulkQualityProfiles.find((item) => item.id === bulkQualityProfileId)?.name ?? bulkQualityProfileId;
        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) ? { ...item, qualityProfile: profileName } : item
          )
        );

        pushHistory({
          label: "Apply quality profile",
          undoLabel: "Quality profile change reverted",
          redoLabel: "Quality profile change re-applied",
          undo: async () => {
            const groups = new Map<string, string[]>();
            previousProfiles.forEach((value, id) => {
              if (!value) return;
              const current = groups.get(value) ?? [];
              current.push(id);
              groups.set(value, current);
            });
            for (const [profileId, ids] of groups) {
              await applyQualityProfile(ids, profileId);
            }
          },
          redo: async () => {
            await applyQualityProfile(selectedIds, bulkQualityProfileId.trim());
          }
        });
      toast.success("Quality profile updated.");
      }
      else if (bulkOperation === "reassignLibrary") {
        if (!bulkTargetLibraryId.trim()) {
          setBulkError("Choose a destination library first.");
          return;
        }

        const previousByLibrary = new Map<string, string[]>();
        selectedItems.forEach((item) => {
          if (!item.libraryId) return;
          const current = previousByLibrary.get(item.libraryId) ?? [];
          current.push(item.id);
          previousByLibrary.set(item.libraryId, current);
        });

        const moveFiles = bulkMoveFiles;
        let totalFilesMoved = 0;
        let allMoveFailures: string[] = [];
        let refusalLines: string[] = [];
        let totalReassigned = 0;
        for (const [fromLibraryId, ids] of previousByLibrary) {
          if (fromLibraryId === bulkTargetLibraryId) {
            continue;
          }
          const result = await applyReassignLibrary(ids, fromLibraryId, bulkTargetLibraryId, moveFiles);
          totalFilesMoved += result.filesMoved ?? 0;
          allMoveFailures = [...allMoveFailures, ...(result.moveFailures ?? [])];
          refusalLines = [...refusalLines, ...moveRefusalLines(result)];
          totalReassigned += result.reassigned ?? 0;
        }

        // A refused title keeps its old library on screen, as it does on the host.
        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) && !allMoveFailures.includes(item.title)
              ? { ...item, libraryId: bulkTargetLibraryId }
              : item
          )
        );

        pushHistory({
          label: "Reassign library",
          undoLabel: "Library reassignment reverted",
          redoLabel: "Library reassignment re-applied",
          undo: async () => {
            for (const [oldLibraryId, ids] of previousByLibrary) {
              await applyReassignLibrary(ids, bulkTargetLibraryId, oldLibraryId, moveFiles);
            }
            setLibraryItems((current) =>
              current.map((item) => {
                for (const [oldLibraryId, ids] of previousByLibrary) {
                  if (ids.includes(item.id)) {
                    return { ...item, libraryId: oldLibraryId };
                  }
                }
                return item;
              })
            );
          },
          redo: async () => {
            for (const [fromLibraryId, ids] of previousByLibrary) {
              await applyReassignLibrary(ids, fromLibraryId, bulkTargetLibraryId, moveFiles);
            }
            setLibraryItems((current) =>
              current.map((item) => selectedIds.includes(item.id) ? { ...item, libraryId: bulkTargetLibraryId } : item)
            );
          }
        });
        // Plain words about what actually happened to the files, not only the
        // record — "only the record was reassigned" is the whole point of
        // #787 when moveFiles was left off.
        if (refusalLines.length > 0) {
          if (totalReassigned > 0) toast.success(`Library assignment updated for ${plural(totalReassigned, "title")}.`);
          toastMoveRefusals(refusalLines);
        } else if (!moveFiles) {
          toast.success("Library assignment updated. Files were left where they are.");
        } else {
          toast.success(`Library assignment updated. ${plural(totalFilesMoved, "file")} moved.`);
        }
      }
      else if (bulkOperation === "minimumAvailability") {
        const previousByAvailability = new Map<string, string[]>();
        selectedItems.forEach((item) => {
          const value = item.minimumAvailability ?? "released";
          const current = previousByAvailability.get(value) ?? [];
          current.push(item.id);
          previousByAvailability.set(value, current);
        });

        await applyMinimumAvailability(selectedIds, bulkMinimumAvailability);
        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) ? { ...item, minimumAvailability: bulkMinimumAvailability } : item
          )
        );

        pushHistory({
          label: "Set minimum availability",
          undoLabel: "Minimum availability reverted",
          redoLabel: "Minimum availability re-applied",
          undo: async () => {
            for (const [value, ids] of previousByAvailability) {
              await applyMinimumAvailability(ids, value);
            }
            setLibraryItems((current) =>
              current.map((item) => {
                for (const [value, ids] of previousByAvailability) {
                  if (ids.includes(item.id)) {
                    return { ...item, minimumAvailability: value };
                  }
                }
                return item;
              })
            );
          },
          redo: async () => {
            await applyMinimumAvailability(selectedIds, bulkMinimumAvailability);
            setLibraryItems((current) =>
              current.map((item) =>
                selectedIds.includes(item.id) ? { ...item, minimumAvailability: bulkMinimumAvailability } : item
              )
            );
          }
        });
        toast.success("Minimum availability updated.");
      }
      else if (bulkOperation === "tags") {
        const normalizedTags = normalizeBulkTags(bulkTagsInput);
        const mode = bulkTagsMode;
        const previousTags = new Map(selectedItems.map((item) => [item.id, item.tags ?? []] as const));
        const nextTagsById = new Map(
          selectedItems.map((item) => [item.id, mergeTagsForMode(item.tags ?? [], normalizedTags, mode)] as const)
        );
        await applyTags(selectedIds, normalizedTags, mode);
        setLibraryItems((current) =>
          current.map((item) =>
            nextTagsById.has(item.id) ? { ...item, tags: nextTagsById.get(item.id)! } : item
          )
        );
        const modeLabel = mode === "remove" ? "Remove tags" : mode === "replace" ? "Replace tags" : "Add tags";
        pushHistory({
          label: modeLabel,
          undoLabel: "Tags reverted",
          redoLabel: "Tags re-applied",
          undo: async () => {
            const groups = new Map<string, string[]>();
            previousTags.forEach((tags, id) => {
              const key = tags.join("||");
              const current = groups.get(key) ?? [];
              current.push(id);
              groups.set(key, current);
            });
            for (const [key, ids] of groups) {
              const tags = key ? key.split("||").filter(Boolean) : [];
              await applyTags(ids, tags, "replace");
            }
            setLibraryItems((current) =>
              current.map((item) =>
                selectedIds.includes(item.id) ? { ...item, tags: previousTags.get(item.id) ?? [] } : item
              )
            );
          },
          redo: async () => {
            await applyTags(selectedIds, normalizedTags, mode);
            setLibraryItems((current) =>
              current.map((item) =>
                nextTagsById.has(item.id) ? { ...item, tags: nextTagsById.get(item.id)! } : item
              )
            );
          }
        });
        toast.success("Tags updated.");
      }
      else if (bulkOperation === "search") {
        await applySearchNow(selectedIds);
        toast.success("Manual search dispatched.");
      }
      else if (bulkOperation === "renamePreview") {
        const endpoint = variant === "movies" ? "/api/movies/bulk/rename-preview" : "/api/series/bulk/rename-preview";
        const response = await authedFetch(endpoint, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(
            variant === "movies"
              ? { movieIds: selectedIds, template: bulkRenameTemplate.trim() || undefined }
              : { seriesIds: selectedIds, template: bulkRenameTemplate.trim() || undefined }
          )
        });

        if (!response.ok) {
          throw new Error("rename-preview-failed");
        }

        const payload = await response.json() as { previews?: Array<Record<string, unknown>> };
        const preview = (payload.previews ?? []).map((item) => ({
          itemId: String(item.movieId ?? item.seriesId ?? ""),
          title: String(item.title ?? ""),
          year: item.releaseYear === null || item.startYear === null
            ? null
            : Number(item.releaseYear ?? item.startYear ?? 0),
          template: String(item.template ?? ""),
          proposedName: String(item.proposedName ?? "")
        }));
        setBulkRenamePreview(preview);
        return;
      }

      setBulkConfirming(false);
      setIsBulkToolsOpen(false);
      setSelectAllActive(false);
      setSelectedIds([]);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Bulk operation failed.";
      setBulkError(message);
      toast.error("Bulk operation failed", { description: message });
    } finally {
      setIsBulkUpdating(false);
    }
  }

  /**
   * Commits the renames the preview just showed - the same selection and the
   * same template, so nobody has to redo the operation from scratch to get
   * from "here is what would happen" to "make it happen" (#788).
   */
  async function applyBulkRename() {
    if (bulkRenamePreview.length === 0 || isApplyingRename) {
      return;
    }

    const endpoint = variant === "movies" ? "/api/movies/bulk/rename-apply" : "/api/series/bulk/rename-apply";
    const ids = bulkRenamePreview.map((item) => item.itemId);
    setIsApplyingRename(true);
    try {
      const response = await authedFetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          variant === "movies"
            ? { movieIds: ids, template: bulkRenameTemplate.trim() || undefined }
            : { seriesIds: ids, template: bulkRenameTemplate.trim() || undefined }
        )
      });

      if (!response.ok) {
        throw new Error("rename-apply-failed");
      }

      const payload = await response.json() as {
        renamedCount?: number;
        failures?: Array<{ title?: string | null; reason?: string }>;
      };
      const renamedCount = payload.renamedCount ?? 0;
      const failures = payload.failures ?? [];

      if (renamedCount > 0) {
        toast.success(`${plural(renamedCount, "folder")} renamed.`);
      }
      if (failures.length > 0) {
        toast.error(
          `${plural(failures.length, "folder")} could not be renamed`,
          { duration: Infinity, description: failures.map((item) => `${item.title ?? "Untitled"}: ${item.reason ?? "unknown reason"}`).join("; ") }
        );
      }
      if (renamedCount === 0 && failures.length === 0) {
        toast.info("Nothing needed renaming.");
      }

      setBulkRenamePreview([]);
      if (failures.length === 0) {
        setIsBulkToolsOpen(false);
        setSelectAllActive(false);
        setSelectedIds([]);
      }
    } catch {
      toast.error("Could not apply the rename.");
    } finally {
      setIsApplyingRename(false);
    }
  }

  function toggleSelectedId(id: string) {
    // A hand-picked change always wins over the standing "select all" intent -
    // otherwise the very next loaded page would silently put back the one row
    // someone just deselected.
    setSelectAllActive(false);
    setSelectedIds((current) =>
      current.includes(id) ? current.filter((entry) => entry !== id) : [...current, id]
    );
  }

  function toggleSelectAllVisible() {
    const visibleIds = libraryItems.map((item) => item.id);
    const allVisibleSelected = visibleIds.length > 0 && visibleIds.every((id) => selectedIds.includes(id));
    if (allVisibleSelected) {
      setSelectAllActive(false);
      setSelectedIds([]);
    } else {
      setSelectAllActive(true);
      setSelectedIds(visibleIds);
    }
  }

  function clearSelection() {
    setSelectAllActive(false);
    setSelectedIds([]);
  }

  return {
    selectedIds, setSelectedIds, isBulkUpdating, setIsBulkUpdating, isBulkToolsOpen, setIsBulkToolsOpen,
    isRemovalConfirmationOpen, setIsRemovalConfirmationOpen, bulkOperation, setBulkOperation,
    bulkMonitored, setBulkMonitored, bulkQualityProfileId, setBulkQualityProfileId,
    bulkMinimumAvailability, setBulkMinimumAvailability,
    bulkTargetLibraryId, setBulkTargetLibraryId, bulkMoveFiles, setBulkMoveFiles,
    bulkTagsInput, setBulkTagsInput,
    bulkTagsMode, setBulkTagsMode,
    bulkRenameTemplate, setBulkRenameTemplate, bulkRenamePreview, setBulkRenamePreview,
    isApplyingRename, applyBulkRename,
    bulkConfirming, setBulkConfirming, bulkError, setBulkError, bulkLibraries, bulkQualityProfiles,
    bulkOptionsLoading, undoStack, redoStack, runUndo, runRedo, openBulkTools,
    executeBulkToolsOperation, toggleSelectedId, toggleSelectAllVisible,
    selectAllActive, clearSelection
  };
}
