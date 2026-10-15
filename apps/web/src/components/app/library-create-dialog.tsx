import type { Dispatch, FormEventHandler, MutableRefObject, SetStateAction } from "react";
import { ArtworkImage } from "../ui/artwork-image";
import { ArrowUpRight, LoaderCircle, Plus, Search, X } from "lucide-react";
import * as Dialog from "../ui/dialog-primitives";
import type { MetadataProviderStatus, MetadataSearchResult } from "../../lib/api";
import { SHOW_MONITOR_PRESETS, type CreateFormDraft, type LibraryVariant, type ShowMonitorPreset } from "../../hooks/use-library-create";
import { cn } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { Select } from "../ui/select";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";
import { AutomationPausedNotice } from "./automation-paused-notice";
import { Field } from "../ui/field";
import { Input } from "../ui/input";
import { TagPicker } from "./tag-picker";
import { TvdbAttribution } from "./tvdb-attribution";

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
  /**
   * Opens a title Deluno already holds, instead of adding it a second time.
   * Which results those are is decided by the server - see
   * `MetadataSearchResult.libraryEntryId`.
   */
  onOpenHeldResult: (result: MetadataSearchResult) => void;
  onCreate: FormEventHandler<HTMLFormElement>;
  /** Libraries of this type; the choice is offered when there is more than one (#530). */
  libraries?: { id: string; name: string; qualityProfileId?: string | null }[];
  libraryId?: string;
  onLibraryChange?: (libraryId: string) => void;
  /** The quality profiles made for this kind of title (#1145). */
  qualityProfiles?: { id: string; name: string }[];
};

function sameMetadataResult(left: MetadataSearchResult, right: MetadataSearchResult) {
  return left.provider === right.provider && left.providerId === right.providerId;
}

