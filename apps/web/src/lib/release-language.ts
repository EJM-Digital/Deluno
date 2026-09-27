/**
 * A release name read for the language it carries (#796), for display only.
 *
 * Mirrors the token set `ReleasePreferenceFactFactory.DetectLanguage`
 * (src/Deluno.Quality/ReleasePreferences/ReleasePreferenceFactFactory.cs)
 * reads server-side when building custom-format facts from a probed file's
 * audio track. That version judges an installed file's audio stream and
 * feeds quality decisions; this one judges a release's name so the Transfers
 * queue can show a language before anything has been downloaded, let alone
 * probed. Keep the two word lists in step by eye — there is no shared
 * assembly between a Vite bundle and the Quality project to hold one table
 * for both.
 */
export function detectReleaseLanguage(releaseName: string): string | null {
  const value = releaseName.toLowerCase();
  const hasToken = (...tokens: string[]) => tokens.some((token) => new RegExp(`(^|[^a-z0-9])${token}([^a-z0-9]|$)`).test(value));

  if (hasToken("multi") || value.includes("multilanguage")) return "Multilingual";
  if (hasToken("dual", "dubbed")) return "Dubbed";
  if (value.includes("original")) return "Original language";
  if (value.includes("french") || hasToken("vostfr", "vff", "vfq", "vf2", "truefrench")) return "French";
  if (value.includes("german") || hasToken("deutsch")) return "German";
  if (value.includes("japanese") || hasToken("jpn", "jap")) return "Japanese";
  if (value.includes("spanish") || hasToken("castellano", "latino")) return "Spanish";
  if (value.includes("english")) return "English";
  return null;
}
