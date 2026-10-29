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
 * un-refusing is one click. Un-refusing starts no search: the owner was explicit
 * that clearing in bulk must not become a storm, and that somebody clearing a
 * single title will search for it themselves.</p>
 *
 * Contract: GET /api/blocked-releases, DELETE /api/blocked-releases/{id},
 * POST /api/blocked-releases/{id}/refuse, POST /api/blocked-releases/{id}/cleanup,
 * POST /api/blocked-releases/cleanup-all.
 */
import { useState } from "react";
import { Check, Eraser, RotateCcw, X } from "lucide-react";
import type { BlockedRelease, PlatformSettingsSnapshot, RecycleBinSettings } from "../../lib/api";
import { reasonWords, type ImportFailureRule } from "../../lib/failure-reasons";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { Drawer, DrawerSection } from "../ui/drawer";
import { Disclosure } from "../ui/disclosure";
import { LIST_TRACK, ListCard, ListEmpty, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { FailureRulesConsole } from "./failure-rules-console";
import { FailureSchedulesConsole } from "./failure-schedules-console";
import { plural } from "../../lib/plural";

/**
 * Whether a person clicked Refuse, or Deluno decided this on its own (#800).
 * Sonarr says this in its blocklist detail; this is the same fact, one chip.
 */
function originLabel(release: BlockedRelease): string {
  return release.origin === "manual" ? "You blocked it" : "Deluno blocked it";
}

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
  settings: PlatformSettingsSnapshot;
  recycleBin: RecycleBinSettings;
  onChanged: () => void;
}

/**
 * Which indexers a refusal holds. Every refusal holds the release from all of
 * them (#585, #874), and saying only the one it came from would tell the
 * reader it could still arrive from another.
 */
function whereItIsRefused(release: BlockedRelease) {
  return `every indexer (found on ${release.indexerName})`;
}

