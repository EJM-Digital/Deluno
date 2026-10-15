/**
 * The title page's two folder buttons (#997): Rescan folder and Preview rename.
 *
 * Kept apart from "Refresh metadata" on purpose (James, 28 Sep): refreshing
 * details, looking at the folder, and previewing a rename are three different
 * things, so they are three separately labelled buttons and never one combined
 * "refresh and scan".
 *
 * Rescan folder never deletes, moves or imports a file. It reports what it
 * found, and marks a file that has vanished from disk as missing, which is what
 * the library-wide file check already does. Preview rename only reads; renaming
 * is a second, explicit click that shows exactly which folder becomes what.
 *
 * Contracts: POST /api/filesystem/rescan-title,
 * GET /api/{movies|series}/{id}/rename-preview,
 * POST /api/{movies|series}/bulk/rename-apply.
 */
import * as Dialog from "../ui/dialog-primitives";
import { FolderSearch, LoaderCircle, PencilLine } from "lucide-react";
import { useState } from "react";
import { authedFetch } from "../../lib/use-auth";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { toast } from "../shell/toaster";

export interface TitleRescanResult {
  outcome: "ok" | "changed" | "not-changed" | "nothing-tracked" | "library-unreachable" | "library-unknown";
  title: string;
  filesChecked: number;
  filesPresent: number;
  missing: Array<{ fileName: string; path: string; markedMissing: boolean }>;
  untracked: Array<{ fileName: string; path: string; sizeBytes: number | null; issueId: string }>;
  summary: string;
}

export interface TitleRenamePreview {
  outcome: "no-files" | "already-named" | "name-taken" | "will-rename" | "flat-layout" | "library-folder" | "outside-library" | "spread-out" | "shared-folder";
  currentFolder: string | null;
  proposedFolder: string;
  willChange: boolean;
  message: string;
}

interface TitleFolderActionsProps {
  variant: "movies" | "series";
  titleId: string;
  titleName: string;
  /** Called after something on disk or in the catalogue changed, so the page can refresh. */
  onChanged?: () => void;
  disabled?: boolean;
}

type Panel =
  | { kind: "rescan"; result: TitleRescanResult }
  | { kind: "rename"; preview: TitleRenamePreview }
  | null;

