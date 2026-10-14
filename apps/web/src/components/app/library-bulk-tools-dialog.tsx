import type { BulkRenamePreviewItem, BulkTagMode, BulkWorkflowOperation } from "../../hooks/use-bulk-edit";
import { BulkTagField } from "./bulk-tag-field";
import type { LibraryVariant } from "../../hooks/use-library-create";
import { Button } from "../ui/button";
import { CheckboxRow } from "../ui/checkbox";
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
  minimumAvailability: string;
  targetLibraryId: string;
  moveFiles: boolean;
  tags: string;
  tagsMode: BulkTagMode;
  renameTemplate: string;
  renamePreview: BulkRenamePreviewItem[];
  isApplyingRename: boolean;
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
  onMinimumAvailabilityChange: (value: string) => void;
  onTargetLibraryChange: (value: string) => void;
  onMoveFilesChange: (value: boolean) => void;
  onTagsChange: (value: string) => void;
  onTagsModeChange: (value: BulkTagMode) => void;
  onRenameTemplateChange: (value: string) => void;
  onExecute: () => void;
  onApplyRename: () => void;
};

const MINIMUM_AVAILABILITY_OPTIONS: NamedOption[] = [
  { id: "announced", name: "Announced" },
  { id: "inCinemas", name: "In cinemas" },
  { id: "released", name: "Released" }
];

