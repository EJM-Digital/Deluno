import { Plus, RefreshCw, Zap } from "lucide-react";
import { librarySummaryTone } from "../../lib/media-status-presentation";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";

type LibrarySummaryHeaderProps = {
  label: string;
  singular: string;
  /** True only while the very first page is loading — zeros are not yet facts, so the stat line shows placeholders. */
  isLoading?: boolean;
  totalCount: number;
  downloadedCount: number;
  monitoredCount: number;
  missingCount: number;
  downloadingCount: number;
  onToggleCreate: () => void;
  isUpdatingMetadata: boolean;
  onUpdateMetadata: () => void;
};

export function LibrarySummaryHeader({
  label,
  singular,
  isLoading = false,
  totalCount,
  downloadedCount,
  monitoredCount,
  missingCount,
  downloadingCount,
  onToggleCreate,
  isUpdatingMetadata,
  onUpdateMetadata,
}: LibrarySummaryHeaderProps) {
  return (
    <div className="relative overflow-hidden rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.06]">
      <span
        aria-hidden
        className="pointer-events-none absolute inset-x-5 top-0 h-px rounded-full"
        style={{ background: "linear-gradient(90deg, transparent, hsl(var(--primary)/0.45), hsl(var(--primary-2)/0.28), transparent)" }}
      />
      <span aria-hidden className="pointer-events-none absolute -right-20 -top-28 h-64 w-64 rounded-full bg-primary/10 blur-3xl" />
      <div className="relative flex flex-col gap-[var(--grid-gap)] lg:flex-row lg:items-center lg:justify-between">
        <div className="min-w-0">
          <h2 className="font-display text-[length:var(--type-title-md)] font-semibold tracking-tight text-foreground">
            Browse and manage your {label}
          </h2>
          <p className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-[length:var(--type-body-sm)] text-muted-foreground">
            {isLoading ? (
              <span>Loading your {label}…</span>
            ) : (
            <>
            <span><span className="tabular font-semibold text-foreground">{totalCount.toLocaleString()}</span> total</span>
            <span className="text-muted-foreground/45">·</span>
            <span><span className={cn("tabular font-semibold", librarySummaryTone("availability", downloadedCount))}>{downloadedCount}</span> downloaded</span>
            <span className="text-muted-foreground/45">·</span>
            <span><span className="tabular font-semibold text-muted-foreground">{monitoredCount}</span> monitored</span>
            {missingCount > 0 ? <><span className="text-muted-foreground/45">·</span><span><span className="tabular font-semibold text-warning">{missingCount}</span> missing</span></> : null}
            {downloadingCount > 0 ? <><span className="text-muted-foreground/45">·</span><span><span className="tabular font-semibold text-info">{downloadingCount}</span> downloading</span></> : null}
            </>
            )}
          </p>
        </div>
        <div className="flex shrink-0 flex-wrap items-center gap-2">
          <Button className="gap-2" onClick={onToggleCreate}>
            <Plus className="h-4 w-4" strokeWidth={2.5} />
            Add {singular}
          </Button>
          <Button
            type="button"
            variant="outline"
            className="gap-2"
            onClick={onUpdateMetadata}
            disabled={isUpdatingMetadata}
            title={`Queue a metadata refresh for every ${singular.toLowerCase()} in Deluno`}
          >
            <RefreshCw className={cn("h-4 w-4", isUpdatingMetadata && "animate-spin")} />
            Update all metadata
          </Button>
          {missingCount > 0 ? <Button variant="secondary" className="gap-2"><Zap className="h-4 w-4" />Hunt {missingCount} missing</Button> : null}
        </div>
      </div>
    </div>
  );
}
