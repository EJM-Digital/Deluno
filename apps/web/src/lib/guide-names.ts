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
 * mean re-pinning the package to change a label.
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
  "LQ (Low Quality Groups)": "Known low-quality release groups",
  // TRaSH's own names for the same formats. Rules made before the catalogue
  // used TRaSH's formats keep the older names above until they are moved.
  "Repack/Proper": "Corrected re-release",
  "LQ": "Known low-quality release groups",
  "No-RlsGroup": "Releases without a release group",
  "Anime Web Tier 01": "Top-tier anime WEB groups",
  "Line/Mic Dubbed": "Line or mic dubbed audio",
  "DV (w/o HDR fallback)": "Dolby Vision without an HDR fallback",
  "DV Boost": "Extra score for Dolby Vision",
  "HDR10+ Boost": "Extra score for HDR10+",
  "DV (Disk)": "Dolby Vision from a disc release",
  "Language: Not English": "Not in English",
  "AMZN": "Amazon Prime Video",
  "ATVP": "Apple TV+",
  "NF": "Netflix",
  "DSNP": "Disney+",
  "HMAX": "HBO Max",
  "MAX": "Max",
  "PCOK": "Peacock",
  "PMTP": "Paramount+",
  "HULU": "Hulu",
  "iT": "iTunes",
  "ROKU": "The Roku Channel",
  "CR": "Crunchyroll"
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
