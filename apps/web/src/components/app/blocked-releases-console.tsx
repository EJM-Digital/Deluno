/**
 * The failure and blocklist console — every release Deluno has refused, every
 * one it is asking about, and the rules that decided both.
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
 * Contract: GET /api/blocked-releases, DELETE /api/blocked-releases/{id},
 * POST /api/blocked-releases/{id}/refuse, POST /api/blocked-releases/{id}/cleanup.
 */
import { useState } from "react";
import { Check, Eraser, RotateCcw, X } from "lucide-react";
import type { BlockedRelease } from "../../lib/api";
import { reasonWords, type ImportFailureRule } from "../../lib/failure-reasons";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { Disclosure } from "../ui/disclosure";
import { LIST_TRACK, ListCard, ListEmpty, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { FailureRulesConsole } from "./failure-rules-console";

/**
 * What clearing up actually did, said the way a person would say it.
 *
 * <p>"Still sharing" is not a failure and must not read like one: the rule that
 * knows what the tracker expects has said wait, and it wins over a button.</p>
 */
const CLEANUP_WORDS: Record<string, string> = {
  cleared: "Cleared at the download client. It will accept this release again if you un-refuse it.",
  stillSharing: "Left alone — your sharing rule still needs this copy seeded. It will be cleared once that is met.",
  nothingToClear: "There was nothing left to clear.",
  clientUnavailable: "The download client did not answer. Nothing was changed, and Deluno will try again."
};

export interface BlockedReleasesConsoleProps {
  releases: BlockedRelease[];
  rules: ImportFailureRule[];
  onChanged: () => void;
}

export function BlockedReleasesConsole({ releases, rules, onChanged }: BlockedReleasesConsoleProps) {
  const [busyId, setBusyId] = useState<string | null>(null);
  const [rulesOpen, setRulesOpen] = useState(false);

  const proposed = releases.filter((release) => release.state === "proposed");
  const refused = releases.filter((release) => release.state !== "proposed");
  const changedRules = rules.filter((rule) => rule.isOverridden).length;

  /**
   * The manual half of the scheduled clear-out — for a refusal that predates
   * the setting, or one whose download client was off when the schedule came
   * round. It calls the same service the schedule calls, so it still will not
   * overrule the sharing rule: a copy the tracker expects you to keep seeding
   * is left alone and says so.
   */
  async function cleanUp(release: BlockedRelease) {
    setBusyId(release.id);
    try {
      const response = await authedFetch(`/api/blocked-releases/${release.id}/cleanup`, { method: "POST" });
      if (!response.ok) throw new Error("cleanup-failed");

      const { outcome } = (await response.json()) as { outcome: string };
      const said = CLEANUP_WORDS[outcome];
      if (outcome === "cleared") {
        toast.success(said!);
        onChanged();
      } else {
        toast.warning(said ?? outcome);
      }
    } catch {
      toast.error("That could not be cleared up.");
    } finally {
      setBusyId(null);
    }
  }

  async function act(release: BlockedRelease, action: "allow" | "refuse") {
    setBusyId(release.id);
    try {
      const response = await authedFetch(
        action === "refuse" ? `/api/blocked-releases/${release.id}/refuse` : `/api/blocked-releases/${release.id}`,
        { method: action === "refuse" ? "POST" : "DELETE" }
      );
      if (!response.ok) throw new Error("blocklist-failed");

      // No search is started. Clearing several at once would otherwise fire one
      // search per row, and somebody clearing a single title searches for it
      // themselves.
      toast.success(
        action === "refuse"
          ? `Deluno will not use ${release.releaseName} again. Searches skip it and say so.`
          : `${release.releaseName} can be used again. Search for the title when you want it.`
      );
      onChanged();
    } catch {
      toast.error(action === "refuse" ? "That release could not be refused." : "That release could not be un-refused.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="grid gap-[var(--grid-gap)]">
      {proposed.length ? (
        <ListCard
          title="Waiting for you"
          count={proposed.length === 1 ? "1 release to decide" : `${proposed.length} releases to decide`}
        >
          <ListTable
            chevron={false}
            columns={[
              { label: "Release" },
              { label: "What happened", mobile: true },
              { label: "", width: "auto", align: "end", mobile: true }
            ]}
          >
            {proposed.map((release) => (
              <ListRow key={release.id}>
                <ListNameCell
                  name={release.releaseName}
                  sub={`${release.title ?? "Unknown title"} · ${release.indexerName}`}
                />
                <div role="cell">
                  <Chip tone="warn">{reasonWords(release.reasonCode)}</Chip>
                </div>
                <div role="cell" className="flex justify-end gap-2">
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={busyId !== null}
                    onClick={() => void act(release, "refuse")}
                  >
                    <X aria-hidden className="h-3.5 w-3.5" />
                    Refuse it
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    disabled={busyId !== null}
                    onClick={() => void act(release, "allow")}
                  >
                    <Check aria-hidden className="h-3.5 w-3.5" />
                    Allow it
                  </Button>
                </div>
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      <ListCard
        title="Blocklist"
        count={refused.length === 1 ? "1 refused release" : `${refused.length} refused releases`}
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
          {refused.length === 0 ? (
            <ListEmpty
              title="Nothing has been refused"
              description="A download that turns out to be junk lands here, with the reason it was refused. Searches skip anything on this list and say so, until you un-refuse it."
            />
          ) : (
            refused.map((release) => (
              <ListRow key={release.id}>
                <ListNameCell
                  name={release.releaseName}
                  sub={`${release.title ?? "Unknown title"} · ${release.indexerName}`}
                />
                <div role="cell">
                  <Chip tone="idle">{reasonWords(release.reasonCode)}</Chip>
                </div>
                <div role="cell" className="text-[length:var(--type-caption)] text-muted-foreground">
                  {new Date(release.blockedUtc).toLocaleDateString(undefined, {
                    day: "numeric",
                    month: "short",
                    year: "numeric"
                  })}
                </div>
                <div role="cell" className="flex justify-end gap-2">
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    disabled={busyId !== null}
                    onClick={() => void cleanUp(release)}
                  >
                    <Eraser aria-hidden className="h-3.5 w-3.5" />
                    Clean up now
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={busyId !== null}
                    onClick={() => void act(release, "allow")}
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

      {/*
        Behind a disclosure because of what people come here for. The list
        answers "why has my film not arrived"; the rules are set once and then
        left alone, and putting seventeen of them above the list would bury the
        question the screen exists to answer.
      */}
      <Disclosure
        title="What Deluno does when an import fails"
        summary={
          changedRules
            ? `${rules.length} kinds of failure · ${changedRules} answered your way`
            : `${rules.length} kinds of failure · all on Deluno's own answers`
        }
        open={rulesOpen}
        onOpenChange={setRulesOpen}
      >
        <FailureRulesConsole rules={rules} onChanged={onChanged} />
      </Disclosure>
    </div>
  );
}
