/**
 * Discover (#785) — a browsable grid of TMDb's own "popular" list, plus every
 * configured import list's entries, with an add action inline. Radarr calls
 * this Discover; Deluno never had a way to browse what to add next, only to
 * search for something already in mind.
 *
 * Popular and each list are separate sections rather than one merged feed:
 * the server already answers "is this in the library, is it excluded" for
 * each shape from its own evidence (`MediaExclusionMatching` for a TMDb card,
 * the intake preview's own `Action` for a list entry), so this reads those
 * two answers rather than inventing a third, unified representation.
 *
 * Contracts: GET /api/discover?mediaType=, POST /api/discover/add,
 * GET /api/libraries, GET /api/quality-profiles.
 */
import { useEffect, useMemo, useState } from "react";
import { ArtworkImage } from "../components/ui/artwork-image";
import { useLoaderData } from "react-router-dom";
import { CircleSlash, Compass, Plus, ShieldCheck } from "lucide-react";
import {
  fetchJson,
  readErrorText,
  type DiscoverAddRequest,
  type DiscoverItem,
  type DiscoverListEntry,
  type DiscoverResponse,
  type LibraryItem,
  type QualityProfileItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card } from "../components/ui/card";
import { Drawer, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { EmptyState } from "../components/shell/empty-state";
import { Field } from "../components/ui/field";
import { LibraryGridSkeleton } from "../components/shell/skeleton";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { FilterChips } from "../components/ui/filter-chips";
import { HeaderTabsPortal, useHeaderSlot } from "../components/ui/header-tabs";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { cn } from "../lib/utils";

const DISCOVER_TYPE_KEY = "deluno-discover-type";

type DiscoverMediaType = "movies" | "series";

interface DiscoverLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
}

export async function discoverLoader(): Promise<DiscoverLoaderData> {
  const [libraries, qualityProfiles] = await Promise.all([
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles")
  ]);
  return { libraries, qualityProfiles };
}

/** What the add drawer needs, whichever section a person clicked Add from. */
interface AddCandidate {
  title: string;
  year: number | null;
  imdbId: string | null;
  provider: string | null;
  providerId: string | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
}

function candidateFromDiscoverItem(item: DiscoverItem): AddCandidate {
  return {
    title: item.title,
    year: item.year,
    imdbId: item.imdbId,
    provider: item.provider,
    providerId: item.providerId,
    overview: item.overview,
    posterUrl: item.posterUrl,
    backdropUrl: item.backdropUrl
  };
}

function candidateFromListEntry(entry: DiscoverListEntry): AddCandidate {
  return {
    title: entry.title,
    year: entry.year,
    imdbId: entry.imdbId,
    provider: null,
    providerId: null,
    overview: null,
    posterUrl: null,
    backdropUrl: null
  };
}

