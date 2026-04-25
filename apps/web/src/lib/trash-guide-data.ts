/**
 * Bundled TrashGuides data for Deluno.
 *
 * Everything a user needs to configure quality profiles and custom formats
 * is pre-loaded here — no reading guides, no YAML, no JSON imports, no
 * Recyclarr. Based on the TRaSH-Guides/Guides repository and Recyclarr
 * config-templates.
 *
 * Sources:
 *   https://trash-guides.info/Radarr/radarr-setup-quality-profiles/
 *   https://github.com/TRaSH-Guides/Guides/tree/master/docs/json/radarr/cf
 *   https://github.com/recyclarr/config-templates/tree/main/radarr
 */

/* ── Quality tiers ───────────────────────────────────────────────── */

export type QualitySource =
  | "cam"
  | "dvd"
  | "hdtv"
  | "webdl"
  | "webrip"
  | "bluray"
  | "remux"
  | "brdisk";

export type QualityResolution = "480p" | "720p" | "1080p" | "2160p";

export interface QualityTier {
  id: string;
  label: string;
  source: QualitySource;
  resolution: QualityResolution;
  /** MB per minute — from TRaSH quality-size/movie.json */
  minMbPerMin: number;
  maxMbPerMin: number;
  /** Display order — higher = better */
  rank: number;
}

export const QUALITY_TIERS: QualityTier[] = [
  { id: "hdtv-720p",    label: "HDTV 720p",     source: "hdtv",   resolution: "720p",  minMbPerMin: 17.1,  maxMbPerMin: 2000, rank: 10 },
  { id: "webdl-720p",   label: "WEB-DL 720p",   source: "webdl",  resolution: "720p",  minMbPerMin: 12.5,  maxMbPerMin: 2000, rank: 20 },
  { id: "webrip-720p",  label: "WEBRip 720p",   source: "webrip", resolution: "720p",  minMbPerMin: 12.5,  maxMbPerMin: 2000, rank: 21 },
  { id: "bluray-720p",  label: "Bluray 720p",   source: "bluray", resolution: "720p",  minMbPerMin: 25.7,  maxMbPerMin: 2000, rank: 25 },
  { id: "hdtv-1080p",   label: "HDTV 1080p",    source: "hdtv",   resolution: "1080p", minMbPerMin: 33.8,  maxMbPerMin: 2000, rank: 30 },
  { id: "webdl-1080p",  label: "WEB-DL 1080p",  source: "webdl",  resolution: "1080p", minMbPerMin: 12.5,  maxMbPerMin: 2000, rank: 40 },
  { id: "webrip-1080p", label: "WEBRip 1080p",  source: "webrip", resolution: "1080p", minMbPerMin: 12.5,  maxMbPerMin: 2000, rank: 41 },
  { id: "bluray-1080p", label: "Bluray 1080p",  source: "bluray", resolution: "1080p", minMbPerMin: 50.8,  maxMbPerMin: 2000, rank: 50 },
  { id: "remux-1080p",  label: "Remux 1080p",   source: "remux",  resolution: "1080p", minMbPerMin: 102,   maxMbPerMin: 2000, rank: 60 },
  { id: "hdtv-2160p",   label: "HDTV 4K",       source: "hdtv",   resolution: "2160p", minMbPerMin: 85,    maxMbPerMin: 2000, rank: 65 },
  { id: "webdl-2160p",  label: "WEB-DL 4K",     source: "webdl",  resolution: "2160p", minMbPerMin: 34.5,  maxMbPerMin: 2000, rank: 70 },
  { id: "webrip-2160p", label: "WEBRip 4K",     source: "webrip", resolution: "2160p", minMbPerMin: 34.5,  maxMbPerMin: 2000, rank: 71 },
  { id: "bluray-2160p", label: "Bluray 4K",     source: "bluray", resolution: "2160p", minMbPerMin: 102,   maxMbPerMin: 2000, rank: 80 },
  { id: "remux-2160p",  label: "Remux 4K",      source: "remux",  resolution: "2160p", minMbPerMin: 187.4, maxMbPerMin: 2000, rank: 90 },
];

/* ── Custom format categories ────────────────────────────────────── */

export type CFCategory =
  | "hdr"
  | "codec"
  | "audio"
  | "channels"
  | "source"
  | "streaming"
  | "edition"
  | "groups"
  | "anime"
  | "language"
  | "unwanted"
  | "misc";

export const CF_CATEGORY_META: Record<CFCategory, { label: string; description: string; color: string }> = {
  hdr:       { label: "HDR & Color",      description: "HDR formats, Dolby Vision, HLG",                   color: "text-violet-400" },
  codec:     { label: "Video Codec",      description: "x265, x264, AV1, HEVC",                             color: "text-blue-400" },
  audio:     { label: "Audio Format",     description: "Atmos, TrueHD, DTS-HD, FLAC, etc.",                 color: "text-green-400" },
  channels:  { label: "Audio Channels",   description: "Mono, stereo, 5.1, 7.1 and other channel layouts",  color: "text-teal-400" },
  source:    { label: "Source / Edition", description: "REMUX, IMAX, Director's Cut, Extended",             color: "text-amber-400" },
  streaming: { label: "Streaming Service",description: "Netflix, Amazon, Apple TV+, Disney+ release tags",  color: "text-sky-400" },
  edition:   { label: "Edition",          description: "Special editions, cuts, and versions",              color: "text-orange-400" },
  groups:    { label: "Release Groups",   description: "Trusted release group tiers (HD & WEB)",            color: "text-emerald-400" },
  anime:     { label: "Anime",            description: "Anime-specific groups, versions, audio, and release tags", color: "text-pink-400" },
  language:  { label: "Language",         description: "Language and regional release preferences",          color: "text-cyan-400" },
  unwanted:  { label: "Block / Unwanted", description: "Releases to penalise or block entirely",            color: "text-red-400" },
  misc:      { label: "Misc",             description: "Proper, Repack, and other technical flags",         color: "text-muted-foreground" },
};

/* ── Bundled custom format definitions ───────────────────────────── */

export interface BundledCF {
  /** Stable TRaSH Guide ID — use as external reference */
  trashId: string;
  name: string;
  category: CFCategory;
  /** Plain English description shown in the UI */
  description: string;
  /** Default score as per TRaSH recommended profiles */
  defaultScore: number;
  /** Regex patterns for matching release names (simplified spec) */
  patterns: string[];
  /** Some CFs should never be used standalone; they only make sense in bundles */
  bundleOnly?: boolean;
}

export interface CustomFormatBundleEntry {
  trashId: string;
  score?: number;
}

