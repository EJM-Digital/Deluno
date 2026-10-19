import { Check, Film, LoaderCircle, Plus, RefreshCcw, Trash2 } from "lucide-react";
import { ArtworkImage } from "../components/ui/artwork-image";
import { useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { TagPicker } from "../components/app/tag-picker";
import { Button } from "../components/ui/button";
import { Card } from "../components/ui/card";
import { Chip } from "../components/ui/chip";
import { Checkbox } from "../components/ui/checkbox";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Input } from "../components/ui/input";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { fetchJson, readErrorText, type LibraryItem, type MovieCollectionItem, type MovieCollectionMemberItem, type QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";

interface CollectionsLoaderData {
  collections: MovieCollectionItem[];
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  membersByCollection: Record<string, MovieCollectionMemberItem[]>;
}

export async function collectionsLoader(): Promise<CollectionsLoaderData> {
  const [collections, libraries, qualityProfiles] = await Promise.all([
    fetchJson<MovieCollectionItem[]>("/api/movie-collections").catch(() => []),
    fetchJson<LibraryItem[]>("/api/libraries").catch(() => []),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles").catch((): QualityProfileItem[] => [])
  ]);
  const entries = await Promise.all(collections.map(async (collection) => [
    collection.id,
    await fetchJson<MovieCollectionMemberItem[]>(`/api/movie-collections/${encodeURIComponent(collection.id)}/members`).catch(() => [])
  ] as const));
  return {
    collections,
    libraries,
    qualityProfiles,
    membersByCollection: Object.fromEntries(entries)
  };
}

export function CollectionsPage() {
  const { collections, libraries, qualityProfiles, membersByCollection } = useLoaderData() as CollectionsLoaderData;
  const revalidator = useRevalidator();
  // Which films of a collection are held changes when any film does, and a collection sync reports as a change to films (#1179).
  const readAgain = useCoalescedRevalidate(() => revalidator.revalidate(), 500);
  useSignalREvent("MovieChanged", RealtimeGroups.Dashboard, readAgain);
  const movieLibraries = useMemo(() => libraries.filter((library) => library.mediaType === "movies"), [libraries]);
  const [addOpen, setAddOpen] = useState(false);
  const [providerId, setProviderId] = useState("");
  const [libraryId, setLibraryId] = useState(movieLibraries[0]?.id ?? "");
  const [monitored, setMonitored] = useState(true);
  const [monitorMovies, setMonitorMovies] = useState(true);
  const [searchOnAdd, setSearchOnAdd] = useState(true);
  const [tags, setTags] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [removing, setRemoving] = useState<MovieCollectionItem[] | null>(null);

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
        body: JSON.stringify({ providerId: providerId.trim(), libraryId, monitored, monitorMovies, searchOnAdd, tags: tags.trim() })
      });
      if (!response.ok) throw new Error(await readErrorText(response, "Collection could not be added."));
      setProviderId("");
      setTags("");
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

  async function setMemberExcluded(collection: MovieCollectionItem, member: MovieCollectionMemberItem, excluded: boolean) {
    if (busy) return;
    setBusy(`exclude:${collection.id}:${member.providerId}`);
    try {
      const response = await authedFetch(`/api/movie-collections/${encodeURIComponent(collection.id)}/members/${encodeURIComponent(member.providerId)}/exclusion`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ excluded })
      });
      if (!response.ok) throw new Error(excluded ? `${member.title} could not be excluded.` : `${member.title} could not be included again.`);
      toast.success(excluded ? `${member.title} is excluded. Deluno will not add or search for it.` : `${member.title} is included again.`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That film could not be changed.");
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

  async function removeCollections() {
    if (!removing || busy) return;
    const ids = removing.map((item) => item.id);
    setBusy("remove");
    try {
      const response = await authedFetch("/api/movie-collections/remove", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ collectionIds: ids })
      });
      if (!response.ok) throw new Error(await bulkErrorText(response, "Collections could not be removed."));
      toast.success(ids.length === 1 ? `Stopped following ${removing[0].name}. Its movies were left as they are.` : `Stopped following ${ids.length} collections. Their movies were left as they are.`);
      setSelected((current) => current.filter((id) => !ids.includes(id)));
      setRemoving(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Collections could not be removed.");
    } finally {
      setBusy(null);
    }
  }

  async function applyBulk(changes: BulkCollectionChanges) {
    if (!selected.length || busy) return;
    setBusy("bulk");
    try {
      const response = await authedFetch("/api/movie-collections/bulk", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ collectionIds: selected, ...changes })
      });
      if (!response.ok) throw new Error(await bulkErrorText(response));
      toast.success(`${selected.length} collection${selected.length === 1 ? "" : "s"} updated`);
      setSelected([]);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Collections could not be updated.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="space-y-(--page-gap)">
      <PageToolbar
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
          <div className="mt-5 grid gap-(--grid-gap) md:grid-cols-2">
            <label className="space-y-1.5 text-sm font-medium text-foreground">
              TMDb collection id
              <Input value={providerId} onChange={(event) => setProviderId(event.target.value)} placeholder="645" inputMode="numeric" autoFocus />
            </label>
            <label className="space-y-1.5 text-sm font-medium text-foreground">
              Movie library
              <Select value={libraryId} onChange={(event) => setLibraryId(event.target.value)} options={movieLibraries.map((library) => ({ value: library.id, label: library.name }))} placeholder="Choose a movie library" />
            </label>
          </div>
          <div className="mt-5 space-y-1.5">
            <span className="block text-sm font-medium text-foreground">Tags for every movie it adds (optional)</span>
            <TagPicker label="Tags for every movie the collection adds" value={tags} onChange={setTags} warnUnused={false} />
            <span className="block text-xs font-normal text-muted-foreground">Each movie this collection adds gets these tags straight away, so a destination rule, a download client or a notification can act on them.</span>
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
        <Card className="flex flex-col gap-(--grid-gap) px-4 py-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <span className="text-sm font-semibold text-foreground">{selected.length} selected</span>
            <div className="flex gap-2">
              <Button size="sm" variant="outline" onClick={() => setSelectedMonitoring(true)} disabled={busy === "bulk"}>Monitor</Button>
              <Button size="sm" variant="outline" onClick={() => setSelectedMonitoring(false)} disabled={busy === "bulk"}>Stop monitoring</Button>
              <Button size="sm" variant="outline" onClick={() => setRemoving(collections.filter((item) => selected.includes(item.id)))} disabled={busy !== null}>
                Stop following {selected.length} selected
              </Button>
            </div>
          </div>
          <CollectionBulkEditor
            key={selected.join(",")}
            count={selected.length}
            qualityProfiles={qualityProfiles.filter((profile) => profile.mediaType === "movies")}
            libraries={movieLibraries}
            disabled={busy === "bulk"}
            onApply={applyBulk}
          />
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
        <div className="grid gap-(--grid-gap) xl:grid-cols-2">
          {collections.map((collection) => {
            const members = membersByCollection[collection.id] ?? [];
            const isBusy = busy === collection.id;
            const isRefreshing = busy === `refresh:${collection.id}`;
            const excludedCount = members.filter((member) => member.isExcluded).length;
            const excludePrefix = `exclude:${collection.id}:`;
            return (
              <Card key={collection.id} className="flex min-h-[250px] flex-col">
                <div className="flex gap-(--grid-gap) p-4">
                  <Checkbox
                    aria-label={`Select ${collection.name}`}
                    checked={selected.includes(collection.id)}
                    onCheckedChange={(checked) => setSelected((current) => checked ? [...new Set([...current, collection.id])] : current.filter((id) => id !== collection.id))}
                    className="mt-1"
                  />
                  <div className="h-36 w-24 shrink-0 overflow-hidden rounded-xl bg-surface-2">
                    {collection.posterUrl ? <ArtworkImage src={collection.posterUrl} alt="" className="h-full w-full object-cover" /> : <Film className="m-auto mt-14 h-6 w-6 text-muted-foreground" />}
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
                      {excludedCount > 0 ? <span className="text-muted-foreground">{excludedCount} excluded</span> : null}
                      {collection.qualityProfileName ? <span className="text-muted-foreground">{collection.qualityProfileName}</span> : null}
                    </div>
                  </div>
                </div>

                <div className="border-t border-hairline px-4 py-3">
                  <p className="mb-2 text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Every member</p>
                  <CollectionMembers
                    members={members}
                    busyProviderId={busy?.startsWith(excludePrefix) ? busy.slice(excludePrefix.length) : null}
                    onSetExcluded={(member, excluded) => setMemberExcluded(collection, member, excluded)}
                  />
                </div>

                <CollectionTagsRow
                  key={`${collection.id}:${collection.tags}`}
                  collection={collection}
                  disabled={isBusy || isRefreshing}
                  onSave={(value) => updateCollection(collection, { tags: value })}
                />

                <div className="mt-auto flex flex-wrap items-center justify-between gap-3 border-t border-hairline px-4 py-3">
                  <div className="flex items-center gap-(--grid-gap) text-xs text-muted-foreground">
                    <SwitchRow label="Monitor" checked={collection.monitored} onCheckedChange={(value) => updateCollection(collection, { monitored: value })} disabled={isBusy || isRefreshing} />
                    <SwitchRow label="Add missing" checked={collection.monitorMovies} onCheckedChange={(value) => updateCollection(collection, { monitorMovies: value })} disabled={isBusy || isRefreshing} />
                  </div>
                  <div className="flex gap-2">
                    <Button size="sm" variant="ghost" onClick={() => setRemoving([collection])} disabled={isBusy || isRefreshing}>
                      <Trash2 className="h-3.5 w-3.5" />Stop following<span className="sr-only"> {collection.name}</span>
                    </Button>
                    <Button size="sm" variant="outline" onClick={() => refreshCollection(collection)} disabled={isBusy || isRefreshing}>
                      {isRefreshing ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <RefreshCcw className="h-3.5 w-3.5" />}Refresh
                    </Button>
                  </div>
                </div>
                {collection.lastSyncError ? <p className="border-t border-destructive/20 bg-destructive/5 px-4 py-2 text-xs text-destructive">Last refresh: {collection.lastSyncError}</p> : null}
              </Card>
            );
          })}
        </div>
      )}

      <RemoveCollectionsConfirm
        collections={removing}
        busy={busy === "remove"}
        onCancel={() => setRemoving(null)}
        onConfirm={removeCollections}
      />
    </div>
  );
}

