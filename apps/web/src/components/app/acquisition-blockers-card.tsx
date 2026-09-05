/**
 * Why this will not download — and the one button that changes it.
 *
 * <p>The behaviour being replaced is Radarr's, and Radarr is not wrong to have
 * it: a release that failed to import is blocklisted so the same broken file is
 * not fetched for ever, SABnzbd remembers the name, and qBittorrent still holds
 * the infohash. Three mechanisms, each correct, and all three silent. A title
 * you deleted and asked for again simply never arrives, and the only way back
 * is to know that three hidden lists exist and go and empty all three.</p>
 *
 * <p>So this card exists to say the quiet part. Every sentence in it is
 * composed by the server — see `AcquisitionBlockerReader` — because the screen
 * that explains a blocker and the endpoint that clears it have to agree about
 * what is in the way, and a UI that re-words the reason is a second opinion
 * nobody asked for.</p>
 *
 * <p><b>It renders nothing when nothing is wrong.</b> A permanent panel reading
 * "no problems" is a panel people stop seeing, and this one has to be noticed
 * on the day it finally has something to say.</p>
 *
 * Contract: GET /api/{movies,series}/{id}/acquisition-blockers,
 * POST /api/{movies,series}/{id}/force-redownload.
 */
import { useState } from "react";
import { RotateCcw } from "lucide-react";
import type { AcquisitionBlockersResponse, AcquisitionOverrideResponse } from "../../lib/api";
import { ACQUISITION_BLOCKER_KINDS } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import type { Tone } from "../../lib/status-tones";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { ListCard, ListNameCell, ListRow, ListTable } from "../ui/list-card";

/**
 * How each kind is coloured, looked up rather than chosen at the point of use —
 * the rule `Chip` states.
 *
 * <p>Only an exclusion is `bad`. The rest are the system working: a download in
 * flight is progress, a file with the processor is progress, and a title that
 * is not out yet is nobody's fault. Painting all of them red would teach people
 * that this card means breakage, and then the one that does mean breakage would
 * arrive in a colour they had learned to discount.</p>
 */
const BLOCKER_TONE: Record<string, Tone> = {
  [ACQUISITION_BLOCKER_KINDS.alreadyHeld]: "ok",
  [ACQUISITION_BLOCKER_KINDS.downloadInFlight]: "info",
  [ACQUISITION_BLOCKER_KINDS.processorHoldingFile]: "info",
  [ACQUISITION_BLOCKER_KINDS.importExcluded]: "bad",
  [ACQUISITION_BLOCKER_KINDS.searchSkipped]: "warn",
  [ACQUISITION_BLOCKER_KINDS.searchDeferred]: "warn",
  [ACQUISITION_BLOCKER_KINDS.notYetAvailable]: "idle"
};

export interface AcquisitionBlockersCardProps {
  blockers: AcquisitionBlockersResponse | null;
  /** "/api/movies" or "/api/series". */
  route: string;
  mediaId: string;
  /** Called once the force has finished, so the page can reload what changed. */
  onForced: () => void;
  /** True while some other action on the page owns the buttons. */
  disabled?: boolean;
}

/**
 * What the confirmation says, built from the server's own account of each
 * effect rather than from a generic warning. "This will clear the blocklist"
 * is not something a person can weigh; "Removes the download from qBittorrent,
 * along with its files" is.
 */
function describeEffects(blockers: AcquisitionBlockersResponse): string {
  const effects = blockers.blockers
    .filter((blocker) => blocker.canClear)
    .map((blocker) => blocker.clearEffect ?? blocker.summary);

  const opening =
    effects.length === 1
      ? "One record will be cleared, and then Deluno will search again:"
      : `${effects.length} records will be cleared, and then Deluno will search again:`;

  return `${opening} ${effects.join(" ")} This reaches into things Deluno does not own, and pressing the button a second time does not undo it.`;
}

export function AcquisitionBlockersCard({
  blockers,
  route,
  mediaId,
  onForced,
  disabled = false
}: AcquisitionBlockersCardProps) {
  const [isConfirming, setIsConfirming] = useState(false);
  const [isForcing, setIsForcing] = useState(false);

  if (!blockers || blockers.nothingIsBlocking || blockers.blockers.length === 0) {
    return null;
  }

  async function force() {
    setIsForcing(true);
    try {
      const response = await authedFetch(`${route}/${mediaId}/force-redownload`, { method: "POST" });
      if (!response.ok) throw new Error("force-redownload-failed");

      const result = (await response.json()) as AcquisitionOverrideResponse;

      // The server already wrote the sentence, including the half that did not
      // work. Showing its words rather than "Done" is the point of the feature:
      // a force reaches into a download client and a processor, and "some of it
      // worked" is a thing a person needs to be told.
      if (result.couldNotClear.length > 0) {
        toast.warning(result.summary);
      } else {
        toast.success(result.summary);
      }

      setIsConfirming(false);
      onForced();
    } catch {
      toast.error("The re-download could not be forced. Nothing was changed.");
    } finally {
      setIsForcing(false);
    }
  }

  return (
    <>
      <ListCard
        title="Why this will not download"
        count={blockers.blockers.length === 1 ? "1 reason" : `${blockers.blockers.length} reasons`}
      >
        <ListTable
          chevron={false}
          columns={[{ label: "Reason" }, { label: "Where", width: "auto", align: "end", mobile: true }]}
        >
          {blockers.blockers.map((blocker) => (
            <ListRow key={`${blocker.kind}:${blocker.source}`}>
              <ListNameCell name={blocker.summary} sub={blocker.detail} />
              <div role="cell" className="flex justify-end">
                <Chip tone={BLOCKER_TONE[blocker.kind] ?? "idle"}>{blocker.source}</Chip>
              </div>
            </ListRow>
          ))}
        </ListTable>

        {blockers.canForce ? (
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-hairline px-[var(--card-pad-x)] py-3">
            <p className="text-[length:var(--type-caption)] text-muted-foreground">{blockers.summary}</p>
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={disabled || isForcing}
              onClick={() => setIsConfirming(true)}
            >
              <RotateCcw aria-hidden className="h-3.5 w-3.5" />
              Force a re-download
            </Button>
          </div>
        ) : null}
      </ListCard>

      <ConfirmDialog
        open={isConfirming}
        onOpenChange={(open) => {
          if (!isForcing) setIsConfirming(open);
        }}
        title="Force a re-download?"
        description={describeEffects(blockers)}
        confirmLabel={isForcing ? "Clearing…" : "Clear it and search again"}
        confirmVariant="destructive"
        busy={isForcing}
        onConfirm={() => void force()}
      />
    </>
  );
}