export interface CustomFormatBundle {
  id: string;
  name: string;
  level: "starter" | "balanced" | "premium" | "specialist";
  mediaType: "movies" | "tv" | "all";
  description: string;
  bestFor: string;
  includes: CustomFormatBundleEntry[];
  warnings?: string[];
}

export const BUNDLED_CUSTOM_FORMATS: BundledCF[] = [
  /* ── HDR & Colour ── */
  {
    trashId: "493b6d1dbec3c3364c59d7607f7e3405",
    name: "HDR",
    category: "hdr",
    description: "Any HDR content — HDR10, HDR10+, Dolby Vision, or HLG. A must-have for 4K libraries.",
    defaultScore: 500,
    patterns: ["\\bHDR(\\b|\\d)", "\\bHDR10\\b", "\\bHLG\\b"],
  },
  {
    trashId: "7878c33f1963fefb3d6c8657d46c2f0a",
    name: "HDR10",
    category: "hdr",
    description: "HDR10 — the baseline open standard. Works on all HDR-capable displays.",
    defaultScore: 510,
    patterns: ["\\bHDR10\\b"],
  },
  {
    trashId: "5d96ce331b98e077abb8ceb60553aa16",
    name: "HDR10+",
    category: "hdr",
    description: "HDR10+ — dynamic metadata, better than HDR10 on supported displays.",
    defaultScore: 520,
    patterns: ["\\bHDR10(\\+|Plus)\\b"],
  },
  {
    trashId: "e23edd2482476e595fb990b12e7c609c",
    name: "Dolby Vision",
    category: "hdr",
    description: "Dolby Vision — the best HDR format with dynamic metadata. Requires a DV-capable display.",
    defaultScore: 0,
    patterns: ["\\b(DV|DoVi|Dolby.?Vision)\\b"],
  },
  {
    trashId: "b337d6812e06c200ec9a2d3cfa9d20a7",
    name: "DV Boost",
    category: "hdr",
    description: "Boost score for Dolby Vision releases. Add this if you have a DV display and want DV preferred over HDR10.",
    defaultScore: 1000,
    patterns: ["\\b(DV|DoVi|Dolby.?Vision)\\b"],
  },
  {
    trashId: "caa37d0df9c348912df1fb1d88f9273a",
    name: "HDR10+ Boost",
    category: "hdr",
    description: "Boost score for HDR10+ releases. Add if you have an HDR10+-capable display.",
    defaultScore: 100,
    patterns: ["\\bHDR10(\\+|Plus)\\b"],
  },
  {
    trashId: "923b6abef9b17f937fab56cfcf89e1f1",
    name: "DV (No HDR Fallback)",
    category: "hdr",
    description: "Dolby Vision WITHOUT an HDR10 fallback layer. Will look washed-out on non-DV displays. Penalise unless you only have DV displays.",
    defaultScore: -10000,
    patterns: ["\\b(DV|DoVi)\\b(?!.*HDR10)"],
  },
  {
    trashId: "9d27d9d2181838f76dee150882bdc58c",
    name: "HLG",
    category: "hdr",
    description: "Hybrid Log-Gamma HDR — used mainly by broadcast. No tonemapping needed on HLG displays.",
    defaultScore: 0,
    patterns: ["\\bHLG\\b"],
  },

  /* ── Video Codec ── */
  {
    trashId: "aadb0e9ebba39a5fcf97c5a6d5ed0cff",
    name: "x265 (HD)",
    category: "codec",
    description: "Penalise x265/HEVC encodes at 720p and 1080p — these are often lower quality than x264 at those resolutions.",
    defaultScore: -10000,
    patterns: ["\\b(x265|HEVC|h265|H.265)\\b(?!.*2160p)(?!.*4K)"],
  },
  {
    trashId: "9170d55c319f4fe40da8711ba9d8050d",
    name: "x265 (4K)",
    category: "codec",
    description: "x265/HEVC at 4K resolution — excellent efficiency and quality. No penalty needed here.",
    defaultScore: 0,
    patterns: ["\\b(x265|HEVC|h265|H.265)\\b.*2160p|2160p.*\\b(x265|HEVC|h265|H.265)\\b"],
  },
  {
    trashId: "403f405f92f536f18ea8e2a99ddb9db5",
    name: "AV1",
    category: "codec",
    description: "AV1 — next-generation codec with excellent compression. Relatively new; not all devices support it.",
    defaultScore: 0,
    patterns: ["\\bAV1\\b"],
  },

  /* ── Audio Formats ── */
  {
    trashId: "496f355514737f7d83bf7aa4d24f8169",
    name: "TrueHD Atmos",
    category: "audio",
    description: "TrueHD with Dolby Atmos — the best lossless surround format. Found on Blu-ray and Remux releases.",
    defaultScore: 5,
    patterns: ["\\bTrueHD\\b.*\\bAtmos\\b|\\bAtmos\\b.*\\bTrueHD\\b"],
  },
  {
    trashId: "2f22d89048b01681dde8afe203bf2e95",
    name: "DTS X",
    category: "audio",
    description: "DTS:X — immersive audio, the DTS alternative to Atmos.",
    defaultScore: 4,
    patterns: ["\\bDTS[-. ]?X\\b"],
  },
  {
    trashId: "1c1a4c5e823891c75bc50380a6866f73",
    name: "TrueHD",
    category: "audio",
    description: "TrueHD lossless audio without Atmos — still excellent quality.",
    defaultScore: 3,
    patterns: ["\\bTrueHD\\b(?!.*Atmos)"],
  },
  {
    trashId: "ca3e897c30fb3d15b3f643e8dfae1ce6",
    name: "DTS-HD MA",
    category: "audio",
    description: "DTS-HD Master Audio — lossless DTS, common on Blu-ray releases.",
    defaultScore: 2,
    patterns: ["\\bDTS[-. ]?HD[-. ]?MA\\b"],
  },
  {
    trashId: "240770601cc226190c367ef59aba7463",
    name: "DD+ Atmos",
    category: "audio",
    description: "Dolby Digital Plus with Atmos — used by streaming services. Lossy but Atmos-capable.",
    defaultScore: 1,
    patterns: ["\\b(EAC3|DD\\+|Dolby.?Digital.?Plus)\\b.*\\bAtmos\\b"],
  },
  {
    trashId: "185f1dd7264c4562b9022d963ac37424",
    name: "FLAC",
    category: "audio",
    description: "FLAC lossless audio — common in MKV releases and anime.",
    defaultScore: 2,
    patterns: ["\\bFLAC\\b"],
  },

  /* ── Source / Edition ── */
  {
    trashId: "9f86d08ede6d42d86c2d1b30a10c16f8",
    name: "REMUX",
    category: "source",
    description: "Remux — a direct copy of the Blu-ray disc without re-encoding. The highest quality source available.",
    defaultScore: 0,
    patterns: ["\\bREMUX\\b"],
  },
  {
    trashId: "bfd8eb01832d646a0a89c4deb46f8564",
    name: "Blu-ray",
    category: "source",
    description: "Blu-ray source (encoded) — very high quality, significantly smaller than Remux.",
    defaultScore: 0,
    patterns: ["\\b(BluRay|Blu-Ray|BLURAY|BDRip)\\b"],
  },
  {
    trashId: "9de657fd3d327ecf144ec73dfe3a3e9a",
    name: "IMAX Enhanced",
    category: "source",
    description: "IMAX Enhanced — expanded aspect ratio, higher resolution for IMAX-certified scenes.",
    defaultScore: 800,
    patterns: ["\\bIMAX.?Enhanced\\b", "\\bDECH\\b", "\\bIMAX\\b.*\\bEnhanced\\b"],
  },
  {
    trashId: "4b900e171accbfb172729b63323f9d5e",
    name: "IMAX",
    category: "source",
    description: "IMAX version — filmed or presented in IMAX format.",
    defaultScore: 800,
    patterns: ["\\bIMAX\\b"],
  },
  {
    trashId: "3a4127d8aa781b44120d907f2cd62627",
    name: "Repack / Proper",
    category: "misc",
    description: "Proper or Repack — a corrected re-release, usually fixing technical issues with the original.",
    defaultScore: 5,
    patterns: ["\\b(PROPER|REPACK)\\b"],
  },
  {
    trashId: "4eee26f9ab44a27e8e4dd0a7a2f14697",
    name: "Repack2",
    category: "misc",
    description: "Second repack — slightly preferred over the first repack.",
    defaultScore: 6,
    patterns: ["\\bREPACK2\\b"],
  },
  {
    trashId: "9b64dff695c2115facf1b6ea59c9bd07",
    name: "Repack3",
    category: "misc",
    description: "Third repack — highest repack preference.",
    defaultScore: 7,
    patterns: ["\\bREPACK3\\b"],
  },

  /* ── Streaming Services ── */
  {
    trashId: "4e9a630db98d5391aec1368a0256e2fe",
    name: "Amazon Prime",
    category: "streaming",
    description: "Amazon Prime Video release — identified by AMZN tag.",
    defaultScore: 0,
    patterns: ["\\bAMZN\\b", "\\bAmazon\\b"],
  },
  {
    trashId: "cc5f3e7c8f2b61f4a56f0001a2a0daab",
    name: "Apple TV+",
    category: "streaming",
    description: "Apple TV+ release — identified by ATVP tag.",
    defaultScore: 0,
    patterns: ["\\bATVP\\b", "\\bApple.?TV\\b"],
  },
  {
    trashId: "4e9a630db98d5391aec1368a0256e2ef",
    name: "Netflix",
    category: "streaming",
    description: "Netflix original release — identified by NF tag.",
    defaultScore: 0,
    patterns: ["\\bNF\\b", "\\bNetflix\\b"],
  },
  {
    trashId: "bf7e73dd1d85b12cc527dc619761c840",
    name: "Disney+",
    category: "streaming",
    description: "Disney+ release — identified by DSNP or DSNY tag.",
    defaultScore: 0,
    patterns: ["\\b(DSNP|DSNY|Disney\\+)\\b"],
  },
  {
    trashId: "526d445d4c16214309f0fd2b3be18a89",
    name: "HBO Max",
    category: "streaming",
    description: "HBO Max / Max release — identified by HMAX or MAX tag.",
    defaultScore: 0,
    patterns: ["\\b(HMAX|MAX|HBO)\\b"],
  },
  {
    trashId: "2a6039655313bf5dab785a6e4b82f6e6",
    name: "Peacock",
    category: "streaming",
    description: "Peacock streaming release.",
    defaultScore: 0,
    patterns: ["\\bPCOK\\b", "\\bPeacock\\b"],
  },
  {
    trashId: "7a235133c87f7da4c8cccceca7e3c7a6",
    name: "Paramount+",
    category: "streaming",
    description: "Paramount+ streaming release.",
    defaultScore: 0,
    patterns: ["\\bPMTP\\b", "\\bParamount\\+?\\b"],
  },

  /* ── Editions ── */
  {
    trashId: "ade9a7a4b13e7e5b0f31b2f87f5e9e98",
    name: "Extended Edition",
    category: "edition",
    description: "Extended cut with additional scenes not in the theatrical release.",
    defaultScore: 0,
    patterns: ["\\bEXTENDED\\b"],
  },
  {
    trashId: "ae9b7c9ebde1f3bd336a8cbd1b5fbd67",
    name: "Director's Cut",
    category: "edition",
    description: "The director's preferred version of the film.",
    defaultScore: 0,
    patterns: ["\\bDIRECTOR'?S?.?CUT\\b", "\\bDC\\b"],
  },

  /* ── Release Groups ── */
  {
    trashId: "ed27ebfef2f323293b7b9c9c9b7e4f25",
    name: "HD Bluray Tier 01",
    category: "groups",
    description: "Top-tier trusted Blu-ray release groups — consistently excellent quality (FraMeSToR, BLURAYBITS, SiNNERS, etc.).",
    defaultScore: 1800,
    patterns: ["\\b(FraMeSToR|BLURAYBITS|SiNNERS|EPSiLON|TRiToN|iFT|decibeL|NCmt|DON|HiDt)\\b"],
  },
  {
    trashId: "a58f517a70193f8e578056642178419d",
    name: "HD Bluray Tier 02",
    category: "groups",
    description: "Second-tier trusted Blu-ray groups.",
    defaultScore: 1750,
    patterns: ["\\b(Flights|HANDJOB|BHDStudio|CRiSC|THORA|BluDragon|ZQ|D-Z0N3|W4NK3R|HQMUX)\\b"],
  },
  {
    trashId: "e61e28db84184857a8ca8e7e2e8d6ef2",
    name: "WEB Tier 01",
    category: "groups",
    description: "Top-tier trusted WEB-DL release groups — NTb, NTG, FLUX, etc.",
    defaultScore: 1700,
    patterns: ["\\b(NTb|NTG|FLUX|NPMS|KiNGS|MZABI|TEPES|GNOME|NOSiViD|XEBEC|TOMMY)\\b"],
  },
  {
    trashId: "58790d4e2fdcd9733aa7ae68ba2bb503",
    name: "WEB Tier 02",
    category: "groups",
    description: "Second-tier trusted WEB-DL groups.",
    defaultScore: 1650,
    patterns: ["\\b(AJP69|YIFY|YTS|GOLDENBEARD|PSA|CMRG|GHOST|SiC|GGEZ|BYNDR)\\b"],
  },

  /* ── Unwanted ── */
  {
    trashId: "b8cd450cbfa689c0259a01d9e29ba3d6",
    name: "3D",
    category: "unwanted",
    description: "3D releases — usually unwanted unless you have a 3D display. Heavily penalised by default.",
    defaultScore: -10000,
    patterns: ["\\b3D\\b", "\\bHSBS\\b", "\\bSBS\\b", "\\bHOU\\b"],
  },
  {
    trashId: "90a87bd7b54af7e7f0c5c5c5a6b2e9b3",
    name: "No Release Group",
    category: "unwanted",
    description: "Release has no group tag — often scene or low-quality encode. Penalise to prefer tagged releases.",
    defaultScore: -10000,
    patterns: ["-(?!\\w)$"],
  },
  {
    trashId: "ae9b7c9ebde1f3bd336a8cbd1b5fbd68",
    name: "Upscaled",
    category: "unwanted",
    description: "Artificially upscaled to 4K — not true 4K content. Should be avoided in 4K libraries.",
    defaultScore: -10000,
    patterns: ["\\bUpscale\\b", "\\bUpscaled\\b", "\\bAI.?Upscale\\b"],
  },
  {
    trashId: "cae4ca30163749d0b87284ca3be2980a",
    name: "LQ (Low Quality Groups)",
    category: "unwanted",
    description: "Releases from known low-quality groups — avoid these in favour of trusted tiers.",
    defaultScore: -10000,
    patterns: ["\\b(mSD|STUTTERSHIT|YIFY|YTS(?!\\s)|nhanc3|Sprinter|Ghost|0SEC|FRDS)\\b"],
  },
  {
    trashId: "dc98083864ea246d05a42df0d05f81cc",
    name: "Extras",
    category: "unwanted",
    description: "Extra content (featurettes, interviews, deleted scenes) — not the main feature.",
    defaultScore: -10000,
    patterns: ["\\b(Featurette|Interview|Deleted.?Scene|Behind.?the.?Scene|Extra|Bonus)\\b"],
  },
];