export function LibraryCreateDialog({
  open, onOpenChange, variant, label, singular, metadataStatus, isCreating, createForm,
  setCreateForm, metadataResults, setMetadataResults, selectedMetadataResults,
  setSelectedMetadataResults, isSearchingMetadata, metadataSearchSequence, onSearch,
  onSelectResult, onOpenHeldResult, onCreate, libraries = [], libraryId, onLibraryChange, qualityProfiles = [],
}: LibraryCreateDialogProps) {
  const selectedMetadataCount = selectedMetadataResults.length;
  const chosenLibrary = libraries.find((library) => library.id === (libraryId ?? libraries[0]?.id));
  // The profile shown is the one picked, or else the library's own - the default is a real choice you can see, not a blank.
  const shownProfileId = createForm.qualityProfileId || chosenLibrary?.qualityProfileId || "";

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[3px]" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 flex max-h-[min(88dvh,760px)] w-[calc(100%-2rem)] max-w-5xl -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl">
          <div className="flex items-start justify-between gap-(--grid-gap) border-b border-hairline px-6 py-5">
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
                {metadataStatus?.isConfigured === false ? "Metadata matching is currently unavailable." : isSearchingMetadata ? (variant === "movies" ? "Waiting on TMDb..." : "Waiting on TheTVDB and TMDb...") : "Matches auto-refresh as you type, or press Enter to refresh now."}
              </span></p>
            </form>

            {metadataStatus?.isConfigured === false ? <p className="mt-3 rounded-xl border border-warning/25 bg-warning/10 p-3 text-sm text-warning">Title matching is temporarily unavailable. You can still add this title manually below.</p> : null}
            {metadataResults.length > 0 ? (
              <div className="mt-5">
                <p className="text-sm font-semibold text-foreground">Choose one or more matches to prefill (this does not add yet)</p>
                {variant === "shows" && metadataResults.some((result) => result.tvDbId) ? <TvdbAttribution className="mt-1" /> : null}
                <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                  {metadataResults.slice(0, 6).map((result) => {
                    const isSelected = selectedMetadataResults.some((selected) => sameMetadataResult(selected, result));
                    // Held titles are not offered for selection at all. Adding
                    // one is already a no-op that hands back the row Deluno
                    // has - so a card that said "Selected" and then added
                    // nothing would be the same silence in a different place.
                    const isHeld = Boolean(result.libraryEntryId);
                    return <button key={`${result.provider}:${result.providerId}`} type="button" onClick={() => (isHeld ? onOpenHeldResult(result) : onSelectResult(result))} className={cn("flex min-w-0 gap-3 rounded-xl border bg-surface-1 p-3 text-left transition", isHeld ? "border-success/45 bg-success/5 hover:border-success/70 hover:bg-success/10" : isSelected ? "border-primary/70 bg-primary/10 ring-1 ring-primary/25 hover:border-primary/45 hover:bg-primary/5" : "border-hairline hover:border-primary/45 hover:bg-primary/5")} title={isHeld ? `Open ${result.title} — already in your library` : `Select ${result.title}`}>
                      {result.posterUrl ? <ArtworkImage src={result.posterUrl} alt="" className="h-24 w-16 shrink-0 rounded-lg bg-muted object-cover" /> : <div className="flex h-24 w-16 shrink-0 items-center justify-center rounded-lg bg-muted text-(length:--type-caption) text-muted-foreground">No art</div>}
                      <span className="min-w-0 self-center">
                        <span className="block truncate text-sm font-semibold text-foreground">{result.title}</span>
                        <span className="mt-1 block text-xs text-muted-foreground">{result.year ?? "Unknown year"} · {result.dataSource === "tvdb" ? "TheTVDB" : "TMDb"}</span>
                        {result.rating ? <span className="mt-2 block text-xs tabular-nums text-primary">{result.rating.toFixed(1)} rating</span> : null}
                        {isHeld
                          ? <span className="mt-1 inline-flex items-center gap-1 text-(length:--type-micro) font-semibold text-success">Already in your library — open it<ArrowUpRight className="h-3 w-3" /></span>
                          : isSelected
                            ? <span className="mt-1 inline-flex rounded-full bg-primary/15 px-2 py-0.5 text-(length:--type-micro) font-semibold text-primary">Selected</span>
                            : <span className="mt-1 block text-(length:--type-micro) text-muted-foreground">Click to select</span>}
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

          {/* Where it goes and what it is judged by, chosen before anything is searched (#530, #1145). */}
          {libraries.length > 0 && onLibraryChange ? (
            <div className="grid gap-3 border-t border-hairline px-6 py-4 sm:grid-cols-2">
              <Field label="Add into" help={variant === "movies" ? "Only this library searches for it and holds it. It can be kept in another library too, later, from its page." : "Only this library searches for it and holds it. It can be moved to another library later, from its page."}>
                <Select
                  aria-label="Library"
                  value={libraryId ?? libraries[0].id}
                  onChange={(event) => {
                    onLibraryChange(event.target.value);
                    setCreateForm((current) => ({ ...current, qualityProfileId: "" }));
                  }}
                  options={libraries.map((library) => ({ value: library.id, label: library.name }))}
                />
              </Field>
              {qualityProfiles.length > 0 ? (
                <Field label="Quality profile" help="Starts as the library's own profile. Pick another to judge just this title by it.">
                  <Select
                    aria-label="Quality profile"
                    value={shownProfileId}
                    onChange={(event) => {
                      const next = event.target.value;
                      // Choosing the library's own profile is the same as choosing nothing: the title keeps following the library.
                      setCreateForm((current) => ({ ...current, qualityProfileId: next === (chosenLibrary?.qualityProfileId ?? "") ? "" : next }));
                    }}
                    options={qualityProfiles.map((profile) => ({ value: profile.id, label: profile.id === chosenLibrary?.qualityProfileId ? `${profile.name} (library's)` : profile.name }))}
                  />
                </Field>
              ) : null}
            </div>
          ) : null}

          {/* Optional tags, put on the title as it is added (#1038). */}
          <div className="border-t border-hairline px-6 py-4">
            <Field label="Tags" help="Optional. Put on the title as it is added, so a tagged connection or rule treats it right from the start.">
              <TagPicker
                label={`Tags for the ${singular} being added`}
                value={createForm.tags}
                onChange={(tags) => setCreateForm((current) => ({ ...current, tags }))}
                warnUnused={false}
              />
            </Field>
          </div>

          <form onSubmit={onCreate}className="flex flex-col gap-3 border-t border-hairline bg-surface-1/70 px-6 py-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-col gap-2 text-sm text-muted-foreground">
              <div className="inline-flex min-h-5 flex-wrap items-center gap-x-4 gap-y-2">
                <label className="inline-flex select-none items-center gap-2"><Checkbox checked={createForm.monitored} onCheckedChange={(monitored) => setCreateForm((current) => ({ ...current, monitored }))} />Monitor it</label>

                {selectedMetadataCount > 0 ? <span className="inline-flex items-center gap-2 text-xs font-semibold text-primary"><span className="h-1.5 w-1.5 rounded-full bg-primary" />{selectedMetadataCount} {selectedMetadataCount === 1 ? singular : label} selected</span> : null}
              </div>
              {/* Which episodes to start monitoring (#991) - shows only; a movie has no episodes to pick among. */}
              {variant === "shows" && createForm.monitored ? (
                <div className="flex items-center gap-2">
                  <label className="text-xs text-muted-foreground" htmlFor={`add-${variant}-monitor-preset`}>Monitor</label>
                  <Select
                    id={`add-${variant}-monitor-preset`}
                    aria-label="Which episodes to monitor"
                    value={createForm.monitorPreset}
                    onChange={(event) => {
                      const monitorPreset = event.target.value as ShowMonitorPreset;
                      setCreateForm((current) => ({ ...current, monitorPreset }));
                    }}
                    options={SHOW_MONITOR_PRESETS.map((preset) => ({ value: preset.value, label: preset.label }))}
                  />
                </div>
              ) : null}
              {createForm.searchNow ? <AutomationPausedNotice context="add" noun={variant === "movies" ? "film" : "show"} /> : null}
              <div className="inline-flex flex-wrap items-center gap-x-4 gap-y-2 text-xs">
                <label className="inline-flex select-none items-center gap-2"><Checkbox checked={createForm.searchNow} onCheckedChange={(searchNow) => setCreateForm((current) => ({ ...current, searchNow }))} />Search for it as soon as it is added</label>
              </div>
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
