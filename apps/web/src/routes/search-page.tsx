/**
 * Search (#753) — a free-text query fanned out to every enabled indexer,
 * no title required. Prowlarr's own Search screen: type a query, pick a
 * type, choose which indexers and categories to ask, get back one combined,
 * sortable results table with the same enriched columns per-title
 * interactive search results show.
 *
 *   Query box · search-type picker · indexer & category checklists
 *   ListTable (sortable, filterable) -> Grab confirm
 *
 * Contracts: POST /api/search, /api/search/grab, GET /api/search/categories.
 * Grabbing routes through the same decision (IAcquisitionDecisionPipeline)
 * and dispatch (IDownloadClientGrabService) machinery a per-title grab uses;
 * there is no library to route a download client through for a title-less
 * search, so the client is resolved on the server the same way a per-title
 * grab's routing would for a library of the release's own media type —
 * never a client hand-picked here.
 */
import { useEffect, useState } from "react";
import { ArrowUpDown, Loader2, Search as SearchIcon } from "lucide-react";
import { BlockReleaseAction } from "../components/app/block-release-action";
import { Button } from "../components/ui/button";
import { CheckboxRow } from "../components/ui/checkbox";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFacts, DrawerFooter } from "../components/ui/drawer";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { candidateLabel, candidateTone } from "../lib/release-candidate-status";
import {
  type CandidateSortKey,
  defaultSortDirection,
  filterCandidates,
  formatProtocol,
  formatReleaseAge,
  formatSeedersAndLeechers,
  sortCandidates,
  type SortDirection
} from "../lib/release-candidate-columns";
import { fetchJson, type IndexerItem } from "../lib/api";
import { cn } from "../lib/utils";
import { authedFetch } from "../lib/use-auth";
import { toast } from "../components/shell/toaster";

interface ManualSearchCandidate {
  releaseName: string;
  indexerId: string;
  indexerName: string;
  quality: string;
  meetsCutoff: boolean;
  decisionStatus?: string;
  decisionReasons?: string[];
  sizeBytes?: number | null;
  seeders?: number | null;
  leechers?: number | null;
  indexerProtocol?: string | null;
  releaseAgeHours?: number | null;
  downloadUrl?: string | null;
  /** Which engine this candidate came from — only meaningful for a "basic" search, which asks both (#753). */
  searchType?: "movies" | "tv";
}

interface SearchCategoryOption {
  value: number;
  label: string;
}

type SearchType = "movie" | "tv" | "basic";

