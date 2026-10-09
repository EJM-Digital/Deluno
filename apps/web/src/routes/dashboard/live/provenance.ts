/**
 * Why Deluno is fetching a title (#1098): a small tag on each journey card. It is
 * only ever told from what Deluno recorded: a Seerr request that names the title,
 * the way the download was started (a grab the person chose, a title they added)
 * or a reason the server stored with the download. When nothing says why, there is
 * no tag; it is never guessed.
 */
import type { DownloadDispatchItem } from "../../../lib/api";

export type StartedBy = "you" | "seerr" | "list" | "upgrade" | "missing";

/** The words on each tag. */
export const STARTED_LABEL: Record<StartedBy, string> = {
  you: "You asked for it",
  seerr: "Asked for in Seerr",
  list: "On one of your lists",
  upgrade: "Looking for an upgrade",
  missing: "A missing title"
};

/** What a tag says for a card: "missing" names the card's own kind (film, episode, season or show) and never calls a film an episode. */
export function startedLabel(by: StartedBy, entity?: { kind: string }, release?: string | null): string {
  if (by !== "missing") return STARTED_LABEL[by];
  if (entity?.kind === "movie") return "A missing film";
  if (entity?.kind === "episode") return "A missing episode";
  if (entity?.kind === "series") {
    return /\bS\d{1,2}[\s._-]?E\d{1,3}\b/i.test(release ?? "") ? "A missing episode" : /\bS\d{1,2}\b|\bSeason[\s._-]?\d{1,2}\b/i.test(release ?? "") ? "A missing season" : "Missing episodes";
  }
  return STARTED_LABEL.missing;
}

/** The colour of each tag: the app's own tokens, so dark and light both read. */
export const STARTED_TONE: Record<StartedBy, string> = {
  you: "bg-primary/15 text-primary",
  seerr: "bg-[hsl(var(--stage-search)/0.16)] text-[hsl(var(--stage-search))]",
  list: "bg-info/15 text-info",
  upgrade: "bg-success/15 text-success",
  missing: "bg-warning/15 text-warning"
};

/** What the server calls it, as a tag. Unknown words give no tag. */
export function startedByFromWord(word: string | null | undefined): StartedBy | undefined {
  const said = (word ?? "").trim().toLowerCase();
  if (!said) return undefined;
  if (["you", "manual", "manual-season", "person", "user", "movie-added", "series-added"].includes(said)) return "you";
  if (["seerr", "request", "requests", "overseerr", "jellyseerr"].includes(said)) return "seerr";
  if (["list", "lists", "intake", "trakt", "mdblist", "watchlist"].includes(said)) return "list";
  if (["upgrade", "upgrades", "upgrade-hunt"].includes(said)) return "upgrade";
  if (["missing", "missing-episode", "wanted"].includes(said)) return "missing";
  return undefined;
}

/** How a sent download was started, from the reason the server stored or from the way it was grabbed. */
export function startedByOfDispatch(dispatch: Pick<DownloadDispatchItem, "startedBy" | "notesJson" | "indexerName"> | undefined): StartedBy | undefined {
  if (!dispatch) return undefined;
  const stored = startedByFromWord(dispatch.startedBy);
  if (stored) return stored;
  // A release picked by hand from a search is recorded under "Manual selection".
  if (dispatch.indexerName === "Manual selection") return "you";
  if (!dispatch.notesJson) return undefined;
  try {
    const notes = JSON.parse(dispatch.notesJson) as Record<string, unknown> | null;
    if (!notes || typeof notes !== "object") return undefined;
    if (notes.chosenOnSearch === true || notes.selectedRelease) return "you";
    return startedByFromWord(typeof notes.triggeredBy === "string" ? notes.triggeredBy : null);
  } catch {
    return undefined;
  }
}
