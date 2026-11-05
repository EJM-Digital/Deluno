import type { Dispatch, FormEventHandler, MutableRefObject, SetStateAction } from "react";
import { ArtworkImage } from "../ui/artwork-image";
import { ArrowUpRight, Check, LoaderCircle, Plus, Search, Star, X } from "lucide-react";
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
  /** Whether TMDb has a next page of matches for this search (#1473). */
  hasMoreMatches?: boolean;
  isLoadingMoreMatches?: boolean;
  /** Asks for the next page and adds it below the matches; the caller says why a load failed. */
  onMoreMatches?: () => void;
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
  hasMoreMatches = false, isLoadingMoreMatches = false, onMoreMatches, onSelectResult, onOpenHeldResult, onCreate, libraries = [], libraryId, onLibraryChange, qualityProfiles = [],
}: LibraryCreateDialogProps) {
  const selectedMetadataCount = selectedMetadataResults.length;
  const chosenLibrary = libraries.find((library) => library.id === (libraryId ?? libraries[0].id));
  // The profile shown is the one picked, or else the library's own - the default is a real choice you can see, not a blank.
  const shownProfileId = createForm.qualityProfileId || chosenLibrary?.qualityProfileId || "";

  const manualEntry = (
    <details className="mt-3 rounded-xl border border-hairline bg-surface-1 px-4 py-3">
      <summary className="cursor-pointer text-sm font-medium text-muted-foreground">Can’t find it? Add it manually</summary>
      <div className="mt-3 grid gap-3 sm:grid-cols-2">
        <Input type="number" value={createForm.year} onChange={(event) => setCreateForm((current) => ({ ...current, year: event.target.value }))} placeholder={variant === "movies" ? "Year (optional)" : "Start year (optional)"} />
        <Input value={createForm.imdbId} onChange={(event) => setCreateForm((current) => ({ ...current, imdbId: event.target.value }))} placeholder="IMDb ID (optional)" />
      </div>
    </details>
  );

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[3px]" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 flex h-[min(96dvh,1180px)] w-[calc(100%-1.5rem)] max-w-[1600px] -translate-x-1/2 -translate-y-1/2 flex-col overflow-y-auto rounded-2xl border border-hairline bg-card shadow-2xl md:overflow-hidden">
          <div className="flex items-start justify-between gap-(--grid-gap) border-b border-hairline px-6 py-5">
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <Dialog.Title className="font-display text-xl font-semibold tracking-tight text-foreground">Add {singular}</Dialog.Title>
                <Badge variant={metadataStatus?.isConfigured ? "success" : "warning"}>{metadataStatus?.isConfigured ? "Title matching ready" : "Manual entry"}</Badge>
              </div>
              <Dialog.Description className="mt-1 text-sm [@media(max-height:800px)]:hidden text-muted-foreground">Start typing, then pick matches to prefill details. Use Add at the bottom to create what you selected.</Dialog.Description>
            </div>
            <Dialog.Close asChild><Button variant="ghost" size="icon" aria-label={`Close add ${singular}`} disabled={isCreating}><X className="h-4 w-4" /></Button></Dialog.Close>
          </div>

          <div className="flex flex-col px-6 pt-5 md:min-h-0 md:flex-1">
            <form className="shrink-0" onSubmit={(event) => { event.preventDefault(); onSearch(); }}>
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
              <div className="mt-4 flex flex-col md:min-h-0 md:flex-1">
                <p className="shrink-0 text-sm font-semibold text-foreground">Choose one or more matches to prefill (this does not add yet)</p>
                {variant === "shows" && metadataResults.some((result) => result.tvDbId) ? <TvdbAttribution className="mt-1 shrink-0" /> : null}
                {/* The matches scroll here, so the search above and the settings below stay in view. */}
                <div className="mt-3 pb-2 pr-1 md:min-h-0 md:flex-1 md:overflow-y-auto" data-testid="add-matches">
                  <div className="grid gap-3 md:grid-cols-2 2xl:grid-cols-3">
                    {metadataResults.map((result) => {
                      const isSelected = selectedMetadataResults.some((selected) => sameMetadataResult(selected, result));
                      // Held titles are not offered for selection at all. Adding
                      // one is already a no-op that hands back the row Deluno
                      // has - so a card that said "Selected" and then added
                      // nothing would be the same silence in a different place.
                      const isHeld = Boolean(result.libraryEntryId);
                      const card = <>
                        {result.posterUrl ? <ArtworkImage src={result.posterUrl} alt="" className="aspect-2/3 w-24 shrink-0 rounded-lg bg-muted object-cover sm:w-32" /> : <div className="flex aspect-2/3 w-24 shrink-0 items-center justify-center rounded-lg bg-muted text-(length:--type-caption) text-muted-foreground sm:w-32">No art</div>}
                        <span className="min-w-0 flex-1">
                          <span className="line-clamp-2 block font-display text-base font-semibold leading-snug text-foreground">{result.title}</span>
                          <span className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
                            <span>{result.year ?? "Unknown year"} · {result.dataSource === "tvdb" ? "TheTVDB" : "TMDb"}</span>
                            {result.rating ? <span className="inline-flex items-center gap-1 tabular-nums text-primary"><Star className="h-3 w-3" aria-hidden />{result.rating.toFixed(1)} rating</span> : null}
                          </span>
                          {result.overview ? <span className={cn("mt-2 block text-sm leading-relaxed text-muted-foreground", isSelected ? "" : "line-clamp-4")}>{result.overview}</span> : <span className="mt-2 block text-sm italic text-muted-foreground">No synopsis yet.</span>}
                          {isHeld
                            ? <span className="mt-3 flex flex-wrap items-center gap-2"><Badge variant="success"><Check className="h-3 w-3" aria-hidden />In your library</Badge><Button type="button" size="sm" variant="secondary" className="gap-1" aria-label={`Open ${result.title}, already in your library`} title={`Open ${result.title} — already in your library`} onClick={() => onOpenHeldResult(result)}>Open<ArrowUpRight className="h-3 w-3" aria-hidden /></Button></span>
                            : isSelected
                              ? <span className="mt-2 inline-flex rounded-full bg-primary/15 px-2 py-0.5 text-(length:--type-micro) font-semibold text-primary">Selected</span>
                              : <span className="mt-2 block text-(length:--type-micro) text-muted-foreground">Click to select</span>}
                        </span>
                      </>;
                      // Already in the library: said in words, not selectable, and opened only by its own Open button, so the search stays (#1507).
                      if (isHeld) return <div key={`${result.provider}:${result.providerId}`} data-held="true" className="flex min-w-0 items-start gap-3 rounded-xl border border-dashed border-hairline bg-surface-1/40 p-3 text-left sm:p-4">{card}</div>;
                      return <button key={`${result.provider}:${result.providerId}`} type="button" onClick={() => onSelectResult(result)} className={cn("flex min-w-0 items-start gap-3 rounded-xl border p-3 text-left transition sm:p-4", isSelected ? "border-primary/70 bg-primary/10 ring-1 ring-primary/25 hover:border-primary/45 hover:bg-primary/5" : "border-hairline bg-surface-1 hover:border-primary/45 hover:bg-primary/5")} title={`Select ${result.title}`}>{card}</button>;
                    })}
                  </div>
                  {hasMoreMatches && onMoreMatches ? (
                    <div className="mt-4 flex justify-center">
                      <Button type="button" variant="secondary" onClick={onMoreMatches} disabled={isLoadingMoreMatches} className="gap-2">
                        {isLoadingMoreMatches ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                        More matches
                      </Button>
                    </div>
                  ) : null}
                  {manualEntry}
                </div>
              </div>
            ) : manualEntry}

          </div>

          {/* Where it goes and what it is judged by, chosen before anything is searched (#530, #1145). */}
          <div className="grid shrink-0 gap-3 border-t border-hairline px-6 py-4 sm:grid-cols-2 xl:grid-cols-[1fr_1fr_1.4fr]">
          {libraries.length > 0 && onLibraryChange ? (
            <>
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
                      setCreateForm((current) => ({ ...current, qualityProfileId: next === chosenLibrary?.qualityProfileId ? "" : next }));
                    }}
                    options={qualityProfiles.map((profile) => ({ value: profile.id, label: profile.id === chosenLibrary?.qualityProfileId ? `${profile.name} (library's)` : profile.name }))}
                  />
                </Field>
              ) : null}
            </>
          ) : null}

          {/* Optional tags, put on the title as it is added (#1038). */}
          <div className={libraries.length > 0 && onLibraryChange ? "sm:col-span-2 xl:col-span-1" : "sm:col-span-2 xl:col-span-3"}>
            <Field label="Tags (optional)">
              <TagPicker
                label={`Tags for the ${singular} being added`}
                value={createForm.tags}
                onChange={(tags) => setCreateForm((current) => ({ ...current, tags }))}
                warnUnused={false}
              />
            </Field>
          </div>
          </div>

          <form onSubmit={onCreate} className="sticky bottom-0 z-10 flex shrink-0 flex-col md:static gap-3 border-t border-hairline bg-card px-6 py-4 sm:flex-row md:bg-surface-1/70 sm:items-center sm:justify-between">
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
              {createForm.monitored && createForm.searchNow ? <AutomationPausedNotice context="add" noun={variant === "movies" ? "film" : "show"} /> : null}
              <div className="inline-flex flex-wrap items-center gap-x-4 gap-y-2 text-xs">
                <label className="inline-flex select-none items-center gap-2"><Checkbox checked={createForm.monitored && createForm.searchNow} disabled={!createForm.monitored} onCheckedChange={(searchNow) => setCreateForm((current) => ({ ...current, searchNow }))} />Search for it as soon as it is added</label>
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