export function SearchPage() {
  const [query, setQuery] = useState("");
  const [searchType, setSearchType] = useState<SearchType>("movie");
  const [indexers, setIndexers] = useState<IndexerItem[] | null>(null);
  const [selectedIndexerIds, setSelectedIndexerIds] = useState<Set<string>>(new Set());
  const [categories, setCategories] = useState<SearchCategoryOption[] | null>(null);
  const [selectedCategoryIds, setSelectedCategoryIds] = useState<Set<number>>(new Set());
  const [searching, setSearching] = useState(false);
  const [results, setResults] = useState<ManualSearchCandidate[] | null>(null);
  const [candidateSort, setCandidateSort] = useState<{ key: CandidateSortKey; direction: SortDirection } | null>(null);
  const [protocolFilter, setProtocolFilter] = useState("");
  const [indexerFilter, setIndexerFilter] = useState("");
  const [grabTarget, setGrabTarget] = useState<ManualSearchCandidate | null>(null);
  const [grabbing, setGrabbing] = useState(false);

  useEffect(() => {
    let active = true;
    void fetchJson<IndexerItem[]>("/api/indexers")
      .then((list) => { if (active) setIndexers(list); })
      .catch(() => { if (active) setIndexers([]); });
    void fetchJson<SearchCategoryOption[]>("/api/search/categories")
      .then((list) => { if (active) setCategories(Array.isArray(list) ? list : []); })
      .catch(() => { if (active) setCategories([]); });
    return () => { active = false; };
  }, []);

  async function runSearch() {
    const trimmed = query.trim();
    if (!trimmed) {
      toast.error("Type something to search for.");
      return;
    }

    setSearching(true);
    setCandidateSort(null);
    setProtocolFilter("");
    setIndexerFilter("");
    try {
      const body = await fetchJson<{ candidates: ManualSearchCandidate[]; indexersQueried: number }>("/api/search", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          query: trimmed,
          searchType,
          indexerIds: selectedIndexerIds.size > 0 ? [...selectedIndexerIds] : null,
          categoryIds: selectedCategoryIds.size > 0 ? [...selectedCategoryIds] : null
        })
      });
      setResults(body.candidates);
      if (body.indexersQueried === 0) {
        toast.error("No enabled indexer covers this search. Check Find & Download -> Indexers.");
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That search could not run.");
      setResults([]);
    } finally {
      setSearching(false);
    }
  }

  function toggleSort(key: CandidateSortKey) {
    setCandidateSort((current) =>
      current?.key === key
        ? { key, direction: current.direction === "asc" ? "desc" : "asc" }
        : { key, direction: defaultSortDirection(key) }
    );
  }

  const visibleResults = results
    ? (() => {
        const filtered = filterCandidates(results, { protocol: protocolFilter, indexerName: indexerFilter });
        return candidateSort ? sortCandidates(filtered, candidateSort.key, candidateSort.direction) : filtered;
      })()
    : [];
  const protocolOptions = results ? [...new Set(results.map((item) => item.indexerProtocol).filter((value): value is string => Boolean(value)))] : [];
  const indexerOptions = results ? [...new Set(results.map((item) => item.indexerName))] : [];

  function openGrab(candidate: ManualSearchCandidate) {
    setGrabTarget(candidate);
  }

  async function submitGrab() {
    if (!grabTarget) return;
    setGrabbing(true);
    try {
      const response = await authedFetch("/api/search/grab", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          releaseName: grabTarget.releaseName,
          indexerId: grabTarget.indexerId,
          indexerName: grabTarget.indexerName,
          downloadUrl: grabTarget.downloadUrl,
          quality: grabTarget.quality,
          sizeBytes: grabTarget.sizeBytes,
          seeders: grabTarget.seeders,
          // A "basic" result carries its own engine; anything else uses the
          // page's own type (never "basic" itself, which the server does not
          // know how to route a grab through).
          searchType: grabTarget.searchType ?? (searchType === "tv" ? "tv" : "movie")
        })
      });
      const body = (await response.json().catch(() => null)) as
        | { succeeded?: boolean; message?: string; downloadClientName?: string; errors?: Record<string, string[]> }
        | null;
      if (!response.ok) {
        throw new Error(firstValidationMessage(body) ?? "That release could not be sent.");
      }
      if (body?.succeeded) {
        toast.success(`${grabTarget.releaseName} sent to ${body.downloadClientName ?? "the download client"}.`);
        setGrabTarget(null);
      } else {
        toast.error(body?.message || "That release could not be sent.");
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That release could not be sent.");
    } finally {
      setGrabbing(false);
    }
  }

  return (
    <div className="flex flex-col gap-[var(--page-gap)]">
      <ListCard title="Search every indexer at once" count="No title required">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Query">
            <div className="flex gap-2">
              <Input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                onKeyDown={(event) => { if (event.key === "Enter") void runSearch(); }}
                placeholder="Search for a release…"
                className="flex-1"
                autoFocus
              />
              <Button type="button" onClick={() => void runSearch()} disabled={searching}>
                {searching ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <SearchIcon className="h-3.5 w-3.5" />}
                Search
              </Button>
            </div>
          </Field>
          <Field label="Type">
            <SegmentedControl
              value={searchType}
              onValueChange={(value) => {
                setSearchType(value);
                // A category picked for one engine's family means nothing to
                // the other, so switching type clears any that no longer apply.
                setSelectedCategoryIds(new Set());
              }}
              options={[{ value: "movie", label: "Movie" }, { value: "tv", label: "TV" }, { value: "basic", label: "Basic" }]}
            />
          </Field>
          <Field label="Indexers" optional help="Leave every box unchecked to search every enabled indexer that covers this type.">
            <IndexerChecklist
              indexers={indexers}
              selected={selectedIndexerIds}
              onChange={setSelectedIndexerIds}
            />
          </Field>
          <Field label="Categories" optional help="Leave every box unchecked to search every category this type covers.">
            <CategoryChecklist
              categories={categories}
              searchType={searchType}
              selected={selectedCategoryIds}
              onChange={setSelectedCategoryIds}
            />
          </Field>
        </div>
      </ListCard>

      {results !== null ? (
        <ListCard title="Results" count={`${visibleResults.length} of ${results.length} release${results.length === 1 ? "" : "s"}`}>
          {(protocolOptions.length > 1 || indexerOptions.length > 1) ? (
            <div className="flex flex-wrap items-center gap-2 border-b border-hairline p-[var(--card-pad-x)]">
              <Select
                value={protocolFilter}
                onChange={(event) => setProtocolFilter(event.target.value)}
                options={[{ value: "", label: "Every protocol" }, ...protocolOptions.map((protocol) => ({ value: protocol, label: formatProtocol(protocol) }))]}
                aria-label="Filter by protocol"
                className="max-w-[10rem]"
              />
              <Select
                value={indexerFilter}
                onChange={(event) => setIndexerFilter(event.target.value)}
                options={[{ value: "", label: "Every indexer" }, ...indexerOptions.map((name) => ({ value: name, label: name }))]}
                aria-label="Filter by indexer"
                className="max-w-[12rem]"
              />
            </div>
          ) : null}
          {results.length === 0 ? (
            <ListEmpty title="No releases found" description="Try a different query, or check that an indexer covering this type is enabled." />
          ) : (
            <ListTable
              columns={[
                { label: "Release" },
                // Only a "basic" search asks both engines, so this column only
                // earns its place there -- a movie or TV search already says
                // its own type once, in the toolbar above.
                ...(searchType === "basic" ? [{ label: "Type" }] : []),
                { label: <SortHeader label="Protocol" active={candidateSort?.key === "protocol"} direction={candidateSort?.direction} onClick={() => toggleSort("protocol")} />, mobile: true },
                { label: <SortHeader label="Age" active={candidateSort?.key === "age"} direction={candidateSort?.direction} onClick={() => toggleSort("age")} /> },
                { label: <SortHeader label="Seeders/Leechers" active={candidateSort?.key === "seeders"} direction={candidateSort?.direction} onClick={() => toggleSort("seeders")} /> },
                { label: <SortHeader label="Size" active={candidateSort?.key === "size"} direction={candidateSort?.direction} onClick={() => toggleSort("size")} /> },
                { label: "", width: LIST_TRACK.action, align: "end", mobile: true }
              ]}
              chevron={false}
            >
              {visibleResults.map((candidate) => (
                <ListRow key={`${candidate.indexerId}:${candidate.releaseName}`}>
                  <ListNameCell name={candidate.releaseName} sub={`${candidate.indexerName} · ${candidate.quality}`} />
                  {searchType === "basic" ? (
                    <ListCell primary={candidate.searchType === "tv" ? "TV" : "Movie"} />
                  ) : null}
                  <ListCell primary={formatProtocol(candidate.indexerProtocol)} mobile />
                  <ListCell primary={formatReleaseAge(candidate.releaseAgeHours)} />
                  <ListCell primary={formatSeedersAndLeechers(candidate.seeders, candidate.leechers)} />
                  <ListCell primary={candidate.sizeBytes ? formatBytes(candidate.sizeBytes) : "—"} />
                  <div role="cell" className="flex items-center justify-end gap-2">
                    <Chip tone={candidateTone(candidate)}>{candidateLabel(candidate)}</Chip>
                    <BlockReleaseAction
                      releaseName={candidate.releaseName}
                      indexerName={candidate.indexerName}
                      mediaType={(candidate.searchType ?? searchType) === "tv" ? "tv" : "movie"}
                    />
                    <Button type="button" size="sm" variant="outline" disabled={!candidate.downloadUrl} onClick={() => openGrab(candidate)}>
                      Grab
                    </Button>
                  </div>
                </ListRow>
              ))}
            </ListTable>
          )}
        </ListCard>
      ) : null}

      <Drawer
        open={grabTarget !== null}
        onOpenChange={(open) => { if (!open) setGrabTarget(null); }}
        title={grabTarget?.releaseName ?? "Grab"}
        description={grabTarget ? `${grabTarget.indexerName} · ${grabTarget.quality}` : undefined}
        footer={
          <DrawerFooter
            state={grabbing ? "saving" : "clean"}
            saveType="button"
            saveLabel="Send to download client"
            saveEnabled={!grabbing}
            onSave={() => void submitGrab()}
            onCancel={() => setGrabTarget(null)}
          />
        }
      >
        {grabTarget ? (
          <div className="grid gap-[var(--grid-gap)]">
            <DrawerFacts
              items={[
                { label: "Size", value: grabTarget.sizeBytes ? formatBytes(grabTarget.sizeBytes) : "—" },
                { label: "Seeders/Leechers", value: formatSeedersAndLeechers(grabTarget.seeders, grabTarget.leechers) },
                { label: "Protocol", value: formatProtocol(grabTarget.indexerProtocol) }
              ]}
            />
            <p className="text-[length:var(--type-body-sm)] text-muted-foreground">
              There is no title here to route through a library, so Deluno sends this the same way it would route a{" "}
              {grabTarget.searchType === "tv" || searchType === "tv" ? "TV" : "movie"} library's own grab: to whichever
              linked download client is set up to take it.
            </p>
          </div>
        ) : null}
      </Drawer>
    </div>
  );
}