export function TitleFolderActions({ variant, titleId, titleName, onChanged, disabled }: TitleFolderActionsProps) {
  const [busy, setBusy] = useState<"rescan" | "rename-preview" | "rename-apply" | null>(null);
  const [panel, setPanel] = useState<Panel>(null);
  const [reviewing, setReviewing] = useState<string | null>(null);

  const [confirmRescan, setConfirmRescan] = useState(false);

  async function rescan() {
    setConfirmRescan(false);
    setBusy("rescan");
    try {
      const response = await authedFetch("/api/filesystem/rescan-title", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ mediaType: variant === "movies" ? "movies" : "tv", titleId })
      });
      if (!response.ok) throw new Error("rescan-failed");
      const result = (await response.json()) as TitleRescanResult;
      setPanel({ kind: "rescan", result });
      if (result.outcome === "changed") onChanged?.();
    } catch {
      toast.error("The folder could not be rescanned.");
    } finally {
      setBusy(null);
    }
  }

  async function previewRename() {
    setBusy("rename-preview");
    try {
      const response = await authedFetch(`/api/${variant}/${titleId}/rename-preview`);
      if (!response.ok) throw new Error("rename-preview-failed");
      setPanel({ kind: "rename", preview: (await response.json()) as TitleRenamePreview });
    } catch {
      toast.error("The rename preview could not be made.");
    } finally {
      setBusy(null);
    }
  }

  async function applyRename() {
    setBusy("rename-apply");
    try {
      const response = await authedFetch(`/api/${variant}/bulk/rename-apply`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(variant === "movies" ? { movieIds: [titleId] } : { seriesIds: [titleId] })
      });
      if (!response.ok) throw new Error("rename-apply-failed");
      const payload = (await response.json()) as { renamedCount?: number; failures?: Array<{ reason?: string }> };
      if ((payload.renamedCount ?? 0) > 0) {
        toast.success("The folder was renamed.");
        onChanged?.();
      } else if (payload.failures?.length) {
        toast.error("The folder could not be renamed", { duration: Infinity, description: payload.failures[0]?.reason });
      } else {
        toast.info("Nothing needed renaming.");
      }
      setPanel(null);
    } catch {
      toast.error("The folder could not be renamed.");
    } finally {
      setBusy(null);
    }
  }

  async function reviewUntracked(issueId: string) {
    setReviewing(issueId);
    try {
      const response = await authedFetch("/api/filesystem/reconciliation/repair", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ issueId, action: "queue-import-review" })
      });
      if (!response.ok) throw new Error("review-failed");
      toast.success("Added to Import issues so you can review it.");
    } catch {
      toast.error("That file could not be added for review.");
    } finally {
      setReviewing(null);
    }
  }

  const isBusy = busy !== null;

  return (
    <>
      <ConfirmDialog
        open={confirmRescan}
        onOpenChange={setConfirmRescan}
        title={`Rescan the folder for ${titleName}?`}
        description="Deluno will check this title's files; any that are gone are marked missing and it stops wanting them. Nothing is deleted."
        confirmLabel="Rescan now"
        confirmVariant="default"
        onConfirm={() => void rescan()}
      />
      <Button
        variant="outline"
        className="w-full"
        onClick={() => setConfirmRescan(true)}
        disabled={disabled || isBusy}
        aria-label={`Rescan the folder for ${titleName}`}
      >
        {busy === "rescan" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <FolderSearch className="h-4 w-4" />}
        Rescan folder
      </Button>
      <Button
        variant="outline"
        className="w-full"
        onClick={() => void previewRename()}
        disabled={disabled || isBusy}
        aria-label={`Preview the folder rename for ${titleName}`}
      >
        {busy === "rename-preview" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <PencilLine className="h-4 w-4" />}
        Preview rename
      </Button>

      <Dialog.Root open={panel !== null} onOpenChange={(open) => { if (!open && busy !== "rename-apply") setPanel(null); }}>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px] data-[state=open]:animate-fade-in-stable" />
          <Dialog.Content
            className={cn(
              "fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2",
              "max-h-[85vh] overflow-y-auto rounded-2xl border border-hairline bg-card shadow-2xl dark:border-white/8",
              "data-[state=open]:animate-fade-in-stable"
            )}
            aria-describedby="title-folder-description"
          >
            {panel?.kind === "rescan" ? (
              <div className="p-6 sm:p-8">
                <Dialog.Title className="text-(length:--type-title-md) font-semibold tracking-[-0.02em] text-foreground">
                  Folder rescanned
                </Dialog.Title>
                <p id="title-folder-description" className="mt-2 text-(length:--type-body) leading-relaxed text-muted-foreground">
                  {panel.result.summary}
                </p>
                {panel.result.outcome === "library-unreachable" || panel.result.outcome === "library-unknown" ? (
                  <p className="mt-3 rounded-lg border border-hairline px-3 py-2 text-sm text-foreground">
                    {panel.result.outcome === "library-unreachable"
                      ? "Nothing was changed: no file was marked missing and the title stays monitored. Reconnect the drive and rescan. If you deleted the files, choose These files really are gone in System, Health."
                      : "Nothing was changed: no file was marked missing and the title stays monitored. Put the title back in a library, then rescan."}
                  </p>
                ) : null}
                {panel.result.missing.length > 0 ? (
                  <div className="mt-4">
                    <p className="text-xs font-semibold uppercase tracking-widest text-muted-foreground">No longer on disk</p>
                    <ul className="mt-1 space-y-1 text-sm text-foreground">
                      {panel.result.missing.map((file) => (
                        <li key={file.path} className="truncate font-mono text-xs" title={file.path}>{file.fileName}</li>
                      ))}
                    </ul>
                  </div>
                ) : null}
                {panel.result.untracked.length > 0 ? (
                  <div className="mt-4">
                    <p className="text-xs font-semibold uppercase tracking-widest text-muted-foreground">Videos Deluno is not tracking</p>
                    <ul className="mt-1 space-y-2">
                      {panel.result.untracked.slice(0, 20).map((file) => (
                        <li key={file.path} className="flex items-center justify-between gap-3">
                          <span className="min-w-0 truncate font-mono text-xs text-foreground" title={file.path}>{file.fileName}</span>
                          <Button
                            size="sm"
                            variant="outline"
                            disabled={reviewing !== null}
                            aria-label={`Review ${file.fileName} for import`}
                            onClick={() => void reviewUntracked(file.issueId)}
                          >
                            Review for import
                          </Button>
                        </li>
                      ))}
                    </ul>
                  </div>
                ) : null}
                <div className="mt-6 flex justify-end">
                  <Button variant="outline" onClick={() => setPanel(null)}>Close</Button>
                </div>
              </div>
            ) : panel?.kind === "rename" ? (
              <div className="p-6 sm:p-8">
                <Dialog.Title className="text-(length:--type-title-md) font-semibold tracking-[-0.02em] text-foreground">
                  Rename preview
                </Dialog.Title>
                <p id="title-folder-description" className="mt-2 text-(length:--type-body) leading-relaxed text-muted-foreground">
                  {panel.preview.message}
                </p>
                {panel.preview.willChange ? (
                  <dl className="mt-4 space-y-2 text-sm">
                    <div>
                      <dt className="text-xs font-semibold uppercase tracking-widest text-muted-foreground">Folder now</dt>
                      <dd className="break-all font-mono text-xs text-foreground">{panel.preview.currentFolder}</dd>
                    </div>
                    <div>
                      <dt className="text-xs font-semibold uppercase tracking-widest text-muted-foreground">Folder after</dt>
                      <dd className="break-all font-mono text-xs text-foreground">{panel.preview.proposedFolder}</dd>
                    </div>
                  </dl>
                ) : null}
                <div className="mt-6 flex justify-end gap-2">
                  <Button variant="outline" onClick={() => setPanel(null)} disabled={busy === "rename-apply"}>
                    {panel.preview.willChange ? "Not now" : "Close"}
                  </Button>
                  {panel.preview.willChange ? (
                    <Button onClick={() => void applyRename()} disabled={busy === "rename-apply"}>
                      {busy === "rename-apply" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                      Rename folder
                    </Button>
                  ) : null}
                </div>
              </div>
            ) : null}
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </>
  );
}
