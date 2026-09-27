/**
 * Search (#753) — a free-text query fanned out to every enabled indexer,
 * no title required. Prowlarr's own Search screen: type a query, pick a
 * type, choose which indexers to ask, get back one combined, sortable
 * results table with the same enriched columns per-title interactive
 * search results show.
 *
 *   Query box · search-type picker · indexer checklist
 *   ListTable (sortable, filterable) -> Grab drawer (client picker)
 *
 * Contracts: POST /api/search, /api/search/grab. Grabbing routes through the
 * same decision (IAcquisitionDecisionPipeline) and dispatch
 * (IDownloadClientGrabService) machinery a per-title grab uses; there is no
 * library to route a download client through for a title-less search, so the
 * client is chosen directly here.
 */
import { useEffect, useState } from "react";
import { ArrowUpDown, Loader2, Search as SearchIcon } from "lucide-react";
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
import { fetchJson, type DownloadClientItem, type IndexerItem } from "../lib/api";
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
}

type SearchType = "movie" | "tv";

export function SearchPage() {
  const [query, setQuery] = useState("");
  const [searchType, setSearchType] = useState<SearchType>("movie");
  const [indexers, setIndexers] = useState<IndexerItem[] | null>(null);
  const [selectedIndexerIds, setSelectedIndexerIds] = useState<Set<string>>(new Set());
  const [searching, setSearching] = useState(false);
  const [results, setResults] = useState<ManualSearchCandidate[] | null>(null);
  const [candidateSort, setCandidateSort] = useState<{ key: CandidateSortKey; direction: SortDirection } | null>(null);
  const [protocolFilter, setProtocolFilter] = useState("");
  const [indexerFilter, setIndexerFilter] = useState("");
  const [grabTarget, setGrabTarget] = useState<ManualSearchCandidate | null>(null);
  const [downloadClients, setDownloadClients] = useState<DownloadClientItem[] | null>(null);
  const [chosenClientId, setChosenClientId] = useState("");
  const [grabbing, setGrabbing] = useState(false);

  useEffect(() => {
    let active = true;
    void fetchJson<IndexerItem[]>("/api/indexers")
      .then((list) => { if (active) setIndexers(list); })
      .catch(() => { if (active) setIndexers([]); });
    return () => { active = false; };
  }, []);

  async function ensureDownloadClients() {
    if (downloadClients) return downloadClients;
    const list = await fetchJson<DownloadClientItem[]>("/api/download-clients").catch(() => []);
    setDownloadClients(list);
    return list;
  }

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
          indexerIds: selectedIndexerIds.size > 0 ? [...selectedIndexerIds] : null
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

  async function openGrab(candidate: ManualSearchCandidate) {
    setGrabTarget(candidate);
    setChosenClientId("");
    await ensureDownloadClients();
  }

  async function submitGrab() {
    if (!grabTarget || !chosenClientId) return;
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
          downloadClientId: chosenClientId,
          searchType
        })
      });
      if (!response.ok) throw new Error("That release could not be sent.");
      const body = (await response.json()) as { succeeded: boolean; message: string };
      if (body.succeeded) {
        toast.success(`${grabTarget.releaseName} sent to the download client.`);
        setGrabTarget(null);
      } else {
        toast.error(body.message || "That release could not be sent.");
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
              onValueChange={setSearchType}
              options={[{ value: "movie", label: "Movie" }, { value: "tv", label: "TV" }]}
            />
          </Field>
          <Field label="Indexers" optional help="Leave every box unchecked to search every enabled indexer that covers this type.">
            <IndexerChecklist
              indexers={indexers}
              selected={selectedIndexerIds}
              onChange={setSelectedIndexerIds}
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
                  <ListCell primary={formatProtocol(candidate.indexerProtocol)} mobile />
                  <ListCell primary={formatReleaseAge(candidate.releaseAgeHours)} />
                  <ListCell primary={formatSeedersAndLeechers(candidate.seeders, candidate.leechers)} />
                  <ListCell primary={candidate.sizeBytes ? formatBytes(candidate.sizeBytes) : "—"} />
                  <div role="cell" className="flex items-center justify-end gap-2">
                    <Chip tone={candidateTone(candidate)}>{candidateLabel(candidate)}</Chip>
                    <Button type="button" size="sm" variant="outline" disabled={!candidate.downloadUrl} onClick={() => void openGrab(candidate)}>
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
            saveEnabled={Boolean(chosenClientId) && !grabbing}
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
            <Field label="Download client" help="A title-less search has no library to route a client through, so pick one directly.">
              <Select
                value={chosenClientId}
                onChange={(event) => setChosenClientId(event.target.value)}
                options={[{ value: "", label: "Choose a download client" }, ...(downloadClients ?? []).map((client) => ({ value: client.id, label: client.name }))]}
                aria-label="Download client"
              />
            </Field>
          </div>
        ) : null}
      </Drawer>
    </div>
  );
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
