import type { Dispatch, FormEventHandler, MutableRefObject, SetStateAction } from "react";
import { LoaderCircle, Plus, Search, X } from "lucide-react";
import * as Dialog from "@radix-ui/react-dialog";
import type { MetadataProviderStatus, MetadataSearchResult } from "../../lib/api";
import type { CreateFormDraft, LibraryVariant } from "../../hooks/use-library-create";
import { cn } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";
import { Input } from "../ui/input";

type LibraryCreateDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  variant: LibraryVariant;
  label: string;
  singular: string;
  metadataStatus?: MetadataProviderStatus | null;
  isCreating: boolean;
  createForm: CreateFormDraft;
  setCreateForm: Dispatch<SetStateAction<CreateFormDraft>>;
  metadataResults: MetadataSearchResult[];
  setMetadataResults: Dispatch<SetStateAction<MetadataSearchResult[]>>;
  selectedMetadataResults: MetadataSearchResult[];
  setSelectedMetadataResults: Dispatch<SetStateAction<MetadataSearchResult[]>>;
  isSearchingMetadata: boolean;
  metadataSearchSequence: MutableRefObject<number>;
  onSearch: () => void;
  onSelectResult: (result: MetadataSearchResult) => void;
  onCreate: FormEventHandler<HTMLFormElement>;
};

function sameMetadataResult(left: MetadataSearchResult, right: MetadataSearchResult) {
  return left.provider === right.provider && left.providerId === right.providerId;
}

