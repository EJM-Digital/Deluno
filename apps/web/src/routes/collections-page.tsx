import { Check, Film, LoaderCircle, Plus, RefreshCcw } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Button } from "../components/ui/button";
import { Card } from "../components/ui/card";
import { Checkbox } from "../components/ui/checkbox";
import { Input } from "../components/ui/input";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { fetchJson, type LibraryItem, type MovieCollectionItem, type MovieCollectionMemberItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";

interface CollectionsLoaderData {
  collections: MovieCollectionItem[];
  libraries: LibraryItem[];
  membersByCollection: Record<string, MovieCollectionMemberItem[]>;
}

export async function collectionsLoader(): Promise<CollectionsLoaderData> {
  const [collections, libraries] = await Promise.all([
    fetchJson<MovieCollectionItem[]>("/api/movie-collections").catch(() => []),
    fetchJson<LibraryItem[]>("/api/libraries").catch(() => [])
  ]);
  const entries = await Promise.all(collections.map(async (collection) => [
    collection.id,
    await fetchJson<MovieCollectionMemberItem[]>(`/api/movie-collections/${encodeURIComponent(collection.id)}/members`).catch(() => [])
  ] as const));
  return {
    collections,
    libraries,
    membersByCollection: Object.fromEntries(entries)
  };
}

export function CollectionsPage() {
  const { collections, libraries, membersByCollection } = useLoaderData() as CollectionsLoaderData;
  const revalidator = useRevalidator();
  const movieLibraries = useMemo(() => libraries.filter((library) => library.mediaType === "movies"), [libraries]);
  const [addOpen, setAddOpen] = useState(false);
  const [providerId, setProviderId] = useState("");
  const [libraryId, setLibraryId] = useState(movieLibraries[0]?.id ?? "");
  const [monitored, setMonitored] = useState(true);
  const [monitorMovies, setMonitorMovies] = useState(true);
  const [searchOnAdd, setSearchOnAdd] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[]>([]);

  const missing = collections.reduce((total, collection) => total + collection.missingCount, 0);
  const monitoredCount = collections.filter((collection) => collection.monitored).length;
  const held = collections.reduce((total, collection) => total + collection.heldCount, 0);

  async function addCollection(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!providerId.trim() || !libraryId || busy) return;
    setBusy("add");
    try {
      const response = await authedFetch("/api/movie-collections", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ providerId: providerId.trim(), libraryId, monitored, monitorMovies, searchOnAdd })
      });
      if (!response.ok) throw new Error(await response.text() || "Collection could not be added.");
      setProviderId("");
      setAddOpen(false);
      toast.success("Collection added");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Collection could not be added.");
    } finally {
      setBusy(null);
    }
  }

  async function updateCollection(collection: MovieCollectionItem, patch: Record<string, unknown>) {
    setBusy(collection.id);
    try {
      const response = await authedFetch(`/api/movie-collections/${encodeURIComponent(collection.id)}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(patch)
      });
      if (!response.ok) throw new Error("Collection settings could not be saved.");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Collection settings could not be saved.");
    } finally {
      setBusy(null);
    }
  }

  async function refreshCollection(collection: MovieCollectionItem) {
    setBusy(`refresh:${collection.id}`);
    try {
      const response = await authedFetch(`/api/movie-collections/${encodeURIComponent(collection.id)}/refresh`, { method: "POST" });
      if (!response.ok) throw new Error("Collection could not be refreshed.");
      toast.success(`${collection.name} refreshed`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Collection could not be refreshed.");
    } finally {
      setBusy(null);
    }
  }

  async function setSelectedMonitoring(value: boolean) {
    if (!selected.length || busy) return;
    setBusy("bulk");
    try {
      await Promise.all(selected.map(async (id) => {
        const response = await authedFetch(`/api/movie-collections/${encodeURIComponent(id)}`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ monitored: value })
        });
        if (!response.ok) throw new Error("One or more collections could not be updated.");
      }));
      setSelected([]);
      toast.success(`${selected.length} collection${selected.length === 1 ? "" : "s"} updated`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Collections could not be updated.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <PageToolbar
        left={<span className="text-sm font-semibold text-foreground">Movie collections</span>}
        actions={<PageToolbarAction onClick={() => setAddOpen((value) => !value)}>Follow collection</PageToolbarAction>}
      />

      <SummaryStrip cells={[
        { label: "Collections", value: collections.length, help: "Franchises you follow" },
        { label: "Monitored", value: monitoredCount, help: "Checked by automation" },
        { label: "Missing members", value: missing, help: "Provider titles not held", tone: missing ? "warning" : "success" },
        { label: "Held members", value: held, help: "Titles already in a library" }
      ]} />

      {addOpen ? (
        <Card as="form" onSubmit={addCollection} className="p-5">
          <div className="flex flex-col gap-1">
            <h2 className="font-display text-base font-semibold text-foreground">Follow a TMDb collection</h2>
            <p className="text-sm text-muted-foreground">Paste the numeric id from a TMDb collection URL. Deluno will show every member, then the normal movie automation cycle can add new ones.</p>
          </div>
          <div className="mt-5 grid gap-[var(--grid-gap)] md:grid-cols-2">
            <label className="space-y-1.5 text-sm font-medium text-foreground">
              TMDb collection id
              <Input value={providerId} onChange={(event) => setProviderId(event.target.value)} placeholder="645" inputMode="numeric" autoFocus />
            </label>
            <label className="space-y-1.5 text-sm font-medium text-foreground">
              Movie library
              <Select value={libraryId} onChange={(event) => setLibraryId(event.target.value)} options={movieLibraries.map((library) => ({ value: library.id, label: library.name }))} placeholder="Choose a movie library" />
            </label>
          </div>
          <div className="mt-5 grid gap-3 md:grid-cols-3">
            <SwitchRow label="Monitor collection" description="Discover new franchise members." checked={monitored} onCheckedChange={setMonitored} />
            <SwitchRow label="Monitor movies" description="Add missing members as wanted movies." checked={monitorMovies} onCheckedChange={setMonitorMovies} />
            <SwitchRow label="Search on add" description="Request the library's existing search cycle." checked={searchOnAdd} onCheckedChange={setSearchOnAdd} />
          </div>
          <div className="mt-5 flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => setAddOpen(false)}>Cancel</Button>
            <Button type="submit" disabled={busy === "add" || !libraryId}>{busy === "add" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />}Add collection</Button>
          </div>
        </Card>
      ) : null}

      {selected.length > 0 ? (
        <Card className="flex flex-wrap items-center justify-between gap-3 px-4 py-3">
          <span className="text-sm font-semibold text-foreground">{selected.length} selected</span>
          <div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => setSelectedMonitoring(true)} disabled={busy === "bulk"}>Monitor</Button>
            <Button size="sm" variant="outline" onClick={() => setSelectedMonitoring(false)} disabled={busy === "bulk"}>Stop monitoring</Button>
          </div>
        </Card>
      ) : null}

      {collections.length === 0 ? (
        <Card className="flex flex-col items-center justify-center px-6 py-16 text-center">
          <Film className="h-8 w-8 text-muted-foreground" />
          <h2 className="mt-4 font-display text-lg font-semibold text-foreground">No collections yet</h2>
          <p className="mt-1 max-w-md text-sm text-muted-foreground">Follow a TMDb franchise to see its full membership and let Deluno keep the library current as sequels appear.</p>
          <Button className="mt-5" onClick={() => setAddOpen(true)}><Plus className="h-4 w-4" />Follow collection</Button>
        </Card>
      ) : (
        <div className="grid gap-[var(--grid-gap)] xl:grid-cols-2">
          {collections.map((collection) => {
            const members = membersByCollection[collection.id] ?? [];
            const isBusy = busy === collection.id;
            const isRefreshing = busy === `refresh:${collection.id}`;
            return (
              <Card key={collection.id} className="flex min-h-[250px] flex-col">
                <div className="flex gap-[var(--grid-gap)] p-4">
                  <Checkbox
                    aria-label={`Select ${collection.name}`}
                    checked={selected.includes(collection.id)}
                    onCheckedChange={(checked) => setSelected((current) => checked ? [...new Set([...current, collection.id])] : current.filter((id) => id !== collection.id))}
                    className="mt-1"
                  />
                  <div className="h-36 w-24 shrink-0 overflow-hidden rounded-xl bg-surface-2">
                    {collection.posterUrl ? <img src={collection.posterUrl} alt="" className="h-full w-full object-cover" /> : <Film className="m-auto mt-14 h-6 w-6 text-muted-foreground" />}
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <div>
                        <h2 className="font-display text-lg font-semibold text-foreground">{collection.name}</h2>
                        <p className="mt-0.5 text-xs font-medium uppercase tracking-[0.12em] text-muted-foreground">{collection.libraryName} · TMDb {collection.providerId}</p>
                      </div>
                      <span className={cn("rounded-full border px-2 py-1 text-xs font-semibold", collection.monitored ? "border-success/30 bg-success/10 text-success" : "border-hairline bg-surface-2 text-muted-foreground")}>
                        {collection.monitored ? "Monitoring" : "Paused"}
                      </span>
                    </div>
                    <p className="mt-3 line-clamp-3 text-sm leading-relaxed text-muted-foreground">{collection.overview || "No synopsis supplied by TMDb."}</p>
                    <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-sm">
                      <span className={collection.missingCount ? "font-semibold text-warning" : "font-semibold text-success"}>{collection.missingCount} missing</span>
                      <span className="text-muted-foreground">{collection.heldCount}/{collection.memberCount} held</span>
                      {collection.qualityProfileName ? <span className="text-muted-foreground">{collection.qualityProfileName}</span> : null}
                    </div>
                  </div>
                </div>

                <div className="border-t border-hairline px-4 py-3">
                  <p className="mb-2 text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Every member</p>
                  <div className="flex flex-wrap gap-2">
                    {members.map((member) => (
                      <a key={member.providerId} href={member.localMovieId ? `/movies/${member.localMovieId}` : member.externalUrl ?? undefined} target={member.localMovieId ? undefined : "_blank"} rel={member.localMovieId ? undefined : "noreferrer"} title={`${member.title}${member.releaseYear ? ` (${member.releaseYear})` : ""}`} className={cn("group relative h-14 w-10 overflow-hidden rounded-md bg-surface-2 ring-offset-2 transition hover:ring-2 hover:ring-primary", !member.localMovieId && "opacity-55 saturate-50") }>
                        {member.posterUrl ? <img src={member.posterUrl} alt={member.title} className="h-full w-full object-cover" /> : <Film className="m-auto mt-5 h-4 w-4 text-muted-foreground" />}
                        {member.localMovieId ? <span className="absolute bottom-0.5 right-0.5 rounded-full bg-success p-0.5 text-white"><Check className="h-2.5 w-2.5" /></span> : null}
                      </a>
                    ))}
                  </div>
                </div>

                <div className="mt-auto flex flex-wrap items-center justify-between gap-3 border-t border-hairline px-4 py-3">
                  <div className="flex items-center gap-[var(--grid-gap)] text-xs text-muted-foreground">
                    <SwitchRow label="Monitor" checked={collection.monitored} onCheckedChange={(value) => updateCollection(collection, { monitored: value })} disabled={isBusy || isRefreshing} />
                    <SwitchRow label="Add missing" checked={collection.monitorMovies} onCheckedChange={(value) => updateCollection(collection, { monitorMovies: value })} disabled={isBusy || isRefreshing} />
                  </div>
                  <Button size="sm" variant="outline" onClick={() => refreshCollection(collection)} disabled={isBusy || isRefreshing}>
                    {isRefreshing ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <RefreshCcw className="h-3.5 w-3.5" />}Refresh
                  </Button>
                </div>
                {collection.lastSyncError ? <p className="border-t border-destructive/20 bg-destructive/5 px-4 py-2 text-xs text-destructive">Last refresh: {collection.lastSyncError}</p> : null}
              </Card>
            );
          })}
        </div>
      )}
    </div>
  );
}