export function DiscoverPage() {
  const { libraries, qualityProfiles } = useLoaderData() as DiscoverLoaderData;
  // Movies or TV is remembered between visits, as Schedule remembers its range (the owner, 2 Oct 2026).
  const [mediaType, setMediaType] = useState<DiscoverMediaType>(() => {
    try {
      return window.localStorage.getItem(DISCOVER_TYPE_KEY) === "series" ? "series" : "movies";
    } catch {
      return "movies";
    }
  });
  useEffect(() => {
    try {
      window.localStorage.setItem(DISCOVER_TYPE_KEY, mediaType);
    } catch {
      // Nothing kept: the choice holds until the page is left.
    }
  }, [mediaType]);
  const slot = useHeaderSlot();
  const typeChips = (
    <FilterChips<DiscoverMediaType>
      label="Discover media type"
      value={mediaType}
      onValueChange={setMediaType}
      options={[
        { value: "movies", label: "Movies" },
        { value: "series", label: "TV" }
      ]}
    />
  );
  const [data, setData] = useState<DiscoverResponse | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [addTarget, setAddTarget] = useState<{ candidate: AddCandidate; mediaType: DiscoverMediaType } | null>(null);

  useEffect(() => {
    let cancelled = false;
    setIsLoading(true);
    setError(null);
    fetchJson<DiscoverResponse>(`/api/discover?mediaType=${mediaType}`)
      .then((response) => {
        if (!cancelled) setData(response);
      })
      .catch((caught) => {
        if (!cancelled) setError(caught instanceof Error ? caught.message : "Discover could not be loaded.");
      })
      .finally(() => {
        if (!cancelled) setIsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [mediaType]);

  // Memoized so the Add drawer's own reset-on-open effect (keyed on this
  // array's identity) does not fire on every unrelated re-render of this
  // page and silently wipe whatever the person had just chosen in the
  // drawer - a plain .filter() here made a fresh array every render.
  const relevantLibraries = useMemo(
    () => libraries.filter((library) =>
      mediaType === "series" ? library.mediaType === "tv" || library.mediaType === "shows" : library.mediaType === "movies"
    ),
    [libraries, mediaType]
  );
  const relevantProfiles = useMemo(
    () => qualityProfiles.filter((profile) =>
      mediaType === "series" ? profile.mediaType === "tv" || profile.mediaType === "shows" : profile.mediaType === "movies"
    ),
    [qualityProfiles, mediaType]
  );

  function markPopularAdded(title: string, year: number | null) {
    setData((current) =>
      current
        ? {
            ...current,
            popular: current.popular.map((item) =>
              item.title === title && item.year === year ? { ...item, inLibrary: true } : item
            )
          }
        : current
    );
  }

  return (
    <div className="grid gap-(--page-gap)">
      {/* Movies / TV are a page-wide filter, so they are chips on the title line (Activity's mechanism); where the header has no slot, a row of their own. */}
      {slot ? (
        <HeaderTabsPortal slot={slot}>
          <div data-header-controls className="flex items-center">{typeChips}</div>
        </HeaderTabsPortal>
      ) : (
        <div>{typeChips}</div>
      )}

      <Card as="section" className="dark:border-white/[0.07]">
        <header className="flex min-h-(--list-header-height) items-center gap-2 border-b border-hairline px-(--card-pad-x)">
          <Compass className="h-4 w-4 text-muted-foreground" />
          <h2 className="text-(length:--type-card-title) font-semibold leading-none text-foreground">Popular on TMDb</h2>
          {data ? (
            <span className="text-(length:--type-caption) text-muted-foreground">{data.popular.length} titles</span>
          ) : null}
        </header>

        {isLoading ? (
          <div className="p-(--tile-pad)"><LibraryGridSkeleton count={12} /></div>
        ) : error ? (
          <EmptyState
            variant="offline"
            title="Discover could not be loaded"
            description={error}
          />
        ) : !data || data.popular.length === 0 ? (
          <EmptyState
            variant="library"
            title="Nothing to show right now"
            description="The popular list could not be loaded just now. Deluno will try again when you open this page."
          />
        ) : (
          <div className="grid grid-cols-[repeat(auto-fill,minmax(var(--library-card-sm),1fr))] gap-(--library-grid-gap) p-(--tile-pad)">
            {/* The Movies page's poster size (its small cards, its gap): as many across as fit, never a sixth of the page each. */}
            {data.popular.map((item) => (
              <DiscoverPosterCard
                key={`${item.provider}:${item.providerId}`}
                item={item}
                onAdd={() => setAddTarget({ candidate: candidateFromDiscoverItem(item), mediaType })}
              />
            ))}
          </div>
        )}
      </Card>

      {!isLoading && data && data.lists.length > 0
        ? data.lists.map((section) => (
            <ListCard key={section.sourceId} title={section.sourceName} count={`${section.items.length} entries`}>
              {section.items.length === 0 ? (
                <ListEmpty title="Nothing in this list right now" description="Check the list's own settings under Discover Media, Import Lists." />
              ) : (
                <ListTable listId="discover-lists" columns={[{ label: "Title" }, { label: "Status", width: LIST_TRACK.status }, { label: "", width: "96px" }]}>
                  {section.items.map((entry) => (
                    <ListRow key={`${entry.title}:${entry.year ?? ""}`}>
                      <ListNameCell name={entry.title} sub={entry.year ? String(entry.year) : "Year unknown"} />
                      <ListCell>
                        <ListEntryStatusBadge entry={entry} />
                      </ListCell>
                      <ListCell align="end">
                        {entry.inLibrary ? (
                          <span className="inline-flex items-center gap-1 text-(length:--type-caption) text-success">
                            <ShieldCheck className="h-3.5 w-3.5" /> Held
                          </span>
                        ) : entry.excluded ? (
                          <span className="inline-flex items-center gap-1 text-(length:--type-caption) text-muted-foreground">
                            <CircleSlash className="h-3.5 w-3.5" /> Excluded
                          </span>
                        ) : (
                          <Button
                            type="button"
                            size="sm"
                            variant="outline"
                            aria-label={`Add ${entry.title}`}
                            onClick={() => setAddTarget({ candidate: candidateFromListEntry(entry), mediaType })}
                          >
                            <Plus className="h-3.5 w-3.5" /> Add
                          </Button>
                        )}
                      </ListCell>
                    </ListRow>
                  ))}
                </ListTable>
              )}
            </ListCard>
          ))
        : null}

      <DiscoverAddDrawer
        target={addTarget}
        libraries={relevantLibraries}
        qualityProfiles={relevantProfiles}
        onOpenChange={(open) => {
          if (!open) setAddTarget(null);
        }}
        onAdded={(title, year) => {
          markPopularAdded(title, year);
          setAddTarget(null);
        }}
      />
    </div>
  );
}

function ListEntryStatusBadge({ entry }: { entry: DiscoverListEntry }) {
  if (entry.inLibrary) return <Badge variant="success">Already in library</Badge>;
  if (entry.excluded) return <Badge variant="default">Excluded</Badge>;
  if (entry.status === "not eligible") return <Badge variant="warning">Not eligible</Badge>;
  return <Badge variant="info">Would add</Badge>;
}

function DiscoverPosterCard({ item, onAdd }: { item: DiscoverItem; onAdd: () => void }) {
  return (
    <div className="group relative overflow-hidden rounded-[10px] border border-hairline bg-surface-1">
      <div className="aspect-2/3 w-full overflow-hidden bg-surface-2">
        {item.posterUrl ? (
          <ArtworkImage src={item.posterUrl} alt="" className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-(length:--type-caption) text-muted-foreground">No art</div>
        )}
      </div>
      {item.inLibrary || item.excluded ? (
        <div className="absolute inset-x-0 top-0 flex justify-end p-1.5">
          {item.inLibrary ? (
            <Badge variant="success" className="shadow-sm">
              <ShieldCheck className="h-3 w-3" /> In library
            </Badge>
          ) : (
            <Badge variant="default" className="shadow-sm">
              <CircleSlash className="h-3 w-3" /> Excluded
            </Badge>
          )}
        </div>
      ) : null}
      <div className="p-2">
        <p className="truncate text-(length:--type-body-sm) font-semibold text-foreground" title={item.title}>{item.title}</p>
        <p className="text-(length:--type-caption) text-muted-foreground">
          {item.year ?? "Unknown year"}
          {item.rating ? ` · ${item.rating.toFixed(1)}` : ""}
        </p>
        {!item.inLibrary ? (
          <Button type="button" size="sm" variant="outline" className="mt-1.5 w-full" aria-label={`Add ${item.title}`} onClick={onAdd}>
            <Plus className="h-3.5 w-3.5" /> Add
          </Button>
        ) : null}
      </div>
    </div>
  );
}

function DiscoverAddDrawer({
  target,
  libraries,
  qualityProfiles,
  onOpenChange,
  onAdded
}: {
  target: { candidate: AddCandidate; mediaType: DiscoverMediaType } | null;
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  onOpenChange: (open: boolean) => void;
  onAdded: (title: string, year: number | null) => void;
}) {
  const [libraryId, setLibraryId] = useState<string>("");
  const [qualityProfileId, setQualityProfileId] = useState<string>("");
  const [monitored, setMonitored] = useState(true);
  const [searchNow, setSearchNow] = useState(true);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!target) return;
    setLibraryId(libraries[0]?.id ?? "");
    setQualityProfileId("");
    setMonitored(true);
    setSearchNow(true);
  }, [target, libraries]);

  if (!target) {
    return <Drawer open={false} onOpenChange={onOpenChange} title="Add title"><div /></Drawer>;
  }

  const { candidate, mediaType } = target;
  const chosenLibrary = libraries.find((library) => library.id === libraryId);
  const shownProfileId = qualityProfileId || chosenLibrary?.qualityProfileId || "";

  async function handleAdd() {
    setBusy(true);
    try {
      const body: DiscoverAddRequest = {
        mediaType,
        title: candidate.title,
        year: candidate.year,
        imdbId: candidate.imdbId,
        provider: candidate.provider,
        providerId: candidate.providerId,
        overview: candidate.overview,
        posterUrl: candidate.posterUrl,
        backdropUrl: candidate.backdropUrl,
        libraryId: libraryId || null,
        // The library's own profile is the same as choosing nothing: the title keeps following the library.
        qualityProfileId: qualityProfileId && qualityProfileId !== chosenLibrary?.qualityProfileId ? qualityProfileId : null,
        monitored,
        searchForMissingNow: searchNow,
        searchForCutoffUnmetNow: searchNow
      };
      const response = await authedFetch("/api/discover/add", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      if (!response.ok) {
        throw new Error(await readErrorText(response, `${candidate.title} could not be added.`));
      }
      toast.success(`${candidate.title} added${searchNow ? " — searching now" : ""}.`);
      onAdded(candidate.title, candidate.year);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That title could not be added.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Drawer
      open
      onOpenChange={onOpenChange}
      title={`Add ${candidate.title}`}
      description={candidate.year ? String(candidate.year) : "Year unknown"}
      footer={
        <DrawerFooter
          state={busy ? "saving" : "clean"}
          saveType="button"
          saveLabel="Add"
          saveEnabled={!busy && libraries.length > 0}
          onSave={() => void handleAdd()}
          onCancel={() => onOpenChange(false)}
        />
      }
    >
      <DrawerSection title="Where it goes">
        {libraries.length === 0 ? (
          <p className={cn("density-help leading-relaxed text-muted-foreground")}>
            No {mediaType === "series" ? "TV" : "movie"} library is set up yet. Create one under Media Management, Library & Storage first.
          </p>
        ) : (
          <>
            <Field label="Library">
              <Select
                aria-label="Library"
                value={libraryId}
                onChange={(event) => {
                  setLibraryId(event.target.value);
                  setQualityProfileId("");
                }}
                options={libraries.map((library) => ({ value: library.id, label: library.name }))}
              />
            </Field>
            <Field label="Quality profile" help="Starts as the library's own profile. Pick another to judge just this title by it.">
              <Select
                aria-label="Quality profile"
                value={shownProfileId}
                onChange={(event) => setQualityProfileId(event.target.value)}
                options={qualityProfiles.map((profile) => ({ value: profile.id, label: profile.id === chosenLibrary?.qualityProfileId ? `${profile.name} (library's)` : profile.name }))}
              />
            </Field>
          </>
        )}
      </DrawerSection>
      <DrawerSection title="Monitoring">
        <SwitchRow
          label="Monitor it"
          description="Keep watching this title for missing files and better quality."
          checked={monitored}
          onCheckedChange={setMonitored}
        />
        <SwitchRow
          label="Search for it as soon as it is added"
          description="Ticked: Deluno searches right away. Unticked: nothing is searched until the next scheduled search."
          checked={searchNow}
          onCheckedChange={setSearchNow}
        />
      </DrawerSection>
    </Drawer>
  );
}