/* ── Quality preset profiles ─────────────────────────────────────── */

export interface QualityProfilePreset {
  id: string;
  name: string;
  tagline: string;
  description: string;
  /** User-visible bullet points */
  highlights: string[];
  mediaType: "movies" | "tv" | "anime";
  /** Ordered quality tier IDs — first is most preferred */
  qualityOrder: string[];
  /** The quality where upgrading stops */
  cutoffQualityId: string;
  upgradeAllowed: boolean;
  minFormatScore: number;
  cutoffFormatScore: number;
  /** CFs to add automatically with their recommended scores */
  recommendedCFs: Array<{ trashId: string; score: number }>;
}

export const QUALITY_PRESETS: QualityProfilePreset[] = [
  {
    id: "web-1080p",
    name: "1080p Streaming",
    tagline: "Great quality, small files",
    description: "The most popular choice. Grabs WEB-DL 1080p releases from Netflix, Amazon, Apple TV+ and other streaming services. Perfect balance of quality and storage.",
    highlights: [
      "WEB-DL 1080p from streaming platforms",
      "WEBRip as a fallback source",
      "Smaller file sizes than Blu-ray",
      "Works on all displays",
    ],
    mediaType: "movies",
    qualityOrder: ["webdl-1080p", "webrip-1080p", "webdl-720p", "hdtv-1080p"],
    cutoffQualityId: "webdl-1080p",
    upgradeAllowed: true,
    minFormatScore: 0,
    cutoffFormatScore: 10000,
    recommendedCFs: [
      { trashId: "3a4127d8aa781b44120d907f2cd62627", score: 5 },  // Repack/Proper
      { trashId: "4eee26f9ab44a27e8e4dd0a7a2f14697", score: 6 },  // Repack2
      { trashId: "9b64dff695c2115facf1b6ea59c9bd07", score: 7 },  // Repack3
      { trashId: "aadb0e9ebba39a5fcf97c5a6d5ed0cff", score: -10000 }, // x265 HD
      { trashId: "b8cd450cbfa689c0259a01d9e29ba3d6", score: -10000 }, // 3D
      { trashId: "dc98083864ea246d05a42df0d05f81cc", score: -10000 }, // Extras
    ],
  },
  {
    id: "bluray-1080p",
    name: "1080p HD Blu-ray + WEB",
    tagline: "Best 1080p quality",
    description: "Starts with WEB-DL 1080p, then automatically upgrades to Blu-ray 1080p when available. Recommended for cinephiles who want the best 1080p experience.",
    highlights: [
      "Grabs WEB-DL 1080p immediately",
      "Automatically upgrades to Blu-ray 1080p",
      "Trusted release groups scored highly",
      "Proper/Repack corrections preferred",
    ],
    mediaType: "movies",
    qualityOrder: ["bluray-1080p", "webdl-1080p", "webrip-1080p", "bluray-720p"],
    cutoffQualityId: "bluray-1080p",
    upgradeAllowed: true,
    minFormatScore: 0,
    cutoffFormatScore: 10000,
    recommendedCFs: [
      { trashId: "ed27ebfef2f323293b7b9c9c9b7e4f25", score: 1800 }, // HD Bluray Tier 01
      { trashId: "a58f517a70193f8e578056642178419d", score: 1750 }, // HD Bluray Tier 02
      { trashId: "e61e28db84184857a8ca8e7e2e8d6ef2", score: 1700 }, // WEB Tier 01
      { trashId: "58790d4e2fdcd9733aa7ae68ba2bb503", score: 1650 }, // WEB Tier 02
      { trashId: "3a4127d8aa781b44120d907f2cd62627", score: 5 },    // Repack/Proper
      { trashId: "4eee26f9ab44a27e8e4dd0a7a2f14697", score: 6 },    // Repack2
      { trashId: "9b64dff695c2115facf1b6ea59c9bd07", score: 7 },    // Repack3
      { trashId: "aadb0e9ebba39a5fcf97c5a6d5ed0cff", score: -10000 }, // x265 HD
      { trashId: "b8cd450cbfa689c0259a01d9e29ba3d6", score: -10000 }, // 3D
      { trashId: "cae4ca30163749d0b87284ca3be2980a", score: -10000 }, // LQ Groups
    ],
  },
  {
    id: "web-2160p",
    name: "4K Streaming",
    tagline: "4K from streaming platforms",
    description: "Grabs WEB-DL 4K (2160p) releases from Netflix, Amazon, Apple TV+ and others. Includes HDR content. Perfect for 4K displays without needing massive Remux files.",
    highlights: [
      "WEB-DL 4K from streaming platforms",
      "HDR10 and HDR10+ preferred",
      "Dolby Vision optional boost",
      "Much smaller than Blu-ray Remux",
    ],
    mediaType: "movies",
    qualityOrder: ["webdl-2160p", "webrip-2160p", "webdl-1080p", "webrip-1080p"],
    cutoffQualityId: "webdl-2160p",
    upgradeAllowed: true,
    minFormatScore: 0,
    cutoffFormatScore: 10000,
    recommendedCFs: [
      { trashId: "493b6d1dbec3c3364c59d7607f7e3405", score: 500 },   // HDR
      { trashId: "5d96ce331b98e077abb8ceb60553aa16", score: 520 },   // HDR10+
      { trashId: "e61e28db84184857a8ca8e7e2e8d6ef2", score: 1700 },  // WEB Tier 01
      { trashId: "58790d4e2fdcd9733aa7ae68ba2bb503", score: 1650 },  // WEB Tier 02
      { trashId: "3a4127d8aa781b44120d907f2cd62627", score: 5 },     // Repack/Proper
      { trashId: "4eee26f9ab44a27e8e4dd0a7a2f14697", score: 6 },     // Repack2
      { trashId: "9b64dff695c2115facf1b6ea59c9bd07", score: 7 },     // Repack3
      { trashId: "923b6abef9b17f937fab56cfcf89e1f1", score: -10000 }, // DV No Fallback
      { trashId: "b8cd450cbfa689c0259a01d9e29ba3d6", score: -10000 }, // 3D
    ],
  },
  {
    id: "remux-2160p",
    name: "4K Remux",
    tagline: "Uncompromising 4K quality",
    description: "The absolute best quality — a bit-for-bit copy of the 4K Blu-ray disc. Largest file sizes but perfect video and audio with all HDR formats and lossless audio.",
    highlights: [
      "Direct copy from 4K Blu-ray disc",
      "Lossless audio (TrueHD Atmos, DTS-HD MA)",
      "All HDR formats: DV, HDR10+, HDR10",
      "No re-encoding quality loss",
    ],
    mediaType: "movies",
    qualityOrder: ["remux-2160p", "bluray-2160p", "webdl-2160p", "webrip-2160p", "remux-1080p"],
    cutoffQualityId: "remux-2160p",
    upgradeAllowed: true,
    minFormatScore: 0,
    cutoffFormatScore: 10000,
    recommendedCFs: [
      { trashId: "493b6d1dbec3c3364c59d7607f7e3405", score: 500 },   // HDR
      { trashId: "5d96ce331b98e077abb8ceb60553aa16", score: 520 },   // HDR10+
      { trashId: "b337d6812e06c200ec9a2d3cfa9d20a7", score: 1000 },  // DV Boost
      { trashId: "caa37d0df9c348912df1fb1d88f9273a", score: 100 },   // HDR10+ Boost
      { trashId: "496f355514737f7d83bf7aa4d24f8169", score: 5 },     // TrueHD Atmos
      { trashId: "ca3e897c30fb3d15b3f643e8dfae1ce6", score: 3 },     // DTS-HD MA
      { trashId: "2f22d89048b01681dde8afe203bf2e95", score: 4 },     // DTS:X
      { trashId: "9de657fd3d327ecf144ec73dfe3a3e9a", score: 800 },   // IMAX Enhanced
      { trashId: "4b900e171accbfb172729b63323f9d5e", score: 800 },   // IMAX
      { trashId: "b8cd450cbfa689c0259a01d9e29ba3d6", score: -10000 }, // 3D
      { trashId: "cae4ca30163749d0b87284ca3be2980a", score: -10000 }, // LQ Groups
    ],
  },
  {
    id: "web-1080p-tv",
    name: "TV — 1080p Streaming",
    tagline: "Standard quality for TV series",
    description: "The recommended profile for TV shows. Grabs WEB-DL 1080p from streaming platforms. Same day as Netflix, Amazon, etc.",
    highlights: [
      "1080p WEB-DL from streaming platforms",
      "Season packs supported",
      "WEBRip as fallback",
      "Proper/Repack corrections preferred",
    ],
    mediaType: "tv",
    qualityOrder: ["webdl-1080p", "webrip-1080p", "webdl-720p"],
    cutoffQualityId: "webdl-1080p",
    upgradeAllowed: true,
    minFormatScore: 0,
    cutoffFormatScore: 10000,
    recommendedCFs: [
      { trashId: "e61e28db84184857a8ca8e7e2e8d6ef2", score: 1700 },  // WEB Tier 01
      { trashId: "58790d4e2fdcd9733aa7ae68ba2bb503", score: 1650 },  // WEB Tier 02
      { trashId: "3a4127d8aa781b44120d907f2cd62627", score: 5 },     // Repack/Proper
      { trashId: "4eee26f9ab44a27e8e4dd0a7a2f14697", score: 6 },     // Repack2
      { trashId: "9b64dff695c2115facf1b6ea59c9bd07", score: 7 },     // Repack3
      { trashId: "aadb0e9ebba39a5fcf97c5a6d5ed0cff", score: -10000 }, // x265 HD
      { trashId: "b8cd450cbfa689c0259a01d9e29ba3d6", score: -10000 }, // 3D
    ],
  },
  {
    id: "anime-1080p",
    name: "Anime — 1080p Remux",
    tagline: "Best quality for anime",
    description: "Optimised for anime releases. Prefers Blu-ray Remux sources from trusted anime groups. Supports both dual audio and Japanese audio with subtitles.",
    highlights: [
      "Blu-ray Remux from trusted anime groups",
      "Dual audio (EN/JP) preferred",
      "FLAC lossless audio scored",
      "x265 allowed (efficient for anime)",
    ],
    mediaType: "anime",
    qualityOrder: ["remux-1080p", "bluray-1080p", "webdl-1080p", "webrip-1080p"],
    cutoffQualityId: "remux-1080p",
    upgradeAllowed: true,
    minFormatScore: 0,
    cutoffFormatScore: 10000,
    recommendedCFs: [
      { trashId: "185f1dd7264c4562b9022d963ac37424", score: 5 },     // FLAC
      { trashId: "3a4127d8aa781b44120d907f2cd62627", score: 5 },     // Repack/Proper
      { trashId: "b8cd450cbfa689c0259a01d9e29ba3d6", score: -10000 }, // 3D
    ],
  },
];

