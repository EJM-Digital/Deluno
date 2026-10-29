import { Film, LoaderCircle, Plus, Sparkles } from "lucide-react";
import { ArtworkImage } from "../ui/artwork-image";
import { Button } from "../ui/button";
import { Card } from "../ui/card";
import { Chip } from "../ui/chip";
import { Select } from "../ui/select";
import type { LibraryCollectionItem, LibraryCollectionMember, LibraryItem } from "../../lib/api";
import { cn } from "../../lib/utils";

/** What a member's status says on screen, in the Chip's own tones. */
export const MEMBER_STATUS: Record<LibraryCollectionMember["status"], { label: string; tone: "ok" | "info" | "warn" }> = {
  held: { label: "Held", tone: "ok" },
  wanted: { label: "Wanted", tone: "info" },
  missing: { label: "Missing", tone: "warn" }
};

/** The one action on a collection: how many films it adds, in words. */
export function addMissingLabel(collection: Pick<LibraryCollectionItem, "missingCount">): string {
  return `Add ${collection.missingCount === 1 ? "the missing film" : `${collection.missingCount} missing films`}`;
}

/**
 * Collections the way Radarr shows them (#1425): the Recommended collections to complete, each with why, then every collection
 * a film in the library belongs to with each film held, wanted or missing, and one action that adds the missing ones.
 */
export function LibraryCollections({
  recommended,
  collections,
  libraries,
  libraryId,
  onLibraryChange,
  busyProviderId,
  onAddMissing
}: {
  recommended: LibraryCollectionItem[];
  collections: LibraryCollectionItem[];
  libraries: LibraryItem[];
  libraryId: string;
  onLibraryChange: (libraryId: string) => void;
  busyProviderId: string | null;
  onAddMissing: (collection: LibraryCollectionItem) => void;
}) {
  if (collections.length === 0) return null;
  return (
    <div className="grid gap-(--page-gap)">
      {libraries.length > 1 ? (
        <label className="flex flex-wrap items-center gap-2 text-sm font-medium text-foreground">
          Add missing films to
          <Select aria-label="Library for the missing films" className="w-72" value={libraryId} onChange={(event) => onLibraryChange(event.target.value)} options={libraries.map((library) => ({ value: library.id, label: library.name }))} />
        </label>
      ) : null}

      {recommended.length > 0 ? (
        <section aria-labelledby="recommended-collections" className="grid gap-(--grid-gap)">
          <h2 id="recommended-collections" className="flex items-center gap-2 font-display text-base font-semibold text-foreground">
            <Sparkles className="h-4 w-4 text-muted-foreground" /> Recommended
            <span className="text-sm font-normal text-muted-foreground">Collections your films belong to that are worth completing</span>
          </h2>
          <div className="grid gap-(--grid-gap) lg:grid-cols-2 2xl:grid-cols-3">
            {recommended.map((collection) => (
              <Card key={collection.providerId} className="flex gap-(--grid-gap) p-4">
                <CollectionPoster collection={collection} className="h-24 w-16" />
                <div className="flex min-w-0 flex-1 flex-col gap-2">
                  <h3 className="truncate font-display text-base font-semibold text-foreground" title={collection.name}>{collection.name}</h3>
                  <p className="text-sm text-muted-foreground">{collection.why}</p>
                  <AddMissingButton collection={collection} busyProviderId={busyProviderId} disabled={libraries.length === 0} onAddMissing={onAddMissing} className="mt-auto self-start" />
                </div>
              </Card>
            ))}
          </div>
        </section>
      ) : null}

      <section aria-labelledby="library-collections" className="grid gap-(--grid-gap)">
        <h2 id="library-collections" className="font-display text-base font-semibold text-foreground">
          Your collections
          <span className="ml-2 text-sm font-normal text-muted-foreground">Every collection a film in your library belongs to</span>
        </h2>
        <div className="grid gap-(--grid-gap) xl:grid-cols-2">
          {collections.map((collection) => (
            <Card key={collection.providerId} className="flex flex-col">
              <div className="flex gap-(--grid-gap) p-4">
                <CollectionPoster collection={collection} className="h-28 w-20" />
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <h3 className="font-display text-lg font-semibold text-foreground">{collection.name}</h3>
                    {collection.following ? <Chip tone="ok">Following</Chip> : <Chip tone="idle" dot={false}>Not followed</Chip>}
                  </div>
                  <p className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-sm">
                    <span className="text-success">{collection.heldCount} held</span>
                    {collection.wantedCount > 0 ? <span className="text-info">{collection.wantedCount} wanted</span> : null}
                    {collection.membersKnown ? <span className={collection.missingCount ? "font-semibold text-warning" : "text-muted-foreground"}>{collection.missingCount} missing</span> : null}
                  </p>
                  {collection.membersKnown ? null : (
                    <p className="mt-2 text-sm text-muted-foreground">The list of this collection&apos;s films could not be read just now, so only the films you hold are shown.</p>
                  )}
                  {collection.membersKnown && collection.missingCount > 0 ? (
                    <AddMissingButton collection={collection} busyProviderId={busyProviderId} disabled={libraries.length === 0} onAddMissing={onAddMissing} className="mt-3" />
                  ) : null}
                </div>
              </div>
              <ul className="grid max-h-60 gap-x-4 gap-y-1.5 overflow-y-auto border-t border-hairline px-4 py-3 pr-3 sm:grid-cols-2" aria-label={`Films of ${collection.name}`}>
                {collection.members.map((member) => {
                  const label = `${member.title}${member.releaseYear ? ` (${member.releaseYear})` : ""}`;
                  const status = member.isExcluded ? null : MEMBER_STATUS[member.status];
                  return (
                    <li key={member.providerId} data-status={member.isExcluded ? "excluded" : member.status} className="flex min-w-0 items-center justify-between gap-2">
                      {member.localMovieId ? (
                        <a href={`/movies/${member.localMovieId}`} className="truncate text-sm font-medium text-foreground hover:underline" title={label}>{label}</a>
                      ) : (
                        <span className="truncate text-sm text-muted-foreground" title={label}>{label}</span>
                      )}
                      {status ? <Chip tone={status.tone} dot={false}>{status.label}</Chip> : <Chip tone="idle" dot={false}>Excluded</Chip>}
                    </li>
                  );
                })}
              </ul>
            </Card>
          ))}
        </div>
      </section>
    </div>
  );
}

function CollectionPoster({ collection, className }: { collection: LibraryCollectionItem; className: string }) {
  return (
    <div className={cn("shrink-0 overflow-hidden rounded-xl bg-surface-2", className)}>
      {collection.posterUrl ? <ArtworkImage src={collection.posterUrl} alt="" className="h-full w-full object-cover" /> : <Film className="m-auto mt-8 h-5 w-5 text-muted-foreground" />}
    </div>
  );
}

function AddMissingButton({
  collection,
  busyProviderId,
  disabled,
  onAddMissing,
  className
}: {
  collection: LibraryCollectionItem;
  busyProviderId: string | null;
  disabled: boolean;
  onAddMissing: (collection: LibraryCollectionItem) => void;
  className?: string;
}) {
  const busy = busyProviderId === collection.providerId;
  return (
    <Button
      type="button"
      size="sm"
      variant="outline"
      className={className}
      disabled={busy || busyProviderId !== null || disabled}
      aria-label={`${addMissingLabel(collection)} of ${collection.name}`}
      onClick={() => onAddMissing(collection)}
    >
      {busy ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
      {addMissingLabel(collection)}
    </Button>
  );
}