/** The first message out of an ASP.NET ValidationProblem's `errors` map, when the grab was refused before it could be sent. */
function firstValidationMessage(body: { errors?: Record<string, string[]> } | null): string | null {
  const errors = body?.errors;
  if (!errors) return null;
  for (const key of Object.keys(errors)) {
    const message = errors[key]?.[0];
    if (message) return message;
  }
  return null;
}

function IndexerChecklist({
  indexers,
  selected,
  onChange
}: {
  indexers: IndexerItem[] | null;
  selected: Set<string>;
  onChange: (next: Set<string>) => void;
}) {
  if (indexers === null) {
    return <p className="text-[length:var(--type-body-sm)] text-muted-foreground">Loading indexers…</p>;
  }

  if (indexers.length === 0) {
    return <p className="text-[length:var(--type-body-sm)] text-muted-foreground">No indexers configured yet. Add one under Find &amp; Download.</p>;
  }

  return (
    <div className="grid gap-1.5">
      {indexers.filter((indexer) => indexer.isEnabled).map((indexer) => (
        <CheckboxRow
          key={indexer.id}
          label={indexer.name}
          checked={selected.has(indexer.id)}
          onCheckedChange={(checked) => {
            const next = new Set(selected);
            if (checked) next.add(indexer.id); else next.delete(indexer.id);
            onChange(next);
          }}
        />
      ))}
    </div>
  );
}