/* ── Helper utilities ────────────────────────────────────────────── */

/** Find a preset by ID */
export function findPreset(id: string): QualityProfilePreset | undefined {
  return QUALITY_PRESETS.find((p) => p.id === id);
}

/** Find a bundled CF by its trash ID */
export function findBundledCF(trashId: string): BundledCF | undefined {
  return BUNDLED_CUSTOM_FORMATS.find((cf) => cf.trashId === trashId);
}

/** Get all CFs for a given category */
export function getCFsByCategory(category: CFCategory): BundledCF[] {
  return BUNDLED_CUSTOM_FORMATS.filter((cf) => cf.category === category);
}

/** Get all CFs recommended for a preset with their scores */
export function getPresetCFs(preset: QualityProfilePreset): Array<BundledCF & { score: number }> {
  return preset.recommendedCFs
    .map(({ trashId, score }) => {
      const cf = findBundledCF(trashId);
      return cf ? { ...cf, score } : null;
    })
    .filter((x): x is BundledCF & { score: number } => x !== null);
}

/** Ordered list of all CF categories */
export const CF_CATEGORY_ORDER: CFCategory[] = [
  "hdr", "codec", "audio", "channels", "source", "streaming", "edition", "groups", "anime", "language", "unwanted", "misc"
];

BUNDLED_CUSTOM_FORMATS.push(
  {
    trashId: "deluno-trash-audio-pcm",
    name: "PCM",
    category: "audio",
    description: "PCM/LPCM lossless audio.",
    defaultScore: 2,
    patterns: ["\\b(LPCM|PCM)\\b"],
  },
  {
    trashId: "deluno-trash-audio-dts-hra",
    name: "DTS-HD HRA",
    category: "audio",
    description: "DTS-HD High Resolution Audio.",
    defaultScore: 1,
    patterns: ["\\bDTS[-. ]?HD[-. ]?HRA\\b"],
  },
  {
    trashId: "deluno-trash-audio-ddplus",
    name: "DD+",
    category: "audio",
    description: "Dolby Digital Plus audio without Atmos.",
    defaultScore: 0,
    patterns: ["\\b(EAC3|DD\\+|Dolby.?Digital.?Plus)\\b(?!.*Atmos)"],
  },
  {
    trashId: "deluno-trash-audio-aac",
    name: "AAC",
    category: "audio",
    description: "AAC audio. Common for smaller WEB releases.",
    defaultScore: 0,
    patterns: ["\\bAAC\\b"],
  },
  {
    trashId: "deluno-trash-audio-dts",
    name: "DTS",
    category: "audio",
    description: "Standard DTS audio.",
    defaultScore: 0,
    patterns: ["\\bDTS\\b(?![-. ]?HD|[-. ]?X)"],
  },
  {
    trashId: "deluno-trash-audio-opus",
    name: "Opus",
    category: "audio",
    description: "Opus audio. Efficient, but not universally supported by every playback chain.",
    defaultScore: 0,
    patterns: ["\\bOpus\\b"],
  },
  {
    trashId: "deluno-trash-channel-mono",
    name: "1.0 Mono",
    category: "channels",
    description: "Mono audio. Useful for older titles, but usually not preferred for modern releases.",
    defaultScore: -50,
    patterns: ["\\b1\\.0\\b", "\\bMono\\b"],
  },
  {
    trashId: "deluno-trash-channel-stereo",
    name: "2.0 Stereo",
    category: "channels",
    description: "Stereo audio.",
    defaultScore: 0,
    patterns: ["\\b2\\.0\\b", "\\bStereo\\b"],
  },
  {
    trashId: "deluno-trash-channel-51",
    name: "5.1 Surround",
    category: "channels",
    description: "5.1 surround audio.",
    defaultScore: 1,
    patterns: ["\\b5\\.1\\b"],
  },
  {
    trashId: "deluno-trash-channel-71",
    name: "7.1 Surround",
    category: "channels",
    description: "7.1 surround audio.",
    defaultScore: 2,
    patterns: ["\\b7\\.1\\b"],
  },
  {
    trashId: "deluno-trash-hdr-dv-disk",
    name: "DV Disk",
    category: "hdr",
    description: "Dolby Vision from disc/remux sources. Prefer only when your playback chain handles DV correctly.",
    defaultScore: 0,
    patterns: ["\\b(DV|DoVi)\\b.*\\b(REMUX|BluRay|Blu-Ray)\\b", "\\b(REMUX|BluRay|Blu-Ray)\\b.*\\b(DV|DoVi)\\b"],
  },
  {
    trashId: "deluno-trash-hdr-sdr",
    name: "SDR",
    category: "hdr",
    description: "Standard dynamic range release. Useful when you want non-HDR compatibility.",
    defaultScore: 0,
    patterns: ["\\bSDR\\b"],
  },
  {
    trashId: "deluno-trash-edition-hybrid",
    name: "Hybrid",
    category: "edition",
    description: "Hybrid release assembled from multiple sources. Often desirable when done by trusted groups.",
    defaultScore: 0,
    patterns: ["\\bHybrid\\b"],
  },
  {
    trashId: "deluno-trash-edition-remaster",
    name: "Remaster",
    category: "edition",
    description: "Remastered release.",
    defaultScore: 0,
    patterns: ["\\bRemaster(ed)?\\b"],
  },
  {
    trashId: "deluno-trash-edition-criterion",
    name: "Criterion Collection",
    category: "edition",
    description: "Criterion Collection release.",
    defaultScore: 0,
    patterns: ["\\bCriterion\\b"],
  },
  {
    trashId: "deluno-trash-edition-open-matte",
    name: "Open Matte",
    category: "edition",
    description: "Open matte presentation with more vertical image area.",
    defaultScore: 0,
    patterns: ["\\bOpen.?Matte\\b"],
  },
  {
    trashId: "deluno-trash-groups-remux-tier-01",
    name: "Remux Tier 01",
    category: "groups",
    description: "Top-tier remux groups. Best used inside premium/remux profiles.",
    defaultScore: 1900,
    patterns: ["\\b(FraMeSToR|EPSiLON|TRiToN|BLURANiUM|PmP|KRaLiMaRKo|WiLDCAT|HiFi)\\b"],
  },
  {
    trashId: "deluno-trash-groups-remux-tier-02",
    name: "Remux Tier 02",
    category: "groups",
    description: "Second-tier remux groups.",
    defaultScore: 1850,
    patterns: ["\\b(PTer|DecibeL|NCmt|iFT|CtrlHD|BMF|ZQ)\\b"],
  },
  {
    trashId: "deluno-trash-groups-uhd-bluray-tier-01",
    name: "UHD Bluray Tier 01",
    category: "groups",
    description: "Top-tier UHD Blu-ray encode groups.",
    defaultScore: 1800,
    patterns: ["\\b(DON|HQMUX|TayTO|EbP|CRiSC|CtrlHD|NTb)\\b"],
  },
  {
    trashId: "deluno-trash-groups-web-tier-03",
    name: "WEB Tier 03",
    category: "groups",
    description: "Third-tier WEB groups. Useful as a fallback, not a primary preference.",
    defaultScore: 1600,
    patterns: ["\\b(MiU|MEMENTO|EDITH|LAZY|PECULATE|SMURF|HONE|TrollHD|TEPES)\\b"],
  },
  {
    trashId: "deluno-trash-streaming-hulu",
    name: "Hulu",
    category: "streaming",
    description: "Hulu streaming release.",
    defaultScore: 0,
    patterns: ["\\bHULU\\b"],
  },
  {
    trashId: "deluno-trash-streaming-itunes",
    name: "iTunes",
    category: "streaming",
    description: "iTunes source tag.",
    defaultScore: 0,
    patterns: ["\\b(iT|iTunes)\\b"],
  },
  {
    trashId: "deluno-trash-streaming-roku",
    name: "Roku",
    category: "streaming",
    description: "Roku Channel streaming release.",
    defaultScore: 0,
    patterns: ["\\bROKU\\b"],
  },
  {
    trashId: "deluno-trash-streaming-crunchyroll",
    name: "Crunchyroll",
    category: "streaming",
    description: "Crunchyroll anime streaming release.",
    defaultScore: 0,
    patterns: ["\\b(CR|Crunchyroll)\\b"],
  },
  {
    trashId: "deluno-trash-streaming-hidive",
    name: "HIDIVE",
    category: "streaming",
    description: "HIDIVE anime streaming release.",
    defaultScore: 0,
    patterns: ["\\bHIDIVE\\b"],
  },
  {
    trashId: "deluno-trash-unwanted-br-disk",
    name: "BR-DISK",
    category: "unwanted",
    description: "Full Blu-ray disc folder/image instead of a normal media file. Usually not wanted for library automation.",
    defaultScore: -10000,
    patterns: ["\\b(BD25|BD50|BD66|BD100|BR.?DISK|Blu.?ray.?Disk)\\b"],
  },
  {
    trashId: "deluno-trash-unwanted-generated-dynamic-hdr",
    name: "Generated Dynamic HDR",
    category: "unwanted",
    description: "Generated or converted dynamic HDR metadata. Avoid unless explicitly wanted.",
    defaultScore: -10000,
    patterns: ["\\b(DV.?HDR10|Generated.?HDR|Dynamic.?HDR)\\b"],
  },
  {
    trashId: "deluno-trash-unwanted-obfuscated",
    name: "Obfuscated",
    category: "unwanted",
    description: "Obfuscated release names. Usually harder to identify and import cleanly.",
    defaultScore: -10000,
    patterns: ["\\b(Obfuscated|Scrambled|Randomized)\\b"],
  },
  {
    trashId: "deluno-trash-unwanted-retags",
    name: "Retags",
    category: "unwanted",
    description: "Retagged releases that only change the release group tag.",
    defaultScore: -10000,
    patterns: ["\\b(RETAG|Re.?tag)\\b"],
  },
  {
    trashId: "deluno-trash-unwanted-line-mic-dubbed",
    name: "Line / Mic Dubbed",
    category: "unwanted",
    description: "Line, mic, or dubbed cinema audio. Avoid for normal libraries.",
    defaultScore: -10000,
    patterns: ["\\b(LiNE|LINE|Mic|MIC|Dubbed)\\b"],
  },
  {
    trashId: "deluno-trash-unwanted-hfr",
    name: "HFR",
    category: "unwanted",
    description: "High-frame-rate releases. Neutral by default because this is user preference.",
    defaultScore: 0,
    patterns: ["\\b(HFR|48FPS|60FPS)\\b"],
  },
  {
    trashId: "deluno-trash-misc-720p",
    name: "720p",
    category: "misc",
    description: "Resolution tag for 720p releases.",
    defaultScore: 0,
    patterns: ["\\b720p\\b"],
  },
  {
    trashId: "deluno-trash-misc-1080p",
    name: "1080p",
    category: "misc",
    description: "Resolution tag for 1080p releases.",
    defaultScore: 0,
    patterns: ["\\b1080p\\b"],
  },
  {
    trashId: "deluno-trash-misc-2160p",
    name: "2160p",
    category: "misc",
    description: "Resolution tag for 4K / 2160p releases.",
    defaultScore: 0,
    patterns: ["\\b(2160p|4K|UHD)\\b"],
  },
  {
    trashId: "deluno-trash-anime-bd-tier-01",
    name: "Anime BD Tier 01",
    category: "anime",
    description: "Top anime Blu-ray groups. Use in anime profiles.",
    defaultScore: 1600,
    patterns: ["\\b(Beatrice-Raws|sam|SCY|ZQ|Kawaiika-Raws|VCB-Studio)\\b"],
  },
  {
    trashId: "deluno-trash-anime-web-tier-01",
    name: "Anime WEB Tier 01",
    category: "anime",
    description: "Top anime WEB groups. Use in anime streaming profiles.",
    defaultScore: 1500,
    patterns: ["\\b(SubsPlease|Erai-raws|ASW|GJM|EMBER|Judas)\\b"],
  },
  {
    trashId: "deluno-trash-anime-raws",
    name: "Anime Raws",
    category: "anime",
    description: "Raw anime release without subtitles.",
    defaultScore: -10000,
    patterns: ["\\b(RAW|Raws)\\b"],
  },
  {
    trashId: "deluno-trash-anime-uncensored",
    name: "Uncensored",
    category: "anime",
    description: "Uncensored anime release.",
    defaultScore: 100,
    patterns: ["\\bUncensored\\b"],
  },
  {
    trashId: "deluno-trash-anime-10bit",
    name: "10bit",
    category: "anime",
    description: "10-bit anime encode.",
    defaultScore: 0,
    patterns: ["\\b(10bit|10-bit|Hi10P)\\b"],
  },
  {
    trashId: "deluno-trash-anime-dual-audio",
    name: "Anime Dual Audio",
    category: "anime",
    description: "Anime release with more than one audio language.",
    defaultScore: 100,
    patterns: ["\\b(Dual.?Audio|Multi.?Audio)\\b"],
  },
  {
    trashId: "deluno-trash-language-not-english",
    name: "Not English",
    category: "language",
    description: "Release is not English. Useful for English-only profiles.",
    defaultScore: -10000,
    patterns: ["\\b(FRENCH|GERMAN|SPANISH|ITALIAN|DUTCH|RUSSIAN|PORTUGUESE)\\b"],
  },
  {
    trashId: "deluno-trash-language-french",
    name: "French Audio",
    category: "language",
    description: "French audio/version tags such as VFF, VFQ, VOSTFR, FanSUB and FastSUB.",
    defaultScore: 0,
    patterns: ["\\b(VFF|VOF|VFI|VF2|VFQ|VOQ|VQ|VFB|VOSTFR|FanSUB|FastSUB)\\b"],
  },
  {
    trashId: "deluno-trash-language-german",
    name: "German Audio",
    category: "language",
    description: "German or German dual-language release.",
    defaultScore: 0,
    patterns: ["\\b(German|Deutsch|GERMAN.?DL|DL.?German)\\b"],
  },
);

