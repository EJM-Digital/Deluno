import React, { useCallback, useEffect, useState } from "react";
import { LibraryView } from "./library-view";
import { BulkOperationsPanel, type BulkOperationResponse } from "../BulkOperationsPanel";
import type { MediaItem } from "../../lib/media-types";
import type { MetadataProviderStatus } from "../../lib/api";
import { toast } from "../shell/toaster";

interface LibraryViewWithBulkOpsProps {
  variant: "movies" | "shows";
  items: MediaItem[];
  metadataStatus: MetadataProviderStatus | null;
  isRouteLoading: boolean;
  onReload: () => void;
}

/**
 * Enhanced LibraryView with bulk operations support
 * Manages selection state and provides bulk operations panel
 *
 * NOTE: For full integration with LibraryView's internal rendering,
 * LibraryView would need to support selection checkboxes and callbacks.
 * Current implementation provides:
 * - Bulk operations panel and result handling
 * - Selection management via toolbar
 * - Toast notifications for results
 * - E2E test support for bulk operations API
 */
export function LibraryViewWithBulkOps({
  variant,
  items,
  metadataStatus,
  isRouteLoading,
  onReload,
}: LibraryViewWithBulkOpsProps) {
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [isShowingBulkOps, setIsShowingBulkOps] = useState(false);

  // Handle keyboard shortcut: Ctrl+A to select all
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === "a" && isShowingBulkOps === false) {
        const focusedElement = document.activeElement;
        // Only select all if focus is not on an input
        if (
          focusedElement &&
          !["INPUT", "TEXTAREA"].includes((focusedElement as HTMLElement).tagName)
        ) {
          e.preventDefault();
          handleSelectAll();
        }
      }

      // Esc to clear selection
      if (e.key === "Escape" && selectedIds.length > 0) {
        setSelectedIds([]);
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [selectedIds.length, isShowingBulkOps]);

  const handleSelectItem = useCallback((itemId: string) => {
    setSelectedIds((prev) => {
      if (prev.includes(itemId)) {
        return prev.filter((id) => id !== itemId);
      } else {
        return [...prev, itemId];
      }
    });
  }, []);

  const handleSelectAll = useCallback(() => {
    if (selectedIds.length === items.length && items.length > 0) {
      setSelectedIds([]);
    } else {
      setSelectedIds(items.map((item) => item.id));
    }
  }, [items, selectedIds.length]);

  const handleBulkOperationStart = useCallback(() => {
    // Called when bulk operation execution begins
    // Can be used to disable other interactions if needed
  }, []);

  const handleBulkOperationComplete = useCallback(
    (response: BulkOperationResponse) => {
      // Show result summary
      const message = `${response.successCount}/${response.totalProcessed} succeeded`;
      if (response.failureCount > 0) {
        toast.error("Bulk Operation Completed with Errors", {
          description: message,
        });
      } else {
        toast.success("Bulk Operation Completed", {
          description: message,
        });
      }

      // Close the bulk operations panel
      setIsShowingBulkOps(false);

      // Clear selection
      setSelectedIds([]);

      // Reload the library to show updated items
      onReload();
    },
    [onReload]
  );

  const handleCloseBulkOps = useCallback(() => {
    setIsShowingBulkOps(false);
  }, []);

  const handleOpenBulkOps = useCallback(() => {
    if (selectedIds.length === 0) {
      toast.error("No items selected", {
        description: "Please select at least one item to perform bulk operations",
      });
      return;
    }
    setIsShowingBulkOps(true);
  }, [selectedIds.length]);

  return (
    <div className="relative">
      <LibraryView
        variant={variant}
        items={items}
        metadataStatus={metadataStatus}
        isRouteLoading={isRouteLoading}
        onReload={onReload}
      />

      {isShowingBulkOps && (
        <BulkOperationsPanel
          selectedIds={selectedIds}
          mediaType={variant === "movies" ? "movie" : "series"}
          onOperationStart={handleBulkOperationStart}
          onOperationComplete={handleBulkOperationComplete}
          onClose={handleCloseBulkOps}
        />
      )}

      {/* Selection toolbar - shows when items are selected */}
      {selectedIds.length > 0 && !isShowingBulkOps && (
        <div className="fixed bottom-6 left-1/2 -translate-x-1/2 z-50 flex items-center gap-4 px-6 py-3 bg-card border border-hairline rounded-lg shadow-lg">
          <span className="text-sm font-medium text-foreground">
            {selectedIds.length} item{selectedIds.length !== 1 ? "s" : ""} selected
          </span>
          <button
            onClick={handleOpenBulkOps}
            className="px-4 py-2 bg-primary text-primary-foreground text-sm font-medium rounded hover:bg-primary/90 transition-colors"
          >
            Bulk Operations
          </button>
          <button
            onClick={() => setSelectedIds([])}
            className="px-4 py-2 bg-secondary text-secondary-foreground text-sm font-medium rounded hover:bg-secondary/80 transition-colors"
          >
            Clear
          </button>
        </div>
      )}
    </div>
  );
}
