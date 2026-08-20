import { CircleOff, Eye, FolderTree, Redo2, Trash2, Undo2, Zap } from "lucide-react";
import type { BulkWorkflowOperation } from "../../hooks/use-bulk-edit";
import { cn } from "../../lib/utils";
import { BulkAction } from "./library-bulk-action";

type LibrarySelectionCommandBarProps = {
  count: number;
  isUpdating: boolean;
  canUndo: boolean;
  canRedo: boolean;
  onUndo: () => void;
  onRedo: () => void;
  onOpenBulkTools: (operation: BulkWorkflowOperation, monitoring?: boolean) => void;
  onRemove: () => void;
  onClear: () => void;
};

export function LibrarySelectionCommandBar({
  count,
  isUpdating,
  canUndo,
  canRedo,
  onUndo,
  onRedo,
  onOpenBulkTools,
  onRemove,
  onClear,
}: LibrarySelectionCommandBarProps) {
  if (count === 0) return null;

  return (
    <div className={cn(
      "fixed z-50 mx-auto",
      "bottom-[calc(var(--mobile-tabbar-height)+16px)] md:bottom-8",
      "left-1/2 -translate-x-1/2",
      "animate-fade-in"
    )}>
      <div className={cn(
        "flex items-center overflow-hidden rounded-2xl",
        "border border-white/[0.1] dark:border-white/[0.08]",
        "bg-[hsl(226_24%_10%/0.97)] dark:bg-[hsl(226_24%_8%/0.98)]",
        "shadow-[0_24px_60px_hsl(0_0%_0%/0.45),0_8px_20px_hsl(0_0%_0%/0.3),inset_0_1px_0_hsl(0_0%_100%/0.06)]",
        "backdrop-blur-2xl"
      )}>
        <div className="flex items-center gap-2.5 border-r border-white/[0.07] px-4 py-3">
          <span className={cn(
            "flex h-6 min-w-6 items-center justify-center rounded-full px-2",
            "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))]",
            "text-[length:var(--library-badge-size)] font-bold text-primary-foreground",
            "shadow-[0_2px_8px_hsl(var(--primary-deep)/0.5),inset_0_1px_0_hsl(0_0%_100%/0.2)]"
          )}>
            {count}
          </span>
          <span className="whitespace-nowrap text-[length:var(--library-toolbar-size)] font-medium text-[hsl(var(--media-muted-foreground))]">
            {count === 1 ? "item" : "items"} selected
          </span>
        </div>

        <div className="flex items-center gap-0.5 px-1.5 py-1.5">
          <BulkAction label="Undo" icon={<Undo2 className="h-3.5 w-3.5" />} onClick={onUndo} disabled={isUpdating || !canUndo} />
          <BulkAction label="Redo" icon={<Redo2 className="h-3.5 w-3.5" />} onClick={onRedo} disabled={isUpdating || !canRedo} />
          <BulkAction label="Monitor" icon={<Eye className="h-3.5 w-3.5" />} onClick={() => onOpenBulkTools("monitoring", true)} disabled={isUpdating} loading={isUpdating} variant="primary" />
          <BulkAction label="Search now" icon={<Zap className="h-3.5 w-3.5" />} onClick={() => onOpenBulkTools("search")} disabled={isUpdating} />
          <BulkAction label="Unmonitor" icon={<CircleOff className="h-3.5 w-3.5" />} onClick={() => onOpenBulkTools("monitoring", false)} disabled={isUpdating} />
          <BulkAction label="Remove" icon={<Trash2 className="h-3.5 w-3.5" />} onClick={onRemove} disabled={isUpdating} variant="danger" />
          <BulkAction label="Bulk tools" icon={<FolderTree className="h-3.5 w-3.5" />} onClick={() => onOpenBulkTools("quality")} disabled={isUpdating} />
        </div>

        <div className="border-l border-white/[0.07] px-1.5 py-1.5">
          <button
            type="button"
            onClick={onClear}
            className="flex min-h-[var(--library-toolbar-height)] items-center gap-1.5 rounded-xl px-3 text-[length:var(--library-toolbar-size)] font-medium text-[hsl(var(--media-muted-foreground)/0.65)] transition hover:bg-white/[0.06] hover:text-[hsl(var(--media-foreground))]"
            aria-label="Clear selection"
          >
            Clear
            <kbd className="rounded border border-white/10 bg-white/[0.05] px-1 font-mono text-[length:var(--library-badge-size)] text-[hsl(var(--media-muted-foreground)/0.5)]">Esc</kbd>
          </button>
        </div>
      </div>
    </div>
  );
}