export function LibraryCreateDialog({
  open, onOpenChange, variant, label, singular, metadataStatus, isCreating, createForm,
  setCreateForm, metadataResults, setMetadataResults, selectedMetadataResults,
  setSelectedMetadataResults, isSearchingMetadata, metadataSearchSequence, onSearch,
  onSelectResult, onCreate,
}: LibraryCreateDialogProps) {
  const selectedMetadataCount = selectedMetadataResults.length;

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[3px]" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 flex max-h-[min(88dvh,760px)] w-[calc(100%-2rem)] max-w-5xl -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl">
          <div className="flex items-start justify-between gap-[var(--grid-gap)] border-b border-hairline px-6 py-5">
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <Dialog.Title className="font-display text-xl font-semibold tracking-tight text-foreground">Add {singular}</Dialog.Title>
                <Badge variant={metadataStatus?.isConfigured ? "success" : "warning"}>{metadataStatus?.isConfigured ? "Title matching ready" : "Manual entry"}</Badge>
              </div>
              <Dialog.Description className="mt-1 text-sm text-muted-foreground">Start typing, then pick matches to prefill details. Use Add at the bottom to create what you selected.</Dialog.Description>
            </div>
            <Dialog.Close asChild><Button variant="ghost" size="icon" aria-label={`Close add ${singular}`} disabled={isCreating}><X className="h-4 w-4" /></Button></Dialog.Close>
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto p-6">
            <form onSubmit={(event) => { event.preventDefault(); onSearch(); }}>
              <label className="text-sm font-semibold text-foreground" htmlFor={`add-${variant}-title`}>What do you want to add?</label>
              <div className="mt-2"><Input id={`add-${variant}-title`} autoFocus value={createForm.title} onChange={(event) => {
                metadataSearchSequence.current += 1;
                setMetadataResults([]);
                setSelectedMetadataResults([]);
                setCreateForm((current) => ({ ...current, title: event.target.value, metadata: null }));
              }} placeholder={variant === "movies" ? "Search movies, for example Top Gun" : "Search TV shows, for example Severance"} /></div>
              <p className="mt-2 text-xs text-muted-foreground"><span className="inline-flex items-center gap-2">
                {isSearchingMetadata ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
                {metadataStatus?.isConfigured === false ? "Metadata matching is currently unavailable." : isSearchingMetadata ? "Searching metadata..." : "Matches auto-refresh as you type, or press Enter to refresh now."}
              </span></p>
            </form>

            {metadataStatus?.isConfigured === false ? <p className="mt-3 rounded-xl border border-warning/25 bg-warning/10 p-3 text-sm text-warning">Title matching is temporarily unavailable. You can still add this title manually below.</p> : null}
            {metadataResults.length > 0 ? (
              <div className="mt-5">
                <p className="text-sm font-semibold text-foreground">Choose one or more matches to prefill (this does not add yet)</p>
                <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                  {metadataResults.slice(0, 6).map((result) => {
                    const isSelected = selectedMetadataResults.some((selected) => sameMetadataResult(selected, result));
                    return <button key={`${result.provider}:${result.providerId}`} type="button" onClick={() => onSelectResult(result)} className={cn("flex min-w-0 gap-3 rounded-xl border bg-surface-1 p-3 text-left transition hover:border-primary/45 hover:bg-primary/5", isSelected ? "border-primary/70 bg-primary/10 ring-1 ring-primary/25" : "border-hairline")} title={`Select ${result.title}`}>
                      {result.posterUrl ? <img src={result.posterUrl} alt="" className="h-24 w-16 shrink-0 rounded-lg bg-muted object-cover" /> : <div className="flex h-24 w-16 shrink-0 items-center justify-center rounded-lg bg-muted text-[length:var(--type-caption)] text-muted-foreground">No art</div>}
                      <span className="min-w-0 self-center">
                        <span className="block truncate text-sm font-semibold text-foreground">{result.title}</span>
                        <span className="mt-1 block text-xs text-muted-foreground">{result.year ?? "Unknown year"} · TMDb</span>
                        {result.rating ? <span className="mt-2 block font-mono text-xs text-primary">{result.rating.toFixed(1)} rating</span> : null}
                        {isSelected ? <span className="mt-1 inline-flex rounded-full bg-primary/15 px-2 py-0.5 text-[length:var(--type-micro)] font-semibold text-primary">Selected</span> : <span className="mt-1 block text-[length:var(--type-micro)] text-muted-foreground">Click to select</span>}
                      </span>
                    </button>;
                  })}
                </div>
              </div>
            ) : null}

            <details className="mt-5 rounded-xl border border-hairline bg-surface-1 px-4 py-3">
              <summary className="cursor-pointer text-sm font-medium text-muted-foreground">Can’t find it? Add it manually</summary>
              <div className="mt-3 grid gap-3 sm:grid-cols-2">
                <Input type="number" value={createForm.year} onChange={(event) => setCreateForm((current) => ({ ...current, year: event.target.value }))} placeholder={variant === "movies" ? "Year (optional)" : "Start year (optional)"} />
                <Input value={createForm.imdbId} onChange={(event) => setCreateForm((current) => ({ ...current, imdbId: event.target.value }))} placeholder="IMDb ID (optional)" />
              </div>
            </details>
          </div>

          <form onSubmit={onCreate} className="flex flex-col gap-3 border-t border-hairline bg-surface-1/70 px-6 py-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="inline-flex min-h-[1.25rem] items-center gap-2 text-sm text-muted-foreground">
              <label className="inline-flex select-none items-center gap-2"><Checkbox checked={createForm.monitored} onCheckedChange={(monitored) => setCreateForm((current) => ({ ...current, monitored }))} />Monitor and search automatically</label>
              {selectedMetadataCount > 0 ? <span className="inline-flex items-center gap-2 text-xs font-semibold text-primary"><span className="h-1.5 w-1.5 rounded-full bg-primary" />{selectedMetadataCount} {selectedMetadataCount === 1 ? singular : label} selected</span> : null}
            </div>
            <div className="flex gap-2">
              <Dialog.Close asChild><Button type="button" variant="ghost" disabled={isCreating}>Cancel</Button></Dialog.Close>
              <Button type="submit" disabled={isCreating || (!createForm.title.trim() && selectedMetadataCount === 0)} className="gap-2">
                {isCreating ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />}
                {selectedMetadataCount > 0 ? selectedMetadataCount === 1 ? `Add selected ${singular}` : `Add ${selectedMetadataCount} ${label}` : `Add ${singular} manually`}
              </Button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
