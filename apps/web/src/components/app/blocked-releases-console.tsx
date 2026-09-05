/**
 * The blocklist — every release Deluno has refused, and the way to change its
 * mind.
 *
 * <p>DESIGN-007 decisions 1 and 2 chose permanent refusals: a download that
 * turns out to be junk means that exact copy is refused, and the refusal lasts
 * until somebody clears it. That is only a safe choice because nothing is
 * hidden. This screen is that safety, and without it the design is Radarr's
 * blocklist with the same complaint attached — a title stops arriving and the
 * reason sits somewhere nobody can see.</p>
 *
 * <p>So the reason is shown on every row, in the words the import used, and
 * un-refusing is one click. Un-refusing starts no search: James was explicit
 * that clearing in bulk must not become a storm, and that somebody clearing a
 * single title will search for it themselves.</p>
 *
 * Contract: GET /api/blocked-releases, DELETE /api/blocked-releases/{id}.
 */
import { useState } from "react";
import { RotateCcw } from "lucide-react";
import type { BlockedRelease } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { LIST_TRACK, ListCard, ListEmpty, ListNameCell, ListRow, ListTable } from "../ui/list-card";

export interface BlockedReleasesConsoleProps {
  releases: BlockedRelease[];
  onChanged: () => void;
}

/**
 * The failure code, said the way a person would say it.
 *
 * <p>The import records `noVideoStream`; a screen that prints that is asking
 * the reader to learn Deluno's vocabulary to find out why their film never
 * arrived. Anything unrecognised falls back to the code itself rather than to
 * "unknown", because a code you can search for beats a word that tells you
 * nothing.</p>
 */
const REASON_WORDS: Record<string, string> = {
  noVideoStream: "No video in the file",
  likelySample: "A sample, not the film",
  unsupportedFile: "Not a media file Deluno accepts",
  mediaProbeRejected: "Unreadable — corrupt or encrypted",
  mediaProbeUnreadable: "Could not be read at the time",
  importFailed: "Failed, with no reason recorded",
  replacementRejected: "Worse than the copy you already had"
};

export function BlockedReleasesConsole({ releases, onChanged }: BlockedReleasesConsoleProps) {
  const [busyId, setBusyId] = useState<string | null>(null);

  async function unblock(release: BlockedRelease) {
    setBusyId(release.id);
    try {
      const response = await authedFetch(`/api/blocked-releases/${release.id}`, { method: "DELETE" });
      if (!response.ok) throw new Error("unblock-failed");

      // No search is started. Clearing several at once would otherwise fire one
      // search per row, and somebody clearing a single title searches for it
      // themselves.
      toast.success(`${release.releaseName} can be used again. Search for the title when you want it.`);
      onChanged();
    } catch {
      toast.error("That release could not be un-refused.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <ListCard
      title="Blocklist"
      count={releases.length === 1 ? "1 refused release" : `${releases.length} refused releases`}
    >
      <ListTable
        chevron={false}
        columns={[
          { label: "Release" },
          { label: "Why", mobile: true },
          { label: "Refused", width: LIST_TRACK.status },
          { label: "", width: "auto", align: "end", mobile: true }
        ]}
      >
        {releases.length === 0 ? (
          <ListEmpty
            title="Nothing has been refused"
            description="A download that turns out to be junk lands here, with the reason it was refused. Searches skip anything on this list and say so, until you un-refuse it."
          />
        ) : (
          releases.map((release) => (
            <ListRow key={release.id}>
              <ListNameCell
                name={release.releaseName}
                sub={`${release.title ?? "Unknown title"} · ${release.indexerName}`}
              />
              <div role="cell">
                <Chip tone="idle">{REASON_WORDS[release.reasonCode] ?? release.reasonCode}</Chip>
              </div>
              <div role="cell" className="text-[length:var(--type-caption)] text-muted-foreground">
                {new Date(release.blockedUtc).toLocaleDateString(undefined, {
                  day: "numeric",
                  month: "short",
                  year: "numeric"
                })}
              </div>
              <div role="cell" className="flex justify-end">
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={busyId !== null}
                  onClick={() => void unblock(release)}
                >
                  <RotateCcw aria-hidden className="h-3.5 w-3.5" />
                  {busyId === release.id ? "Un-refusing…" : "Un-refuse"}
                </Button>
              </div>
            </ListRow>
          ))
        )}
      </ListTable>
    </ListCard>
  );
}