export const CUSTOM_FORMAT_BUNDLES: CustomFormatBundle[] = [
  {
    id: "starter-streaming-1080p",
    name: "Simple 1080p Streaming",
    level: "starter",
    mediaType: "all",
    description: "A safe default for most users. Prefer clean WEB releases, block obvious bad releases, and avoid fragile HDR rules.",
    bestFor: "First setup, small libraries, families, laptops, and users who just want reliable 1080p playback.",
    includes: [
      { trashId: "deluno-trash-misc-1080p" },
      { trashId: "deluno-trash-streaming-itunes" },
      { trashId: "deluno-trash-streaming-hulu" },
      { trashId: "deluno-trash-unwanted-br-disk" },
      { trashId: "deluno-trash-unwanted-obfuscated" },
      { trashId: "deluno-trash-unwanted-retags" },
      { trashId: "deluno-trash-unwanted-line-mic-dubbed" },
      { trashId: "aadb0e9ebba39a5fcf97c5a6d5ed0cff" }
    ]
  },
  {
    id: "balanced-1080p",
    name: "Balanced 1080p",
    level: "balanced",
    mediaType: "all",
    description: "A better everyday profile with sensible audio, trusted WEB groups, and common bad-release blocking.",
    bestFor: "Main libraries where quality matters but storage should not explode.",
    includes: [
      { trashId: "deluno-trash-misc-1080p" },
      { trashId: "deluno-trash-groups-web-tier-03", score: 1200 },
      { trashId: "496f355514737f7d83bf7aa4d24f8169" },
      { trashId: "1c1a4c5e823891c75bc50380a6866f73" },
      { trashId: "deluno-trash-audio-ddplus" },
      { trashId: "deluno-trash-channel-51" },
      { trashId: "deluno-trash-unwanted-br-disk" },
      { trashId: "deluno-trash-unwanted-obfuscated" },
      { trashId: "deluno-trash-unwanted-retags" },
      { trashId: "deluno-trash-unwanted-line-mic-dubbed" }
    ]
  },
  {
    id: "premium-4k-streaming",
    name: "Premium 4K Streaming",
    level: "premium",
    mediaType: "all",
    description: "Prioritises 2160p WEB releases, HDR, good audio, and clean source tags without forcing full remux sizes.",
    bestFor: "4K televisions, Apple TV / Shield / Plex users, and users who want a premium library without huge files.",
    includes: [
      { trashId: "deluno-trash-misc-2160p" },
      { trashId: "493b6d1dbec3c3364c59d7607f7e3405" },
      { trashId: "7878c33f1963fefb3d6c8657d46c2f0a" },
      { trashId: "5d96ce331b98e077abb8ceb60553aa16" },
      { trashId: "b337d6812e06c200ec9a2d3cfa9d20a7" },
      { trashId: "deluno-trash-hdr-sdr" },
      { trashId: "deluno-trash-groups-web-tier-03" },
      { trashId: "deluno-trash-audio-ddplus" },
      { trashId: "deluno-trash-channel-51" },
      { trashId: "deluno-trash-unwanted-generated-dynamic-hdr" },
      { trashId: "deluno-trash-unwanted-br-disk" },
      { trashId: "deluno-trash-unwanted-obfuscated" }
    ],
    warnings: [
      "Dolby Vision is boosted. If your display does not support it, keep the no-fallback blocker enabled."
    ]
  },
  {
    id: "archive-4k-remux",
    name: "Archive 4K Remux",
    level: "premium",
    mediaType: "movies",
    description: "Large-file movie archive preset. Prefers remux, UHD Blu-ray groups, lossless audio, HDR, and special editions.",
    bestFor: "Reference movie libraries, home cinema rooms, and users who care more about quality than disk usage.",
    includes: [
      { trashId: "deluno-trash-misc-2160p" },
      { trashId: "493b6d1dbec3c3364c59d7607f7e3405" },
      { trashId: "b337d6812e06c200ec9a2d3cfa9d20a7" },
      { trashId: "deluno-trash-groups-remux-tier-01" },
      { trashId: "deluno-trash-groups-remux-tier-02" },
      { trashId: "deluno-trash-groups-uhd-bluray-tier-01" },
      { trashId: "496f355514737f7d83bf7aa4d24f8169" },
      { trashId: "2f22d89048b01681dde8afe203bf2e95" },
      { trashId: "deluno-trash-audio-dts-hra" },
      { trashId: "deluno-trash-channel-71" },
      { trashId: "deluno-trash-edition-criterion" },
      { trashId: "deluno-trash-edition-remaster" },
      { trashId: "deluno-trash-unwanted-br-disk" },
      { trashId: "deluno-trash-unwanted-obfuscated" },
      { trashId: "deluno-trash-unwanted-retags" }
    ],
    warnings: [
      "This can create very large files. Pair it with a dedicated movie root or storage policy."
    ]
  },
  {
    id: "anime-balanced",
    name: "Anime Balanced",
    level: "specialist",
    mediaType: "tv",
    description: "Anime-specific release groups, dual-audio preference, uncensored boost, and raw-release blocking.",
    bestFor: "Anime libraries where normal Sonarr-style scoring is not enough.",
    includes: [
      { trashId: "deluno-trash-anime-web-tier-01" },
      { trashId: "deluno-trash-anime-bd-tier-01" },
      { trashId: "deluno-trash-anime-dual-audio" },
      { trashId: "deluno-trash-anime-uncensored" },
      { trashId: "deluno-trash-anime-10bit" },
      { trashId: "deluno-trash-streaming-crunchyroll" },
      { trashId: "deluno-trash-streaming-hidive" },
      { trashId: "deluno-trash-anime-raws" },
      { trashId: "deluno-trash-unwanted-obfuscated" }
    ]
  },
  {
    id: "storage-saver",
    name: "Storage Saver",
    level: "starter",
    mediaType: "all",
    description: "Keeps quality sane while discouraging remux-sized and very high bitrate releases.",
    bestFor: "NAS storage limits, remote users, slower upload links, or libraries with many users.",
    includes: [
      { trashId: "deluno-trash-misc-1080p" },
      { trashId: "aadb0e9ebba39a5fcf97c5a6d5ed0cff" },
      { trashId: "deluno-trash-unwanted-br-disk" },
      { trashId: "deluno-trash-unwanted-obfuscated" },
      { trashId: "deluno-trash-unwanted-retags" },
      { trashId: "deluno-trash-unwanted-generated-dynamic-hdr" },
      { trashId: "deluno-trash-unwanted-hfr", score: -100 }
    ]
  }
];
