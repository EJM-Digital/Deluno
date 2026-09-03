import { useEffect, useState, type Dispatch, type SetStateAction } from "react";
import { fetchJson, type LibraryItem, type QualityProfileItem } from "../lib/api";
import type { MediaItem } from "../lib/media-types";
import { authedFetch } from "../lib/use-auth";
import { toast } from "../components/shell/toaster";
import { describeRequestFailure, RequestFailedError } from "../lib/search-reasons";

export type BulkWorkflowOperation = "monitoring" | "quality" | "reassignLibrary" | "tags" | "search" | "renamePreview";

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
  const [bulkTargetLibraryId, setBulkTargetLibraryId] = useState("");
  const [bulkTagsInput, setBulkTagsInput] = useState("");
  const [bulkRenameTemplate, setBulkRenameTemplate] = useState("");
  const [bulkRenamePreview, setBulkRenamePreview] = useState<BulkRenamePreviewItem[]>([]);
  const [bulkConfirming, setBulkConfirming] = useState(false);
  const [bulkError, setBulkError] = useState<string | null>(null);
  const [bulkLibraries, setBulkLibraries] = useState<LibraryItem[]>([]);
  const [bulkQualityProfiles, setBulkQualityProfiles] = useState<QualityProfileItem[]>([]);
  const [bulkOptionsLoading, setBulkOptionsLoading] = useState(false);
  const [undoStack, setUndoStack] = useState<BulkHistoryEntry[]>([]);
  const [redoStack, setRedoStack] = useState<BulkHistoryEntry[]>([]);

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
      if (event.key === "Escape" && selectedIds.length > 0) { event.preventDefault(); setSelectedIds([]); }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [selectedIds.length]);

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

  async function applyTags(ids: string[], tags: string[]) {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/tags"
      : "/api/series/bulk/tags";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, tags: tags.join(", ") }
          : { seriesIds: ids, tags: tags.join(", ") }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-tags-failed");
    }
  }

  async function applyReassignLibrary(ids: string[], fromLibraryId: string, toLibraryId: string) {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/reassign-library"
      : "/api/series/bulk/reassign-library";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, fromLibraryId, toLibraryId }
          : { seriesIds: ids, fromLibraryId, toLibraryId }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-reassign-failed");
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
          ? `${selectedIds.length} title${selectedIds.length === 1 ? "" : "s"} now monitored`
          : `${selectedIds.length} title${selectedIds.length === 1 ? "" : "s"} unmonitored`
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
      `Searching ${selectedItems.length} title${selectedItems.length === 1 ? "" : "s"}…`
    );
    try {
      await applySearchNow(selectedIds);
      toast.success(
        `Manual search dispatched for ${selectedItems.length} title${selectedItems.length === 1 ? "" : "s"}`,
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

        for (const [fromLibraryId, ids] of previousByLibrary) {
          if (fromLibraryId === bulkTargetLibraryId) {
            continue;
          }
          await applyReassignLibrary(ids, fromLibraryId, bulkTargetLibraryId);
        }

        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) ? { ...item, libraryId: bulkTargetLibraryId } : item
          )
        );

        pushHistory({
          label: "Reassign library",
          undoLabel: "Library reassignment reverted",
          redoLabel: "Library reassignment re-applied",
          undo: async () => {
            for (const [oldLibraryId, ids] of previousByLibrary) {
              await applyReassignLibrary(ids, bulkTargetLibraryId, oldLibraryId);
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
              await applyReassignLibrary(ids, fromLibraryId, bulkTargetLibraryId);
            }
            setLibraryItems((current) =>
              current.map((item) => selectedIds.includes(item.id) ? { ...item, libraryId: bulkTargetLibraryId } : item)
            );
          }
        });
        toast.success("Library assignment updated.");
      }
      else if (bulkOperation === "tags") {
        const normalizedTags = normalizeBulkTags(bulkTagsInput);
        const previousTags = new Map(selectedItems.map((item) => [item.id, item.tags ?? []] as const));
        await applyTags(selectedIds, normalizedTags);
        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) ? { ...item, tags: normalizedTags } : item
          )
        );
        pushHistory({
          label: "Apply tags",
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
              await applyTags(ids, tags);
            }
            setLibraryItems((current) =>
              current.map((item) =>
                selectedIds.includes(item.id) ? { ...item, tags: previousTags.get(item.id) ?? [] } : item
              )
            );
          },
          redo: async () => {
            await applyTags(selectedIds, normalizedTags);
            setLibraryItems((current) =>
              current.map((item) => selectedIds.includes(item.id) ? { ...item, tags: normalizedTags } : item)
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
      setSelectedIds([]);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Bulk operation failed.";
      setBulkError(message);
      toast.error("Bulk operation failed", { description: message });
    } finally {
      setIsBulkUpdating(false);
    }
  }

  function toggleSelectedId(id: string) {
    setSelectedIds((current) =>
      current.includes(id) ? current.filter((entry) => entry !== id) : [...current, id]
    );
  }

  function toggleSelectAllVisible() {
    const visibleIds = libraryItems.map((item) => item.id);
    const allVisibleSelected = visibleIds.every((id) => selectedIds.includes(id));
    setSelectedIds(allVisibleSelected ? [] : visibleIds);
  }




  return {
    selectedIds, setSelectedIds, isBulkUpdating, setIsBulkUpdating, isBulkToolsOpen, setIsBulkToolsOpen,
    isRemovalConfirmationOpen, setIsRemovalConfirmationOpen, bulkOperation, setBulkOperation,
    bulkMonitored, setBulkMonitored, bulkQualityProfileId, setBulkQualityProfileId,
    bulkTargetLibraryId, setBulkTargetLibraryId, bulkTagsInput, setBulkTagsInput,
    bulkRenameTemplate, setBulkRenameTemplate, bulkRenamePreview, setBulkRenamePreview,
    bulkConfirming, setBulkConfirming, bulkError, setBulkError, bulkLibraries, bulkQualityProfiles,
    bulkOptionsLoading, undoStack, redoStack, runUndo, runRedo, openBulkTools,
    executeBulkToolsOperation, toggleSelectedId, toggleSelectAllVisible
  };
}