export function BlockedReleasesConsole({
  releases,
  rules,
  settings,
  recycleBin,
  onChanged
}: BlockedReleasesConsoleProps) {
  const [busyId, setBusyId] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const [rulesOpen, setRulesOpen] = useState(false);
  const [schedulesOpen, setSchedulesOpen] = useState(false);
  const [cleanUpAllOpen, setCleanUpAllOpen] = useState(false);
  const [cleaningUpAll, setCleaningUpAll] = useState(false);

  const proposed = releases.filter((release) => release.state === "proposed");
  const refused = releases.filter((release) => release.state !== "proposed");
  const openRelease = releases.find((release) => release.id === openId) ?? null;
  const changedRules = rules.filter((rule) => rule.isOverridden).length;

  /**
   * The bulk version of `cleanUp` below (#800): the same per-release rules,
   * run once for every refused release rather than once per click. Behind a
   * confirmation naming how many will be touched, because unlike un-refusing
   * this does talk to a download client for each one.
   */
  async function cleanUpAll() {
    setCleaningUpAll(true);
    try {
      const response = await authedFetch("/api/blocked-releases/cleanup-all", { method: "POST" });
      if (!response.ok) throw new Error("cleanup-all-failed");

      const result = (await response.json()) as { cleared: number; waitingOnSharing: number; failed: number };
      if (result.cleared > 0) {
        toast.success(
          `Cleared ${plural(result.cleared, "release")} at the download client.` +
            (result.waitingOnSharing > 0 ? ` ${result.waitingOnSharing} left alone — still sharing.` : "")
        );
        onChanged();
      } else if (result.waitingOnSharing > 0) {
        toast.warning(`Nothing was cleared — ${plural(result.waitingOnSharing, "release")} still sharing.`);
      } else {
        toast.warning("There was nothing left to clear.");
      }
    } catch {
      toast.error("The blocklist could not be cleared up.");
    } finally {
      setCleaningUpAll(false);
      setCleanUpAllOpen(false);
    }
  }

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
    <div className="grid gap-(--grid-gap)">
      {proposed.length ? (
        <ListCard
          title="Waiting for you"
          count={proposed.length === 1 ? "1 release to decide" : `${proposed.length} releases to decide`}
        >
          <ListTable
            chevron={false}
            listId="blocked-releases-proposed"
            items={proposed}
            columns={[
              { id: "release", label: "Release", sort: (release) => release.releaseName.toLowerCase() },
              { id: "what", label: "What happened", sort: (release) => reasonWords(release.reasonCode) },
              { id: "actions", label: "", width: LIST_TRACK.action, mobileWidth: "auto", align: "end", mobile: true }
            ]}
          >
            {(rows) => rows.map((release) => (
              <ListRow key={release.id} onClick={() => setOpenId(release.id)} selected={openId === release.id}>
                <ListNameCell
                  name={release.releaseName}
                  sub={
                    <>
                      <span className="md:hidden">{reasonWords(release.reasonCode)} · </span>
                      {`${release.title ?? "Unknown title"} · ${whereItIsRefused(release)}`}
                    </>
                  }
                />
                <div role="cell" className="hidden md:block">
                  <Chip tone="warn">{reasonWords(release.reasonCode)}</Chip>
                </div>
                <div role="cell" className="flex flex-col items-end justify-end gap-2 md:flex-row" onClick={(event) => event.stopPropagation()} onKeyDown={(event) => event.stopPropagation()}>
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
        title={refused.length === 1 ? "1 refused release" : `${refused.length} refused releases`}
        titleCase={false}
        actions={
          refused.length ? (
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busyId !== null || cleaningUpAll}
              onClick={() => setCleanUpAllOpen(true)}
            >
              <Eraser aria-hidden className="h-3.5 w-3.5" />
              Clean up all
            </Button>
          ) : undefined
        }
      >
        <ListTable
          chevron={false}
          listId="blocked-releases-refused"
          items={refused}
          columns={[
            { id: "release", label: "Release", sort: (release) => release.releaseName.toLowerCase() },
            { id: "why", label: "Why", sort: (release) => reasonWords(release.reasonCode) },
            { id: "refused", label: "Refused", width: LIST_TRACK.status, sort: (release) => new Date(release.blockedUtc) },
            { id: "actions", label: "", width: LIST_TRACK.action, mobileWidth: "auto", align: "end", mobile: true }
          ]}
        >
          {(rows) => refused.length === 0 ? (
            <ListEmpty
              title="Nothing has been refused"
              description="A download that turns out to be junk lands here, with the reason it was refused. Searches skip anything on this list and say so, until you un-refuse it."
            />
          ) : (
            rows.map((release) => (
              <ListRow key={release.id} onClick={() => setOpenId(release.id)} selected={openId === release.id}>
                <ListNameCell
                  name={release.releaseName}
                  sub={
                    <>
                      <span className="md:hidden">{reasonWords(release.reasonCode)} · </span>
                      {`${release.title ?? "Unknown title"} · ${whereItIsRefused(release)}`}
                    </>
                  }
                />
                <div role="cell" className="hidden flex-wrap gap-1.5 md:flex">
                  <Chip tone="idle">{reasonWords(release.reasonCode)}</Chip>
                  <Chip tone={release.origin === "manual" ? "info" : "idle"}>{originLabel(release)}</Chip>
                </div>
                <div role="cell" className="hidden text-(length:--type-caption) text-muted-foreground md:block">
                  {new Date(release.blockedUtc).toLocaleDateString(undefined, {
                    day: "numeric",
                    month: "short",
                    year: "numeric"
                  })}
                </div>
                <div role="cell" className="flex flex-col items-end justify-end gap-2 md:flex-row" onClick={(event) => event.stopPropagation()} onKeyDown={(event) => event.stopPropagation()}>
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

      {/*
        The third section DESIGN-007 asked this console to carry. The other two
        answer what Deluno does about a failure; this one answers how often it
        looks, and how long you have to change your mind about what it took.
      */}
      <Disclosure
        title="How often Deluno checks, and what it keeps"
        summary={`Files checked every ${hours(settings.libraryFileCheckHours)} · removed files kept for ${days(recycleBin.retentionDays)}`}
        open={schedulesOpen}
        onOpenChange={setSchedulesOpen}
      >
        <FailureSchedulesConsole settings={settings} recycleBin={recycleBin} onChanged={onChanged} />
      </Disclosure>

      <Drawer
        open={openRelease !== null}
        onOpenChange={(open) => { if (!open) setOpenId(null); }}
        title={openRelease?.releaseName ?? "Release"}
        description={openRelease ? (openRelease.state === "proposed" ? "Waiting for you to decide" : "Refused") : undefined}
      >
        {openRelease ? (
          <DrawerSection title="This release">
            <BlockFact label="Release" value={openRelease.releaseName} mono />
            <BlockFact label="Title" value={openRelease.title ?? "Unknown title"} />
            <BlockFact label="Found on" value={openRelease.indexerName} />
            <BlockFact label="Why" value={reasonWords(openRelease.reasonCode)} />
            <BlockFact label="Who" value={originLabel(openRelease)} />
            <BlockFact label="Refused" value={new Date(openRelease.blockedUtc).toLocaleString()} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={cleanUpAllOpen}
        onOpenChange={setCleanUpAllOpen}
        title="Clean up every refused release?"
        description={`This clears ${plural(refused.length, "refused release")} at their download clients — the same as clicking Clean up now on each row. A copy your sharing rule still needs is left alone.`}
        confirmLabel="Clean up all"
        confirmVariant="destructive"
        presentation="default"
        busy={cleaningUpAll}
        onConfirm={() => void cleanUpAll()}
      />
    </div>
  );
}

function BlockFact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="grid min-w-0 gap-0.5">
      <span className="text-(length:--type-micro) font-semibold uppercase tracking-widest text-muted-foreground">{label}</span>
      <span className={mono ? "break-all font-mono text-(length:--type-caption) text-foreground" : "break-words text-(length:--type-body-sm) text-foreground"}>{value}</span>
    </div>
  );
}

function hours(value: number): string {
  if (value === 1) return "hour";
  if (value === 24) return "day";
  if (value === 168) return "week";
  return `${value} hours`;
}

function days(value: number): string {
  return value === 1 ? "a day" : `${value} days`;
}