/**
 * Every film in a followed collection, one compact row each. A film can be
 * excluded so Deluno never adds or searches for it; an excluded film stays in
 * the list, muted, with a way back.
 */
export function CollectionMembers({ members, busyProviderId, onSetExcluded }: {
  members: MovieCollectionMemberItem[];
  busyProviderId: string | null;
  onSetExcluded: (member: MovieCollectionMemberItem, excluded: boolean) => void;
}) {
  if (members.length === 0) {
    return <p className="text-sm text-muted-foreground">No films listed yet. Refresh the collection to read its members.</p>;
  }
  return (
    <ul className="grid max-h-60 gap-x-4 gap-y-1.5 overflow-y-auto pr-1 sm:grid-cols-2">
      {members.map((member) => {
        const label = `${member.title}${member.releaseYear ? ` (${member.releaseYear})` : ""}`;
        const href = member.localMovieId ? `/movies/${member.localMovieId}` : member.externalUrl ?? undefined;
        const rowBusy = busyProviderId === member.providerId;
        return (
          <li key={member.providerId} data-excluded={member.isExcluded ? "true" : undefined} className={cn("flex min-w-0 items-center gap-2", member.isExcluded && "opacity-60")}>
            <a href={href} target={member.localMovieId ? undefined : "_blank"} rel={member.localMovieId ? undefined : "noreferrer"} tabIndex={-1} aria-hidden className={cn("relative h-12 w-8 shrink-0 overflow-hidden rounded-md bg-surface-2", (!member.localMovieId || member.isExcluded) && "saturate-50")}>
              {member.posterUrl ? <ArtworkImage src={member.posterUrl} alt="" className="h-full w-full object-cover" /> : <Film className="m-auto mt-4 h-4 w-4 text-muted-foreground" />}
              {member.localMovieId ? <span className="absolute bottom-0.5 right-0.5 rounded-full bg-success p-0.5 text-white"><Check className="h-2.5 w-2.5" /></span> : null}
            </a>
            <div className="min-w-0 flex-1">
              <a href={href} target={member.localMovieId ? undefined : "_blank"} rel={member.localMovieId ? undefined : "noreferrer"} className={cn("block truncate text-sm font-medium text-foreground hover:underline", member.isExcluded && "text-muted-foreground")} title={label}>{label}</a>
              <span className="text-xs text-muted-foreground">{member.localMovieId ? "In your library" : "Not in your library"}</span>
            </div>
            {member.isExcluded ? <Chip tone="idle" dot={false}>Excluded</Chip> : null}
            <Button size="sm" variant="ghost" disabled={rowBusy} aria-label={`${member.isExcluded ? "Include again" : "Exclude"} ${label}`} onClick={() => onSetExcluded(member, !member.isExcluded)}>
              {rowBusy ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
              {member.isExcluded ? "Include again" : "Exclude"}
            </Button>
          </li>
        );
      })}
    </ul>
  );
}

/** #1056: the plain confirmation before a collection is unfollowed. Movies are never touched. */
export function RemoveCollectionsConfirm({ collections, busy, onCancel, onConfirm }: {
  collections: MovieCollectionItem[] | null;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const names = collections?.map((item) => item.name) ?? [];
  const title = names.length === 1 ? `Stop following ${names[0]}?` : `Stop following ${names.length} collections?`;
  return (
    <ConfirmDialog
      open={collections !== null && collections.length > 0}
      onOpenChange={(open) => { if (!open) onCancel(); }}
      title={title}
      description={`${names.length === 1 ? "Deluno will stop tracking this collection." : `Deluno will stop tracking ${names.join(", ")}.`} Your movies stay exactly as they are: none is deleted, unmonitored or changed, and any tags they already have are kept. You can follow the collection again later.`}
      confirmLabel="Stop following"
      confirmVariant="destructive"
      busy={busy}
      onConfirm={onConfirm}
    />
  );
}

/** #1046: the tags a collection gives to every movie it adds. Blank gives none. */
export function CollectionTagsRow({ collection, disabled, onSave }: { collection: MovieCollectionItem; disabled: boolean; onSave: (tags: string) => void }) {
  const [value, setValue] = useState(collection.tags ?? "");
  const changed = value.trim() !== (collection.tags ?? "").trim();
  return (
    <div className="flex flex-wrap items-center gap-2 border-t border-hairline px-4 py-3">
      <div className="flex min-w-0 flex-1 items-center gap-2">
        <span className="shrink-0 text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Tags for new movies</span>
        <div className="min-w-0 flex-1">
          <TagPicker label={`Tags ${collection.name} gives to movies it adds`} value={value} onChange={setValue} disabled={disabled} warnUnused={false} />
        </div>
      </div>
      {changed ? (
        <Button size="sm" variant="outline" onClick={() => onSave(value.trim())} disabled={disabled}>
          Save tags for {collection.name}
        </Button>
      ) : null}
    </div>
  );
}

/** What the bulk editor sends: only the fields somebody chose. */
export interface BulkCollectionChanges {
  monitorMovies?: boolean;
  qualityProfileId?: string;
  minimumAvailability?: string;
  libraryId?: string;
  searchOnAdd?: boolean;
}

const YES_NO_OPTIONS = [{ value: "yes", label: "Yes" }, { value: "no", label: "No" }];
const AVAILABILITY_OPTIONS = [
  { value: "announced", label: "As soon as it is announced" },
  { value: "inCinemas", label: "When it is in cinemas" },
  { value: "released", label: "When it is released" }
];

async function bulkErrorText(response: Response, fallback = "Collections could not be updated."): Promise<string> {
  try {
    const body = await response.json() as { errors?: Record<string, string[]> };
    const first = Object.values(body.errors ?? {})[0]?.[0];
    if (first) return first;
  } catch {
    // fall through to the plain sentence
  }
  return fallback;
}

/**
 * #998: Radarr's collection editor, for the collections ticked on the page. Every
 * field starts at "Leave as it is"; only what somebody chooses is sent.
 */
export function CollectionBulkEditor({ count, qualityProfiles, libraries, disabled, onApply }: {
  count: number;
  qualityProfiles: QualityProfileItem[];
  libraries: LibraryItem[];
  disabled: boolean;
  onApply: (changes: BulkCollectionChanges) => void;
}) {
  const [profile, setProfile] = useState("");
  const [availability, setAvailability] = useState("");
  const [folder, setFolder] = useState("");
  const [addMissing, setAddMissing] = useState("");
  const [searchOnAdd, setSearchOnAdd] = useState("");
  const anything = Boolean(profile || availability || folder || addMissing || searchOnAdd);

  function apply() {
    const changes: BulkCollectionChanges = {};
    if (profile) changes.qualityProfileId = profile;
    if (availability) changes.minimumAvailability = availability;
    if (folder) changes.libraryId = folder;
    if (addMissing) changes.monitorMovies = addMissing === "yes";
    if (searchOnAdd) changes.searchOnAdd = searchOnAdd === "yes";
    onApply(changes);
  }

  return (
    <div className="space-y-3 border-t border-hairline pt-3">
      <div className="grid gap-(--grid-gap) md:grid-cols-3 xl:grid-cols-5">
        <label className="space-y-1.5 text-sm font-medium text-foreground">
          Quality profile
          <Select value={profile} onChange={(event) => setProfile(event.target.value)} placeholder="Leave as it is" options={qualityProfiles.map((item) => ({ value: item.id, label: item.name }))} />
        </label>
        <label className="space-y-1.5 text-sm font-medium text-foreground">
          Look for movies
          <Select value={availability} onChange={(event) => setAvailability(event.target.value)} placeholder="Leave as it is" options={AVAILABILITY_OPTIONS} />
        </label>
        <label className="space-y-1.5 text-sm font-medium text-foreground">
          Movie folder
          <Select value={folder} onChange={(event) => setFolder(event.target.value)} placeholder="Leave as it is" options={libraries.map((item) => ({ value: item.id, label: `${item.name} (${item.rootPath})` }))} />
        </label>
        <label className="space-y-1.5 text-sm font-medium text-foreground">
          Add missing movies
          <Select value={addMissing} onChange={(event) => setAddMissing(event.target.value)} placeholder="Leave as it is" options={YES_NO_OPTIONS} />
        </label>
        <label className="space-y-1.5 text-sm font-medium text-foreground">
          Search when movies are added
          <Select value={searchOnAdd} onChange={(event) => setSearchOnAdd(event.target.value)} placeholder="Leave as it is" options={YES_NO_OPTIONS} />
        </label>
      </div>
      <div className="flex justify-end">
        <Button size="sm" onClick={apply} disabled={disabled || !anything}>
          Apply to {count} collection{count === 1 ? "" : "s"}
        </Button>
      </div>
    </div>
  );
}