export function LibraryBulkToolsDialog({
  open, selectedCount, variant, isUpdating, operation, monitored, qualityProfileId,
  minimumAvailability, targetLibraryId, moveFiles, tags, tagsMode, renameTemplate, renamePreview,
  isApplyingRename, confirming, error, libraries,
  qualityProfiles, isOptionsLoading, undoCount, redoCount, onClose, onOperationChange,
  onMonitoredChange, onQualityProfileChange, onMinimumAvailabilityChange, onTargetLibraryChange,
  onMoveFilesChange, onTagsChange, onTagsModeChange,
  onRenameTemplateChange, onExecute, onApplyRename,
}: LibraryBulkToolsDialogProps) {
  if (!open) return null;
  const selectedTitleLabel = `${selectedCount} selected title${selectedCount === 1 ? "" : "s"}`;

  return <div className="fixed inset-0 z-70 flex items-center justify-center bg-black/60 px-4 py-6 backdrop-blur-xs">
    <div className="w-full max-w-2xl space-y-(--page-gap) rounded-2xl border border-hairline bg-card p-5 shadow-2xl">
      <div className="flex items-start justify-between gap-3">
        <div><p className="text-(length:--type-caption) font-semibold uppercase tracking-[0.16em] text-muted-foreground">Bulk workflow</p><h3 className="font-display text-xl font-semibold text-foreground">{selectedTitleLabel}</h3></div>
        <Button type="button" variant="ghost" onClick={onClose} disabled={isUpdating}>Close</Button>
      </div>
      <div className="grid gap-(--grid-gap) md:grid-cols-2">
        <Field label="Operation" help="Choose the bulk action to run.">
          <Select value={operation} onChange={(event) => onOperationChange(event.target.value as BulkWorkflowOperation)}>
          <option value="monitoring">Monitor or unmonitor</option><option value="quality">Set quality profile</option>{variant === "movies" ? <option value="minimumAvailability">Set minimum availability</option> : null}<option value="reassignLibrary">Assign library/root</option><option value="tags">Apply tags</option><option value="search">Search now</option><option value="renamePreview">Rename preview</option>
          </Select>
        </Field>
        {operation === "monitoring" ? <Field label="Monitoring state" help="Apply monitored or unmonitored to the selection."><Select value={monitored ? "true" : "false"} onChange={(event) => onMonitoredChange(event.target.value === "true")}><option value="true">Monitored</option><option value="false">Unmonitored</option></Select></Field> : null}
        {operation === "quality" ? <Field label="Quality profile" help="Set one quality profile for all selected titles."><Select value={qualityProfileId} onChange={(event) => onQualityProfileChange(event.target.value)}><option value="">Choose a quality profile</option>{qualityProfiles.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field> : null}
        {operation === "minimumAvailability" ? <Field label="Minimum availability" help="The point Deluno is allowed to start looking for these titles."><Select value={minimumAvailability} onChange={(event) => onMinimumAvailabilityChange(event.target.value)}>{MINIMUM_AVAILABILITY_OPTIONS.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field> : null}
        {operation === "reassignLibrary" ? <>
          <Field label="Destination library" help="Reassign selected titles to a different library/root."><Select value={targetLibraryId} onChange={(event) => onTargetLibraryChange(event.target.value)}><option value="">Choose library</option>{libraries.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field>
          <Field label="Files" help={moveFiles ? (variant === "movies" ? "Files physically move to the new library's root." : "Each show's whole folder moves to the new library's root.") : "Only the record moves; files stay under the old root."}>
            <CheckboxRow label="Move the files to the new root" checked={moveFiles} onCheckedChange={onMoveFilesChange} />
          </Field>
        </> : null}
        {operation === "tags" ? <BulkTagField tags={tags} mode={tagsMode} onTagsChange={onTagsChange} onModeChange={onTagsModeChange} /> : null}
        {operation === "renamePreview" ? <Field label="Template (optional)" help="Preview generated folder names before rename workflows."><Input value={renameTemplate} onChange={(event) => onRenameTemplateChange(event.target.value)} placeholder={variant === "movies" ? "{Movie Title} ({Release Year})" : "{Series Title} ({Series Year})"} /></Field> : null}
      </div>
      {confirming && operation !== "renamePreview" ? <div className="rounded-xl border border-warning/40 bg-warning/10 px-4 py-3 text-sm text-warning-foreground">Confirming will run this operation across {selectedTitleLabel}.</div> : null}
      {error ? <div className="rounded-xl border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">{error}</div> : null}
      {operation === "renamePreview" && renamePreview.length > 0 ? <>
        <div className="max-h-72 overflow-auto rounded-xl border border-hairline bg-surface-1"><table className="min-w-full text-sm"><thead className="sticky top-0 bg-surface-2 text-left"><tr><th scope="col" className="px-3 py-2">Title</th><th scope="col" className="px-3 py-2">Proposed name</th></tr></thead><tbody>{renamePreview.map((item) => <tr key={item.itemId} className="border-t border-hairline/70"><td className="px-3 py-2 text-foreground">{item.title}</td><td className="px-3 py-2 font-mono text-xs text-muted-foreground">{item.proposedName}</td></tr>)}</tbody></table></div>
        <div className="rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-sm text-muted-foreground">
          This only shows what would happen. Nothing on disk changes until you apply it.
        </div>
      </> : null}
      <div className="flex items-center justify-between gap-3">
        <div className="text-xs text-muted-foreground">{isOptionsLoading ? "Loading options..." : `Undo stack: ${undoCount} · Redo stack: ${redoCount}`}</div>
        <div className="flex items-center gap-2">
          <Button type="button" variant="outline" onClick={onClose} disabled={isUpdating || isApplyingRename}>Cancel</Button>
          <Button type="button" onClick={onExecute} disabled={isUpdating || isOptionsLoading || isApplyingRename}>{isUpdating ? "Running..." : operation === "renamePreview" ? "Run preview" : confirming ? "Confirm and run" : "Review and continue"}</Button>
          {operation === "renamePreview" && renamePreview.length > 0 ? (
            <Button type="button" onClick={onApplyRename} disabled={isApplyingRename}>
              {isApplyingRename ? "Applying..." : `Apply to ${renamePreview.length} title${renamePreview.length === 1 ? "" : "s"}`}
            </Button>
          ) : null}
        </div>
      </div>
    </div>
  </div>;
}
