import * as Dialog from "@radix-ui/react-dialog";
import { AlertTriangle, ListMinus } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "../ui/button";
import { CheckboxRow } from "../ui/checkbox";

export interface BulkRemoveOptions {
  addImportListExclusion: boolean;
}

interface BulkRemoveDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  count: number;
  mediaLabel: string;
  busy?: boolean;
  onConfirm: (options: BulkRemoveOptions) => void;
}

/**
 * The shelf removal action deliberately does not offer file deletion: bulk
 * removal has no per-title preview and should never turn a selection mistake
 * into a disk operation. It does, however, need the same "never add again"
 * decision as the detail page (#315).
 */
export function BulkRemoveDialog({
  open,
  onOpenChange,
  count,
  mediaLabel,
  busy = false,
  onConfirm
}: BulkRemoveDialogProps) {
  const [addImportListExclusion, setAddImportListExclusion] = useState(true);

  useEffect(() => {
    if (open) setAddImportListExclusion(true);
  }, [open]);

  const noun = `${mediaLabel}${count === 1 ? "" : "s"}`;

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl data-[state=open]:animate-fade-in"
          aria-describedby="bulk-remove-description"
        >
          <div className="border-b border-hairline px-6 py-5">
            <div className="flex items-start gap-3">
              <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-destructive/10">
                <AlertTriangle className="h-4 w-4 text-destructive" />
              </span>
              <div>
                <Dialog.Title className="font-semibold text-foreground">
                  Remove {count} {noun}?
                </Dialog.Title>
                <Dialog.Description id="bulk-remove-description" className="mt-1 text-sm leading-relaxed text-muted-foreground">
                  This removes the selected catalogue records and stops Deluno managing them. Imported files and download clients are left alone.
                </Dialog.Description>
              </div>
            </div>
          </div>

          <div className="space-y-3 p-6">
            <div className="rounded-xl border border-hairline bg-surface-1 p-4">
              <div className="flex items-start gap-3">
                <span className="mt-0.5 text-warning"><ListMinus className="h-4 w-4" /></span>
                <CheckboxRow
                  className="min-h-0 flex-1"
                  checked={addImportListExclusion}
                  onCheckedChange={setAddImportListExclusion}
                  label="Prevent automatic re-add"
                  description="For any selected title that was added by an import list, record an exclusion before removal. You can restore it later from Import Lists."
                />
              </div>
            </div>
            <p className="text-xs leading-relaxed text-muted-foreground">
              The exclusion is applied only where Deluno can identify the import list that added the title.
            </p>
          </div>

          <div className="flex items-center justify-between border-t border-hairline bg-surface-1 px-6 py-4">
            <p className="text-xs text-muted-foreground">No files will be deleted by this bulk action.</p>
            <div className="flex gap-2">
              <Dialog.Close asChild>
                <Button variant="secondary" size="sm" disabled={busy}>Cancel</Button>
              </Dialog.Close>
              <Button
                size="sm"
                disabled={busy}
                onClick={() => onConfirm({ addImportListExclusion })}
                className="bg-destructive text-destructive-foreground shadow-none hover:brightness-110"
              >
                {busy ? "Removing…" : `Remove ${noun}`}
              </Button>
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
