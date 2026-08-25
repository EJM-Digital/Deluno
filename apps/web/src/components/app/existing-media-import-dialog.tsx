import * as Dialog from "@radix-ui/react-dialog";
import { AlertTriangle, ArrowLeft, ArrowRight, CheckCircle2, FileVideo, Folder, Loader2, X } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";
import { Chip } from "../ui/chip";
import { fetchJson, type ExistingLibraryCandidate, type ExistingLibraryImportResult, type ExistingLibraryPreviewPage, type LibraryItem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";

export function ExistingMediaImportDialog({
  open,
  library,
  onOpenChange,
  onImported
}: {
  open: boolean;
  library: LibraryItem | null;
  onOpenChange: (open: boolean) => void;
  onImported?: () => void;
}) {
  const [page, setPage] = useState<ExistingLibraryPreviewPage | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [history, setHistory] = useState<string[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastResult, setLastResult] = useState<ExistingLibraryImportResult | null>(null);

  useEffect(() => {
    if (!open || !library) return;
    setPage(null);
    setCursor(null);
    setHistory([]);
    setSelected(new Set());
    setLastResult(null);
    setError(null);
    void loadPage(library.id, null);
    // The dialog is a new review session whenever it opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, library?.id]);

  async function loadPage(libraryId: string, nextCursor: string | null) {
    setLoading(true);
    setError(null);
    try {
      const query = new URLSearchParams({ take: "50" });
      if (nextCursor) query.set("cursor", nextCursor);
      const next = await fetchJson<ExistingLibraryPreviewPage>(`/api/libraries/${libraryId}/import-existing/preview?${query}`);
      setPage(next);
      setCursor(nextCursor);
      setSelected(new Set());
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : "The folder could not be reviewed.");
    } finally {
      setLoading(false);
    }
  }

  async function goNext() {
    if (!library || !page?.nextCursor || loading) return;
    setHistory((current) => [...current, cursor ?? ""]);
    await loadPage(library.id, page.nextCursor);
  }

  async function goPrevious() {
    if (!library || history.length === 0 || loading) return;
    const previous = history[history.length - 1] || null;
    setHistory((current) => current.slice(0, -1));
    await loadPage(library.id, previous);
  }

  function togglePath(path: string, checked: boolean) {
    setSelected((current) => {
      const next = new Set(current);
      if (checked) next.add(path);
      else next.delete(path);
      return next;
    });
  }

  function togglePage(checked: boolean) {
    const importable = (page?.items ?? []).filter((item) => item.canImport);
    setSelected(checked ? new Set(importable.map((item) => item.sourcePath)) : new Set());
  }

  async function importSelected() {
    if (!library || selected.size === 0 || busy) return;
    setBusy(true);
    setLastResult(null);
    try {
      const response = await authedFetch(`/api/libraries/${library.id}/import-existing/selected`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sourcePaths: [...selected] })
      });
      if (!response.ok) throw new Error((await response.text().catch(() => "")) || "The selected media could not be imported.");
      const result = (await response.json()) as ExistingLibraryImportResult;
      setLastResult(result);
      setSelected(new Set());
      onImported?.();
      toast.success(`${result.importedCount} ${library.mediaType === "tv" ? "TV show" : "movie"}${result.importedCount === 1 ? "" : "s"} reviewed and added.`);
      await loadPage(library.id, cursor);
    } catch (importError) {
      toast.error(importError instanceof Error ? importError.message : "The selected media could not be imported.");
    } finally {
      setBusy(false);
    }
  }

  const importableCount = useMemo(() => (page?.items ?? []).filter((item) => item.canImport).length, [page?.items]);
  const selectedImportableCount = useMemo(() => [...selected].filter((path) => page?.items.some((item) => item.sourcePath === path && item.canImport)).length, [page?.items, selected]);
  const pageSelected = importableCount > 0 && selectedImportableCount === importableCount;

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-[60] bg-[hsl(222_44%_3%/0.62)] backdrop-blur-[3px]" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-[60] flex max-h-[min(88dvh,860px)] w-[min(1040px,calc(100vw-2rem))] -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-2xl border border-hairline bg-card text-foreground shadow-2xl focus:outline-none">
          <header className="flex shrink-0 items-start justify-between gap-[var(--grid-gap)] border-b border-hairline px-6 py-5">
            <div className="min-w-0">
              <Dialog.Title className="font-display text-[length:var(--type-title-sm)] font-semibold leading-tight">Review existing media</Dialog.Title>
              <Dialog.Description className="mt-1 max-w-3xl text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">
                Deluno checks the library folder first. Nothing is added until you select the files or folders you want to bring in.
              </Dialog.Description>
            </div>
            <Dialog.Close asChild>
              <Button type="button" variant="outline" size="icon" aria-label="Close review existing media" className="h-8 w-8 shrink-0 rounded-[8px]"><X className="h-3.5 w-3.5" /></Button>
            </Dialog.Close>
          </header>

          <div className="min-h-0 flex-1 overflow-y-auto px-6 py-5">
            <div className="grid gap-[var(--grid-gap)]">
              <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-info/25 bg-info/5 px-4 py-3">
                <div className="min-w-0">
                  <p className="text-[length:var(--type-body-sm)] font-semibold text-foreground">{library?.name ?? "Library"}</p>
                  <p className="truncate text-[length:var(--type-caption)] text-muted-foreground">{library?.rootPath ?? "Choose a library folder first."}</p>
                </div>
                <Chip tone="info">Review before import</Chip>
              </div>

              {error ? <div className="rounded-xl border border-destructive/30 bg-destructive/5 px-4 py-3 text-[length:var(--type-body-sm)] text-destructive">{error}</div> : null}

              <div className="overflow-hidden rounded-xl border border-hairline">
                <div className="flex flex-wrap items-center justify-between gap-3 border-b border-hairline bg-surface-2/40 px-4 py-3">
                  <label className="inline-flex items-center gap-2 text-[length:var(--type-body-sm)] font-medium">
                    <Checkbox checked={pageSelected} onCheckedChange={togglePage} disabled={loading || importableCount === 0} />
                    Select all on this page
                  </label>
                  <span className="text-[length:var(--type-caption)] text-muted-foreground">{loading ? "Reading folder…" : `${page?.items.length ?? 0} items shown`}</span>
                </div>

                {loading ? (
                  <div className="flex min-h-48 items-center justify-center gap-2 text-[length:var(--type-body-sm)] text-muted-foreground"><Loader2 className="h-4 w-4 animate-spin" />Reading the next page</div>
                ) : page?.items.length ? (
                  <div className="divide-y divide-hairline">
                    {page.items.map((item) => <CandidateRow key={item.sourcePath} item={item} checked={selected.has(item.sourcePath)} onCheckedChange={(checked) => togglePath(item.sourcePath, checked)} />)}
                  </div>
                ) : (
                  <div className="flex min-h-48 items-center justify-center px-5 text-center text-[length:var(--type-body-sm)] text-muted-foreground">No supported files or folders were found in this library folder.</div>
                )}
              </div>

              {lastResult ? (
                <div className="rounded-xl border border-success/25 bg-success/5 px-4 py-3 text-[length:var(--type-body-sm)]">
                  <p className="flex items-center gap-2 font-semibold text-foreground"><CheckCircle2 className="h-4 w-4 text-success" />{lastResult.importedCount} selected item{lastResult.importedCount === 1 ? "" : "s"} added</p>
                  {lastResult.issues.length ? (
                    <div className="mt-1 text-muted-foreground">
                      <p>{lastResult.issues.length} warning{lastResult.issues.length === 1 ? "" : "s"} or skipped item{lastResult.issues.length === 1 ? "" : "s"} need review.</p>
                      <ul className="mt-2 grid gap-1 text-[length:var(--type-caption)]">
                        {lastResult.issues.slice(0, 5).map((issue) => <li key={`${issue.sourcePath}:${issue.kind}`}><span className="font-medium text-foreground">{issue.sourcePath}</span> — {issue.detail}</li>)}
                      </ul>
                    </div>
                  ) : null}
                </div>
              ) : null}
            </div>
          </div>

          <footer className="flex shrink-0 flex-wrap items-center justify-between gap-3 border-t border-hairline bg-surface-2/40 px-6 py-4">
            <div className="text-[length:var(--type-caption)] text-muted-foreground">{selectedImportableCount} selected on this page</div>
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => void goPrevious()} disabled={loading || history.length === 0}><ArrowLeft className="h-3.5 w-3.5" />Previous</Button>
              <Button type="button" variant="outline" size="sm" onClick={() => void goNext()} disabled={loading || !page?.hasMore}>Next<ArrowRight className="h-3.5 w-3.5" /></Button>
              <Button type="button" size="sm" onClick={() => void importSelected()} disabled={busy || selectedImportableCount === 0}>{busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}Import selected</Button>
            </div>
          </footer>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function CandidateRow({ item, checked, onCheckedChange }: { item: ExistingLibraryCandidate; checked: boolean; onCheckedChange: (checked: boolean) => void }) {
  return (
    <label className={`grid grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-3 px-4 py-3 ${item.canImport ? "cursor-pointer hover:bg-surface-2/40" : "opacity-65"}`}>
      <Checkbox checked={checked} onCheckedChange={onCheckedChange} disabled={!item.canImport} aria-label={`Select ${item.title}`} />
      {item.isDirectory ? <Folder className="h-4 w-4 text-info" /> : <FileVideo className="h-4 w-4 text-muted-foreground" />}
      <span className="min-w-0">
        <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.title}{item.year ? ` (${item.year})` : ""}</span>
        <span className="block truncate font-mono text-[length:var(--type-caption)] text-muted-foreground" title={item.sourcePath}>{item.relativePath}</span>
        {item.issueDetail ? <span className="mt-1 flex items-start gap-1 text-[length:var(--type-caption)] text-warning"><AlertTriangle className="mt-0.5 h-3 w-3 shrink-0" />{item.issueDetail}</span> : null}
      </span>
      <span className="text-right text-[length:var(--type-caption)] text-muted-foreground">{item.detectedQuality ?? "Quality unknown"}<br />{formatBytes(item.fileSizeBytes)}</span>
    </label>
  );
}

function formatBytes(value: number | null) {
  if (value == null || value < 0) return "Size unknown";
  if (value < 1024 * 1024) return `${Math.max(1, Math.round(value / 1024))} KB`;
  if (value < 1024 * 1024 * 1024) return `${(value / (1024 * 1024)).toFixed(1)} MB`;
  return `${(value / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}
