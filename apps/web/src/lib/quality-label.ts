/**
 * What a title's file actually is, in words.
 *
 * There used to be a `shortQuality()` in `library-grid.tsx` that collapsed the
 * whole ladder into three answers: anything with 2160 became **4K**, anything
 * with 1080 became **1080p**, anything with 720 became **720p**. So a 60 GB
 * Remux 2160p and a 7 GB WEB 2160p read identically, on the shelf, in the
 * compact list and on the dashboard — and the difference between them is the
 * entire reason the quality ladder, the cutoff and every upgrade search exist.
 *
 * The stored value is already the answer. `QualityModelService` names the
 * twenty-one tiers — `WEB 1080p`, `Remux 1080p`, `WEB 2160p`, `Bluray 2160p`,
 * `Remux 2160p` — and those names are what quality profiles, cutoffs and the
 * TRaSH templates all speak. So this does not re-derive anything: mapping
 * "remux" and "2160" back onto a tier name is a rule the server already owns
 * (`VersionedMediaPolicyEngine`), and a second copy of it here is how the last
 * several defects in this codebase were built.
 *
 * It only tidies the separator, because the same tier arrives spelled two ways
 * depending on who wrote it: Deluno's own ladder says `Bluray 1080p`, a release
 * name parsed from disk says `Bluray-1080p`, and those are one quality.
 */
export function qualityLabel(value: string | null | undefined): string | null {
  if (!value) return null;
  const tidied = value.trim().replace(/[-_]+/g, " ").replace(/\s+/g, " ");
  return tidied.length > 0 ? tidied : null;
}

/**
 * The quality a title **has**, which is not the quality it wants.
 *
 * The adapters carry `currentQuality` and `targetQuality` separately and then
 * derive `quality` as `current ?? target`. Reading that derived field for a
 * badge meant a movie with no file at all wore its *target* — a missing 4K
 * movie showed "4K" beside a red dot, claiming a file it did not have. Same
 * defect family as #299, where a search-scheduling concept overrode file state
 * on an availability chip.
 *
 * A title with no file has no quality. The mark already says why.
 */
export function heldQualityLabel(item: {
  currentQuality?: string | null;
  quality?: string | null;
  hasFile?: boolean;
}): string | null {
  if (item.hasFile === false) return null;
  return qualityLabel(item.currentQuality ?? null);
}
