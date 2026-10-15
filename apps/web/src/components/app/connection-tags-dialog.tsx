import * as Dialog from "../ui/dialog-primitives";
import { ListChecks, Tags } from "lucide-react";
import { useEffect, useState } from "react";
import type { BulkTagMode } from "../../hooks/use-bulk-edit";
import { toast } from "../shell/toaster";
import { applyConnectionTags, type ConnectionTagKind } from "../../lib/connection-tags";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";
import { BulkTagField, bulkTagActionLabel } from "./bulk-tag-field";
import { parseTagList } from "./tag-picker";

/** "Select" switch for the header of a connection list. */
export function ConnectionSelectButton({ active, onClick }: { active: boolean; onClick: () => void }) {
  return (
    <Button type="button" variant={active ? "secondary" : "ghost"} size="sm" onClick={onClick}>
      <ListChecks className="h-3.5 w-3.5" />
      {active ? "Done" : "Select"}
    </Button>
  );
}

/** Header checkbox that ticks or clears every visible row. */
export function ConnectionSelectAll({ ids, selected, noun, onChange }: { ids: string[]; selected: Set<string>; noun: string; onChange: (ids: string[]) => void }) {
  const all = ids.length > 0 && ids.every((id) => selected.has(id));
  const some = !all && ids.some((id) => selected.has(id));
  return (
    <Checkbox
      aria-label={all ? `Deselect all ${noun}` : `Select all ${noun}`}
      checked={all}
      indeterminate={some}
      onCheckedChange={(checked) => onChange(checked ? ids : [])}
    />
  );
}

/** Toolbar over a connection list once rows are ticked. */
export function ConnectionSelectionBar({ count, noun, onTags, onClear }: { count: number; noun: string; onTags: () => void; onClear: () => void }) {
  if (count === 0) return null;
  return (
    <div role="toolbar" aria-label={`Bulk ${noun} actions`} className="flex flex-wrap items-center gap-2 border-b border-hairline bg-surface-2/40 px-(--card-pad-x) py-2">
      <span className="text-(length:--type-caption) font-medium text-foreground">{count} selected</span>
      <Button type="button" variant="outline" size="sm" aria-label={`Change tags on the selected ${noun}`} onClick={onTags}>
        <Tags className="h-3.5 w-3.5" />
        Tags
      </Button>
      <span className="flex-1" />
      <Button type="button" variant="ghost" size="sm" onClick={onClear}>Clear selection</Button>
    </div>
  );
}

/**
 * Add, remove or replace tags on the ticked indexers, download clients or
 * notification connections in one action (#1044), with the same three modes
 * and wording as the title bulk editor.
 */
export function ConnectionTagsDialog({
  open,
  kind,
  noun,
  ids,
  onOpenChange,
  onApplied
}: {
  open: boolean;
  kind: ConnectionTagKind;
  /** Plural, lower-case, as said on screen: "indexers". */
  noun: string;
  ids: string[];
  onOpenChange: (open: boolean) => void;
  onApplied: () => void;
}) {
  const [tags, setTags] = useState("");
  const [mode, setMode] = useState<BulkTagMode>("add");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setTags("");
      setMode("add");
      setError(null);
    }
  }, [open]);

  const list = parseTagList(tags);
  const canApply = !busy && (mode === "replace" || list.length > 0);
  const singular = noun.endsWith("s") ? noun.slice(0, -1) : noun;
  const heading = `${ids.length} selected ${ids.length === 1 ? singular : noun}`;

  async function apply() {
    setBusy(true);
    setError(null);
    try {
      const result = await applyConnectionTags(kind, ids, list, mode);
      toast.success(result.updated === 0 ? "Nothing needed changing." : `Tags changed on ${result.updated} ${result.updated === 1 ? singular : noun}.`);
      onOpenChange(false);
      onApplied();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Could not change the tags.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={(next) => { if (!busy) onOpenChange(next); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2 rounded-2xl border border-hairline bg-card shadow-2xl data-[state=open]:animate-fade-in"
          aria-describedby="connection-tags-description"
        >
          <div className="space-y-(--page-gap) px-6 py-5">
            <div>
              <Dialog.Title className="font-semibold text-foreground">Change tags: {heading}</Dialog.Title>
              <Dialog.Description id="connection-tags-description" className="mt-1 text-sm text-muted-foreground">
                One change for every selected {singular}.
              </Dialog.Description>
            </div>
            <BulkTagField tags={tags} mode={mode} onTagsChange={setTags} onModeChange={setMode} />
            {error ? <div role="alert" className="rounded-xl border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">{error}</div> : null}
          </div>
          <div className="flex items-center justify-end gap-2 border-t border-hairline px-6 py-4">
            <Button type="button" variant="outline" disabled={busy} onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="button" disabled={!canApply} onClick={() => void apply()}>{busy ? "Applying..." : bulkTagActionLabel(mode)}</Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
