import type { BulkRenamePreviewItem, BulkWorkflowOperation } from "../../hooks/use-bulk-edit";
import type { LibraryVariant } from "../../hooks/use-library-create";
import { Button } from "../ui/button";
import { Field } from "../ui/field";
import { Input } from "../ui/input";
import { Select } from "../ui/select";

type NamedOption = { id: string; name: string };

type LibraryBulkToolsDialogProps = {
  open: boolean;
  selectedCount: number;
  variant: LibraryVariant;
  isUpdating: boolean;
  operation: BulkWorkflowOperation;
  monitored: boolean;
  qualityProfileId: string;
  targetLibraryId: string;
  tags: string;
  renameTemplate: string;
  renamePreview: BulkRenamePreviewItem[];
  confirming: boolean;
  error: string | null;
  libraries: NamedOption[];
  qualityProfiles: NamedOption[];
  isOptionsLoading: boolean;
  undoCount: number;
  redoCount: number;
  onClose: () => void;
  onOperationChange: (operation: BulkWorkflowOperation) => void;
  onMonitoredChange: (value: boolean) => void;
  onQualityProfileChange: (value: string) => void;
  onTargetLibraryChange: (value: string) => void;
  onTagsChange: (value: string) => void;
  onRenameTemplateChange: (value: string) => void;
  onExecute: () => void;
};

export function LibraryBulkToolsDialog({
  open, selectedCount, variant, isUpdating, operation, monitored, qualityProfileId,
  targetLibraryId, tags, renameTemplate, renamePreview, confirming, error, libraries,
  qualityProfiles, isOptionsLoading, undoCount, redoCount, onClose, onOperationChange,
  onMonitoredChange, onQualityProfileChange, onTargetLibraryChange, onTagsChange,
  onRenameTemplateChange, onExecute,
}: LibraryBulkToolsDialogProps) {
  if (!open) return null;
  const selectedTitleLabel = `${selectedCount} selected title${selectedCount === 1 ? "" : "s"}`;

  return <div className="fixed inset-0 z-[70] flex items-center justify-center bg-black/60 px-4 py-6 backdrop-blur-sm">
    <div className="w-full max-w-2xl space-y-[var(--page-gap)] rounded-2xl border border-hairline bg-card p-5 shadow-2xl">
      <div className="flex items-start justify-between gap-3">
        <div><p className="text-[length:var(--type-caption)] font-semibold uppercase tracking-[0.16em] text-muted-foreground">Bulk workflow</p><h3 className="font-display text-xl font-semibold text-foreground">{selectedTitleLabel}</h3></div>
        <Button type="button" variant="ghost" onClick={onClose} disabled={isUpdating}>Close</Button>
      </div>
      <div className="grid gap-[var(--grid-gap)] md:grid-cols-2">
        <Field label="Operation" help="Choose the bulk action to run.">
          <Select value={operation} onChange={(event) => onOperationChange(event.target.value as BulkWorkflowOperation)}>
          <option value="monitoring">Monitor or unmonitor</option><option value="quality">Set quality profile</option><option value="reassignLibrary">Assign library/root</option><option value="tags">Apply tags</option><option value="search">Search now</option><option value="renamePreview">Rename preview</option>
          </Select>
        </Field>
        {operation === "monitoring" ? <Field label="Monitoring state" help="Apply monitored or unmonitored to the selection."><Select value={monitored ? "true" : "false"} onChange={(event) => onMonitoredChange(event.target.value === "true")}><option value="true">Monitored</option><option value="false">Unmonitored</option></Select></Field> : null}
        {operation === "quality" ? <Field label="Quality profile" help="Set one quality profile for all selected titles."><Select value={qualityProfileId} onChange={(event) => onQualityProfileChange(event.target.value)}><option value="">Choose a quality profile</option>{qualityProfiles.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field> : null}
        {operation === "reassignLibrary" ? <Field label="Destination library" help="Reassign selected titles to a different library/root."><Select value={targetLibraryId} onChange={(event) => onTargetLibraryChange(event.target.value)}><option value="">Choose library</option>{libraries.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field> : null}
        {operation === "tags" ? <Field label="Tags" help="Comma-separated tags to apply to all selected titles."><Input value={tags} onChange={(event) => onTagsChange(event.target.value)} placeholder="e.g. favourites, weekend, 4k" /></Field> : null}
        {operation === "renamePreview" ? <Field label="Template (optional)" help="Preview generated folder names before rename workflows."><Input value={renameTemplate} onChange={(event) => onRenameTemplateChange(event.target.value)} placeholder={variant === "movies" ? "{Movie Title} ({Release Year})" : "{Series Title} ({Series Year})"} /></Field> : null}
      </div>
      {confirming && operation !== "renamePreview" ? <div className="rounded-xl border border-warning/40 bg-warning/10 px-4 py-3 text-sm text-warning-foreground">Confirming will run this operation across {selectedTitleLabel}.</div> : null}
      {error ? <div className="rounded-xl border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">{error}</div> : null}
      {operation === "renamePreview" && renamePreview.length > 0 ? <div className="max-h-72 overflow-auto rounded-xl border border-hairline bg-surface-1"><table className="min-w-full text-sm"><thead className="sticky top-0 bg-surface-2 text-left"><tr><th scope="col" className="px-3 py-2">Title</th><th scope="col" className="px-3 py-2">Proposed name</th></tr></thead><tbody>{renamePreview.map((item) => <tr key={item.itemId} className="border-t border-hairline/70"><td className="px-3 py-2 text-foreground">{item.title}</td><td className="px-3 py-2 font-mono text-xs text-muted-foreground">{item.proposedName}</td></tr>)}</tbody></table></div> : null}
      <div className="flex items-center justify-between gap-3"><div className="text-xs text-muted-foreground">{isOptionsLoading ? "Loading options..." : `Undo stack: ${undoCount} · Redo stack: ${redoCount}`}</div><div className="flex items-center gap-2"><Button type="button" variant="outline" onClick={onClose} disabled={isUpdating}>Cancel</Button><Button type="button" onClick={onExecute} disabled={isUpdating || isOptionsLoading}>{isUpdating ? "Running..." : operation === "renamePreview" ? "Run preview" : confirming ? "Confirm and run" : "Review and continue"}</Button></div></div>
    </div>
  </div>;
}