/** Newznab/Torznab categories for the chosen search type, kept to that type's own family (2000s for movies, 5000s for TV, both for basic) so the list never offers a category that search would just drop. */
function CategoryChecklist({
  categories,
  searchType,
  selected,
  onChange
}: {
  categories: SearchCategoryOption[] | null;
  searchType: SearchType;
  selected: Set<number>;
  onChange: (next: Set<number>) => void;
}) {
  if (categories === null) {
    return <p className="text-[length:var(--type-body-sm)] text-muted-foreground">Loading categories…</p>;
  }

  const relevant = categories.filter((category) => {
    const family = Math.floor(category.value / 1000);
    return searchType === "tv" ? family === 5 : searchType === "movie" ? family === 2 : family === 2 || family === 5;
  });

  if (relevant.length === 0) {
    return null;
  }

  return (
    <div className="grid max-h-40 gap-1.5 overflow-y-auto sm:grid-cols-2">
      {relevant.map((category) => (
        <CheckboxRow
          key={category.value}
          label={category.label}
          checked={selected.has(category.value)}
          onCheckedChange={(checked) => {
            const next = new Set(selected);
            if (checked) next.add(category.value); else next.delete(category.value);
            onChange(next);
          }}
        />
      ))}
    </div>
  );
}

/** A clickable column header, showing which column (if any) currently sorts the table and which way. */
function SortHeader({ label, active, direction, onClick }: { label: string; active: boolean; direction?: SortDirection; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={(event) => { event.stopPropagation(); onClick(); }}
      className="flex items-center gap-1 uppercase tracking-[0.1em] text-muted-foreground hover:text-foreground"
      aria-label={`Sort by ${label}`}
    >
      {label}
      <ArrowUpDown aria-hidden className={cn("h-3 w-3", active && (direction === "asc" ? "rotate-180" : ""))} />
    </button>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index += 1;
  }
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[index]}`;
}
