import * as Dialog from "@radix-ui/react-dialog";
import { AlertTriangle, FolderX, ListMinus } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";

export interface RemoveMediaOptions {
  deleteFiles: boolean;
  addImportListExclusion: boolean;
}

export interface MediaRemovalPreview {
  filePaths: string[];
  folderPaths: string[];
  warnings: string[];
}

interface RemoveMediaDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  mediaLabel: "movie" | "TV show";
  removalPreview: MediaRemovalPreview;
  importListCount: number;
  busy?: boolean;
  onConfirm: (options: RemoveMediaOptions) => void;
}

export function RemoveMediaDialog({
  open,
  onOpenChange,
  title,
  mediaLabel,
  removalPreview,
  importListCount,
  busy = false,
  onConfirm
}: RemoveMediaDialogProps) {
  const hasImportedFiles = removalPreview.filePaths.length > 0;
  const [deleteFiles, setDeleteFiles] = useState(false);
  const [addImportListExclusion, setAddImportListExclusion] = useState(importListCount > 0);

  useEffect(() => {
    if (!open) return;
    setDeleteFiles(false);
    setAddImportListExclusion(importListCount > 0);
  }, [open, importListCount]);

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl data-[state=open]:animate-fade-in"
          aria-describedby="remove-media-description"
        >
          <div className="border-b border-hairline px-6 py-5">
            <div className="flex items-start gap-3">
              <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-destructive/10">
                <AlertTriangle className="h-4 w-4 text-destructive" />
              </span>
              <div>
                <Dialog.Title className="font-semibold text-foreground">Remove {title}?</Dialog.Title>
                <Dialog.Description id="remove-media-description" className="mt-1 text-sm leading-relaxed text-muted-foreground">
                  Choose what should happen before Deluno stops managing this {mediaLabel}. Your download client is never changed here.
                </Dialog.Description>
              </div>
            </div>
          </div>

          <div className="space-y-3 p-6">
            <OptionRow
              checked={addImportListExclusion}
              disabled={importListCount === 0}
              icon={<ListMinus className="h-4 w-4" />}
              label="Prevent automatic re-add"
              description={
                importListCount > 0
                  ? `Keep this ${mediaLabel} out of the ${importListCount === 1 ? "import list" : `${importListCount} import lists`} that added it. You can restore it later from Import Lists.`
                  : `This ${mediaLabel} was not added by an import list, so there is nothing to exclude.`
              }
              onCheckedChange={setAddImportListExclusion}
            />
            <OptionRow
              checked={deleteFiles}
              disabled={!hasImportedFiles}
              icon={<FolderX className="h-4 w-4" />}
              label="Delete imported files from disk"
              description={
                hasImportedFiles
                  ? `Delete the imported ${mediaLabel} files and its title folder from the configured library. This cannot be undone.`
                  : `Deluno has no imported files recorded for this ${mediaLabel}.`
              }
              onCheckedChange={setDeleteFiles}
            />
            {hasImportedFiles ? (
              <div className="rounded-lg border border-hairline bg-surface-0 px-4 py-3 text-xs text-muted-foreground">
                <p className="font-medium text-foreground">Affected library location{removalPreview.folderPaths.length === 1 ? "" : "s"}</p>
                <div className="mt-1 space-y-1 font-mono text-[length:var(--type-caption)] leading-relaxed">
                  {(removalPreview.folderPaths.length ? removalPreview.folderPaths : removalPreview.filePaths).slice(0, 3).map((path) => (
                    <p key={path} className="break-all">{path}</p>
                  ))}
                  {(removalPreview.folderPaths.length || removalPreview.filePaths.length) > 3 ? <p>and more tracked files</p> : null}
                </div>
              </div>
            ) : null}
            {removalPreview.warnings.length ? (
              <p className="text-xs leading-relaxed text-warning">{removalPreview.warnings.join(" ")}</p>
            ) : null}
          </div>

          <div className="flex items-center justify-between border-t border-hairline bg-surface-1 px-6 py-4">
            <p className="text-xs text-muted-foreground">The Deluno catalog record will be removed.</p>
            <div className="flex gap-2">
              <Dialog.Close asChild>
                <Button variant="secondary" size="sm" disabled={busy}>Cancel</Button>
              </Dialog.Close>
              <Button
                size="sm"
                disabled={busy}
                onClick={() => onConfirm({ deleteFiles, addImportListExclusion: importListCount > 0 && addImportListExclusion })}
                className="bg-destructive text-destructive-foreground shadow-none hover:brightness-110"
              >
                {busy ? "Removing…" : `Remove ${mediaLabel}`}
              </Button>
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function OptionRow({
  checked,
  disabled,
  icon,
  label,
  description,
  onCheckedChange
}: {
  checked: boolean;
  disabled: boolean;
  icon: React.ReactNode;
  label: string;
  description: string;
  onCheckedChange: (checked: boolean) => void;
}) {
  return (
    <label className={`flex cursor-pointer items-start gap-3 rounded-xl border border-hairline p-4 ${disabled ? "cursor-not-allowed opacity-50" : "bg-surface-1 hover:border-warning/35"}`}>
      <Checkbox
        checked={checked}
        disabled={disabled}
        onCheckedChange={onCheckedChange}
        className="mt-0.5"
      />
      <span className="mt-0.5 text-warning">{icon}</span>
      <span>
        <span className="block text-sm font-medium text-foreground">{label}</span>
        <span className="mt-1 block text-xs leading-relaxed text-muted-foreground">{description}</span>
      </span>
    </label>
  );
}
