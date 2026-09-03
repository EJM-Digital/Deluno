/**
 * The guide's names are its filenames.
 *
 * "HDR10", "TrueHD" and "Netflix" are the real names of real things and are
 * left alone. "WEB Tier 01", "Repack2", "BR-DISK" and "Retags" are identifiers
 * from a wiki, and reading a list of them teaches you nothing.
 *
 * This lives here rather than in the guide package because the package is
 * pinned to an upstream revision with an integrity hash: a display name is
 * Deluno's word for the thing, not the guide's, and adding one upstream would
 * mean re-pinning 85 entries to change a label.
 */
const FRIENDLY_NAMES: Record<string, string> = {
  "HD Bluray Tier 01": "Top-tier Blu-ray groups",
  "HD Bluray Tier 02": "Trusted Blu-ray groups",
  "UHD Bluray Tier 01": "Top-tier 4K Blu-ray groups",
  "WEB Tier 01": "Top-tier WEB groups",
  "WEB Tier 02": "Trusted WEB groups",
  "WEB Tier 03": "Fallback WEB groups",
  "Remux Tier 01": "Top-tier remux groups",
  "Remux Tier 02": "Trusted remux groups",
  "Anime BD Tier 01": "Top-tier anime Blu-ray groups",
  "Anime WEB Tier 01": "Top-tier anime WEB groups",
  "Repack2": "Second repack",
  "Repack3": "Third repack",
  "Repack / Proper": "Corrected re-release",
  "BR-DISK": "Full disc image, not a video file",
  "Retags": "Re-tagged re-uploads",
  "Obfuscated": "Obfuscated file names",
  "No Release Group": "Releases without a release group",
  "LQ (Low Quality Groups)": "Known low-quality release groups"
};

/** The name to show a person, given whatever the rule is called. */
export function friendlyRuleName(name: string | null | undefined): string {
  const raw = (name ?? "").trim();
  return FRIENDLY_NAMES[raw] ?? raw;
}

/** Whether Deluno renames this one, so the original can be shown underneath. */
export function hasFriendlyRuleName(name: string | null | undefined): boolean {
  return Boolean(name && FRIENDLY_NAMES[name.trim()]);
}
