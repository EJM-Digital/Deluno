import { type FormEvent, type ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { ArtworkImage } from "../components/ui/artwork-image";
import { Link, useLoaderData, useNavigate, useSearchParams } from "react-router-dom";
import {
  ArrowLeft,
  ArrowRight,
  CheckCircle2,
  Clock,
  DownloadCloud,
  FolderTree,
  Globe,
  Loader2,
  MonitorSmartphone,
  Plus,
  RadioTower,
  RefreshCw,
  Rocket,
  Search,
  Settings2,
  ShieldCheck,
  Sparkles,
  UserCircle2,
  Wifi,
  X,
  type LucideIcon
} from "lucide-react";
import {
  emptyPlatformSettingsSnapshot,
  fetchTrashGuidePackage,
  fetchJson,
  type ConnectionTestResponse,
  type DownloadClientItem,
  type IndexerItem,
  type LibraryItem,
  type ProcessorConnectionItem,
  type MetadataProviderStatus,
  type MetadataSearchResult,
  type PlatformSettingsSnapshot,
  type SetupProgressItem,
  type SetupDraftItem,
  type QualityProfileItem,
  type CustomFormatItem,
  type GuideCustomFormat,
  type GuideFormatBundle,
  type GuidePackage,
  type AllowedHostsOverview,
  type LocalNetworkAuthOverview,
  type ContentBlocklistSettings,
  type DownloadHealthRule,
  type UpdatePreferencesResponse,
  emptyContentBlocklistSettings,
  readErrorText,
  type BundledComponentStatus } from "../lib/api";
import { metadataCreatePayload } from "../hooks/use-library-create";
import { createRuleFromGuide, guideBundleTargets } from "../lib/guide-formats";
import { samePath } from "../lib/library-paths";
import { authedFetch, useAuth } from "../lib/use-auth";
import { addressForOtherDevices, cn, plainAddress } from "../lib/utils";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { Checkbox, CheckboxRow } from "../components/ui/checkbox";
import { Field, FieldRow } from "../components/ui/field";
import { PathInput } from "../components/ui/path-input";
import { Select } from "../components/ui/select";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Badge } from "../components/ui/badge";
import { Chip } from "../components/ui/chip";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { SummaryStrip } from "../components/ui/summary-strip";
import { LanAccessNotice } from "../components/app/lan-access-notice";
import { ConnectInstalledApps } from "../components/app/connect-installed-apps";

/**
 * #1034: two stages. Stage 1 ("This computer and you") covers the computer
 * and how it is reached, before anything about media. Stage 2 ("Deluno
 * itself") is the library/media configuration the guide has always asked.
 */
type StepId =
  | "account"
  | "timeRegion"
  | "signIn"
  | "addresses"
  | "network"
  | "updates"
  | "mode"
  | "folders"
  | "quality"
  | "services"
  | "connectApps"
  | "automation"
  | "finish";

/** One Yes/No question on the Automation step. Nothing is pre-selected (#936). */
type AutomationChoiceKey =
  | "startAutomation"
  | "searchMissingTitles"
  | "rssGrabs"
  | "qualityUpgrades"
  | "renameOnImport"
  | "subtitleFetching"
  | "removeBadDownloads"
  | "deleteBadDownloadFiles"
  | "stallStrikes"
  | "leftoverCleanup"
  | "leftoverDownloadCleanup"
  | "forceImport"
  | "pauseSearchWhenQueueLong"
  | "dangerousFileCheck";

export type AutomationChoices = Record<AutomationChoiceKey, boolean | null>;

const AUTOMATION_UNANSWERED: AutomationChoices = {
  startAutomation: null,
  searchMissingTitles: null,
  rssGrabs: null,
  qualityUpgrades: null,
  renameOnImport: null,
  subtitleFetching: null,
  removeBadDownloads: null,
  deleteBadDownloadFiles: null,
  stallStrikes: null,
  leftoverCleanup: null,
  leftoverDownloadCleanup: null,
  forceImport: null,
  pauseSearchWhenQueueLong: null,
  dangerousFileCheck: null
};

/** The numbers the stall question shows and, on a Yes, switches the rules on with (#1194). All changeable on Download Health. */
export const STALL_STRIKE_COUNT = 3;
export const STALL_SPEED_LIMIT_KBPS = 50;
export const STALL_CHECK_EVERY_MINUTES = 5;

export const AUTOMATION_QUESTIONS: { key: AutomationChoiceKey; question: string; help: string }[] = [
  {
    key: "startAutomation",
    question: "Should Deluno start searching and downloading on its own now?",
    help: "If off, automation stays paused: nothing is searched or tracked in the background until you press Resume automation. The Dashboard says so while it is paused."
  },
  {
    key: "searchMissingTitles",
    question: "Search for missing titles on its own?",
    help: "If off, a title stays missing until you search for it yourself."
  },
  {
    key: "rssGrabs",
    question: "Watch your search sources and grab new releases as they appear?",
    help: "If off, nothing downloads until you search for it yourself."
  },
  {
    key: "qualityUpgrades",
    question: "Look for a better version once you already have a title?",
    help: "If off, Deluno keeps the version you already have."
  },
  {
    key: "renameOnImport",
    question: "Rename files to match your naming format when they come in?",
    help: "If off, files keep the name they arrived with."
  },
  {
    key: "subtitleFetching",
    question: "Fetch subtitles on its own?",
    help: "If off, you add subtitles yourself."
  },
  {
    key: "removeBadDownloads",
    question: "Remove a bad download from the download client on its own?",
    help: "A download is judged bad after it fails Deluno's own checks."
  },
  {
    key: "deleteBadDownloadFiles",
    question: "Delete a bad download's files on its own?",
    help: "Only applies once a download has already been judged bad."
  },
  {
    key: "stallStrikes",
    question: `Remove downloads that stall or crawl, after ${STALL_STRIKE_COUNT} strikes?`,
    help: `Every ${STALL_CHECK_EVERY_MINUTES} minutes Deluno checks each download it sent. A check that finds nothing arriving, or a speed under ${STALL_SPEED_LIMIT_KBPS} KB/s, is a strike; a check that finds it moving again clears the strikes. At ${STALL_STRIKE_COUNT} strikes the download is removed from the client, the release is blocked and Deluno searches for another. If off, nothing is struck. You can change the strike count, the speed limit and the check interval later in Automation, Download health.`
  },
  {
    key: "leftoverCleanup",
    question: "Once an import removes the downloaded file, remove the folder it leaves empty?",
    help: "Only matters for a library also set to remove the downloaded file after import (Processing). If off, or that library keeps the file instead, empty folders are left for you to remove."
  },
  {
    key: "leftoverDownloadCleanup",
    question: "Automatically delete leftover download files?",
    help: "A leftover is a download-folder file nothing is using any more (no client, import or hand-off) once it has sat for a while. If off, leftovers stay on the leftovers list in Transfers for you to remove."
  },
  {
    key: "forceImport",
    question: "Import a download anyway when Deluno could not read the file to check it?",
    help: "Only after the import has already stopped once, and only for a file Deluno could not read (a share that dropped, a lock, a slow disk) - never one it read and found wrong, and never onto a file you already have. If off, that download stays stopped for you to look at."
  },
  {
    key: "pauseSearchWhenQueueLong",
    question: "Pause automatic searching while many downloads are waiting?",
    help: "If off, Deluno keeps searching however long the download queue is. If on, it pauses above 20 waiting downloads (changeable in Search cycles) and restarts by itself. Searches you start yourself always run."
  },
  {
    key: "dangerousFileCheck",
    question: "Check the files inside a download for anything dangerous?",
    help: "Checks every file inside a download - not just its name - against a blocklist of executables, scripts and shortcuts. If off, a dangerous file packaged inside an otherwise normal-looking release can slip through."
  }
];

interface SetupGuideLoaderData {
  settings: PlatformSettingsSnapshot;
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  indexers: IndexerItem[];
  clients: DownloadClientItem[];
  metadataStatus: MetadataProviderStatus | null;
  progress: SetupProgressItem;
  draft: SetupDraftItem;
  guide: GuidePackage;
  allowedHosts: AllowedHostsOverview;
  contentBlocklist: ContentBlocklistSettings;
  localNetworkAuth: LocalNetworkAuthOverview;
  updatePreferences: UpdatePreferencesResponse;
}

interface GuideForm {
  mode: "simple" | "advanced";
  mediaIntent: "movies" | "tv" | "both";
  movieRootPath: string;
  seriesRootPath: string;
  /** Optional: where the download clients finish Movies / TV. Blank means Deluno reads it from the connected clients. */
  movieDownloadsPath: string;
  seriesDownloadsPath: string;
  qualityPreset: "" | "balanced1080p" | "premium4k";
  formatGoal: "" | "simpleClean" | "balanced" | "homeTheater" | "storageSaver" | "anime" | "archive";
  indexerName: string;
  indexerProtocol: "torznab" | "newznab" | "rss";
  indexerUrl: string;
  indexerApiKey: string;
  clientName: string;
  clientProtocol: "qbittorrent" | "sabnzbd" | "transmission" | "deluge" | "nzbget" | "utorrent";
  clientHost: string;
  clientPort: string;
  clientUsername: string;
  clientPassword: string;
  backupEnabled: boolean;
  firstTitleType: "movies" | "tv";
  firstTitle: string;
  firstTitleYear: string;
  firstTitleMonitored: boolean;
  firstTitleMetadata: MetadataSearchResult | null;
  automation: AutomationChoices;
  /** Asked per library type once Weir is connected: "Clean files with Weir before they go into your library?" Unanswered by default (#936). */
  refineMovies: boolean | null;
  refineTv: boolean | null;
  /** Where Weir leaves the cleaned files for each library; only asked, and only used, when the answer above is Yes. */
  movieProcessedPath: string;
  seriesProcessedPath: string;
  /** #939: "Only answer on the addresses you use?" Unanswered by default (#936, #1245); only a Yes is written. */
  restrictAllowedHosts: boolean | null;
  allowedHostsSelection: string[];
  /** #993: "Skip sign-in for devices on your home network?" Unanswered by default (#936), same as Automation. */
  skipSignInLocalNetwork: boolean | null;
  localNetworkAllowList: string[];
  /** #1034 Stage 1 "Time and region": detected from the browser/OS, confirm or change. Reuses the same platform-settings fields Settings -> Interface already edits. */
  uiLanguage: string;
  calendarFirstDayOfWeek: string;
  shortDateFormat: string;
  longDateFormat: string;
  timeFormat: string;
  timezone: string;
  /** #1034 Stage 1 "Updates": unanswered by default (#936). Reuses /api/updates/preferences. */
  updateAutomatically: boolean | null;
  /**
   * #1245: on a re-run, the answers as they stood when the guide opened, so each question can say "Current setting".
   * Never edited; null on a fresh install, where nothing is decided for the person.
   */
  savedAnswers: SavedAnswers | null;
}

/** The Yes/No answers as they stood when a finished guide was opened again (#1245). */
export interface SavedAnswers {
  restrictAllowedHosts: boolean;
  skipSignInLocalNetwork: boolean;
  updateAutomatically: boolean;
  automation: AutomationChoices;
  refineMovies: boolean | null;
  refineTv: boolean | null;
}

type ServiceTestState = {
  indexer: "idle" | "testing" | "passed" | "failed";
  client: "idle" | "testing" | "passed" | "failed";
  indexerMessage: string | null;
  clientMessage: string | null;
};

type CreatedEntity = {
  kind: "library" | "indexer" | "client" | "qualityProfile" | "customFormat" | "movie" | "series";
  id: string;
};

type SetupCompletion = {
  message: string;
  libraries: string[];
  qualityProfiles: string[];
  customFormatCount: number;
  indexerName: string | null;
  clientName: string | null;
  firstTitle: string | null;
  firstTitlePath: string | null;
};

/** Whether the step's Yes/No questions each have an answer. Continue stays disabled until they do (#1245). */
export function stepIsAnswered(stepId: string, form: GuideForm, weirConnected = false): boolean {
  switch (stepId) {
    case "signIn": return form.skipSignInLocalNetwork !== null;
    case "addresses": return form.restrictAllowedHosts !== null;
    case "updates": return form.updateAutomatically !== null;
    case "connectApps": return missingWeirAnswers(form, weirConnected).length === 0;
    case "automation": return hasAnsweredAllAutomationQuestions(form.automation);
    default: return true;
  }
}

/** Start stays disabled until every Automation question has an explicit Yes or No (#936). */
export function hasAnsweredAllAutomationQuestions(automation: AutomationChoices): boolean {
  return AUTOMATION_QUESTIONS.every((item) => automation[item.key] !== null);
}

/**
 * The global platform-settings fields the Automation step's answers map to.
 * Every one of these keys reads "off" when unset (#936), so an unanswered
 * item (null) is treated as off here too — the caller is expected to have
 * already refused to finish setup until every item has a real answer.
 */
export function buildAutomationSettingsPayload(automation: AutomationChoices) {
  return {
    // The plain "start now?" answer is the scheduler switch itself. A Yes to
    // the individual searches below only says what runs once it is started.
    autoStartJobs: automation.startAutomation === true,
    renameOnImport: automation.renameOnImport === true,
    cleanupRemoveClientEntryAfterThreshold: automation.removeBadDownloads === true || automation.stallStrikes === true,
    // #1194: a Yes to the stall question is what switches the stall and slow-speed rules on, with the numbers it showed.
    cleanupBlockReleaseAfterThreshold: automation.stallStrikes === true,
    cleanupQueueReplacementAfterThreshold: automation.stallStrikes === true,
    downloadHealthNoProgressEnabled: automation.stallStrikes === true,
    downloadHealthMinSpeedEnabled: automation.stallStrikes === true,
    downloadHealthMinSpeedKbps: STALL_SPEED_LIMIT_KBPS,
    downloadHealthStrikeThreshold: STALL_STRIKE_COUNT,
    downloadHealthDefaultStrikeWindowMinutes: STALL_CHECK_EVERY_MINUTES,
    cleanupPurgePayloadAfterThreshold: automation.deleteBadDownloadFiles === true,
    subtitleFetchAutomaticEnabled: automation.subtitleFetching === true,
    // #932: the age threshold is not asked here - it keeps its own 24-hour
    // default (set in Settings -> Media Management -> Cleanup) whichever way
    // this question is answered.
    leftoverDownloadCleanupEnabled: automation.leftoverDownloadCleanup === true,
    // #1013: the tries keep their own default of 3 (Settings -> Media
    // Management) whichever way this question is answered.
    forceImportEnabled: automation.forceImport === true,
    // #1002: the pause limit keeps its own default of 20 (Search cycles).
    pauseSearchWhenQueueLongEnabled: automation.pauseSearchWhenQueueLong === true
  };
}

/**
 * The per-library fields a new library is created with (#936). Leftover-folder
 * cleanup lands on LibraryItem.RemoveEmptySourceFolders, the switch import
 * actually reads (Processing -> that library's workflow) -- not the global
 * CleanupEmptyFolders flag, which nothing reads and #736 already removed from
 * the UI.
 */
export function buildLibraryAutomationFields(automation: AutomationChoices) {
  return {
    autoSearchEnabled: automation.searchMissingTitles === true || automation.qualityUpgrades === true,
    missingSearchEnabled: automation.searchMissingTitles === true,
    upgradeSearchEnabled: automation.qualityUpgrades === true,
    removeEmptySourceFolders: automation.leftoverCleanup === true
  };
}

/** The per-indexer search fields a new indexer is created with (#936). */
export function buildIndexerAutomationFields(automation: AutomationChoices) {
  return {
    rssEnabled: automation.rssGrabs === true,
    automaticSearchEnabled: automation.searchMissingTitles === true || automation.qualityUpgrades === true
  };
}

function toSetupDraft(form: GuideForm) {
  return {
    mode: form.mode, mediaIntent: form.mediaIntent, movieRootPath: form.movieRootPath, seriesRootPath: form.seriesRootPath,
    movieDownloadsPath: form.movieDownloadsPath, seriesDownloadsPath: form.seriesDownloadsPath,
    qualityPreset: form.qualityPreset, formatGoal: form.formatGoal,
    indexerName: form.indexerName, indexerProtocol: form.indexerProtocol, indexerUrl: form.indexerUrl,
    clientName: form.clientName, clientProtocol: form.clientProtocol, clientHost: form.clientHost, clientPort: form.clientPort,
    backupEnabled: form.backupEnabled,
    firstTitleType: form.firstTitleType, firstTitle: form.firstTitle, firstTitleYear: form.firstTitleYear,
    firstTitleMonitored: form.firstTitleMonitored,
    // The Yes/No answers are saved too, so a reload restores them (#1132). Unanswered stays out.
    skipSignInLocalNetwork: form.skipSignInLocalNetwork,
    updateAutomatically: form.updateAutomatically,
    automation: { ...answeredAutomation(form.automation), ...answeredRefine(form) }
  };
}

/** Where Weir would leave cleaned files by default: its own folder on the same drive as the library. Blank when the library has no drive letter. */
export function suggestedProcessedFolder(rootPath: string, label: "Movies" | "TV"): string {
  const drive = /^([A-Za-z]:)[\\/]/.exec(rootPath.trim());
  return drive ? `${drive[1]}\\Weir\\Ready\\${label}` : "";
}

/** The refine-with-Weir answers that have an explicit Yes or No, saved beside the Automation answers so a reload restores them. */
export function answeredRefine(form: Pick<GuideForm, "refineMovies" | "refineTv"> & { restrictAllowedHosts?: boolean | null }): Record<string, boolean> {
  const answered: Record<string, boolean> = {};
  // The addresses answer rides in the same saved map, so a reload restores it too (#1245).
  if (typeof form.restrictAllowedHosts === "boolean") answered.restrictAllowedHosts = form.restrictAllowedHosts;
  if (typeof form.refineMovies === "boolean") answered.refineMovies = form.refineMovies;
  if (typeof form.refineTv === "boolean") answered.refineTv = form.refineTv;
  return answered;
}

export function restoreDraftRefine(saved: Record<string, boolean> | null | undefined): Pick<GuideForm, "refineMovies" | "refineTv"> {
  return {
    refineMovies: typeof saved?.refineMovies === "boolean" ? saved.refineMovies : null,
    refineTv: typeof saved?.refineTv === "boolean" ? saved.refineTv : null
  };
}

/** What is still missing before Start works, named exactly (never a guess at the wrong thing). */
export function describeMissingForFinish(form: GuideForm, weirConnected = false): string {
  const missing: string[] = [];
  const hasMovies = form.mediaIntent === "movies" || form.mediaIntent === "both";
  const hasTv = form.mediaIntent === "tv" || form.mediaIntent === "both";
  if (!((hasMovies && form.movieRootPath.trim()) || (hasTv && form.seriesRootPath.trim()))) missing.push("a library root folder");
  if (!form.qualityPreset) missing.push("a picture quality");
  if (!form.formatGoal) missing.push("a release preference");
  if (form.skipSignInLocalNetwork === null) missing.push("an answer to \"Skip sign-in for devices on your home network?\"");
  if (form.restrictAllowedHosts === null) missing.push("an answer to \"Only answer on the addresses you use?\"");
  if (form.updateAutomatically === null) missing.push("an answer to \"Update automatically, or ask you first?\"");
  if (!hasAnsweredAllAutomationQuestions(form.automation)) missing.push("an answer to every automation question");
  missing.push(...missingWeirAnswers(form, weirConnected));
  if (missing.length === 0) return "Everything is chosen.";
  return `Still needed before Deluno can create the baseline: ${missing.join(", ")}.`;
}

/**
 * Once Weir is connected, each library type in use needs its Yes or No, and a Yes needs the folder Weir leaves the
 * cleaned files in. Without Weir connected nothing is asked.
 */
export function missingWeirAnswers(form: GuideForm, weirConnected: boolean): string[] {
  if (!weirConnected) return [];
  const missing: string[] = [];
  const types = [
    { label: "Movies", used: form.mediaIntent !== "tv" && form.movieRootPath.trim().length > 0, answer: form.refineMovies, path: form.movieProcessedPath },
    { label: "TV", used: form.mediaIntent !== "movies" && form.seriesRootPath.trim().length > 0, answer: form.refineTv, path: form.seriesProcessedPath }
  ];
  for (const type of types) {
    if (!type.used) continue;
    if (type.answer === null || type.answer === undefined) missing.push(`an answer to "Clean ${type.label} files with Weir before they go into your library?"`);
    else if (type.answer && !type.path.trim()) missing.push(`the folder Weir leaves cleaned ${type.label} files in`);
  }
  return missing;
}

/** Only the questions that have an explicit Yes or No. */
export function answeredAutomation(automation: AutomationChoices): Record<string, boolean> {
  const answered: Record<string, boolean> = {};
  for (const key of Object.keys(automation) as AutomationChoiceKey[]) {
    const value = automation[key];
    if (typeof value === "boolean") answered[key] = value;
  }
  return answered;
}

/** A saved draft's automation answers, limited to the questions Deluno still asks. */
export function restoreDraftAutomation(saved: Record<string, boolean> | null | undefined): AutomationChoices {
  const restored: AutomationChoices = { ...AUTOMATION_UNANSWERED };
  if (!saved) return restored;
  for (const key of Object.keys(restored) as AutomationChoiceKey[]) {
    if (typeof saved[key] === "boolean") restored[key] = saved[key];
  }
  return restored;
}

export type SetupStage = 1 | 2;

export const SETUP_STAGE_LABELS: Record<SetupStage, string> = {
  1: "This computer and you",
  2: "Deluno itself"
};

/**
 * #1034: Stage 1 covers the computer and how it is reached, before anything
 * about media. Stage 2 is the media/library configuration the guide has
 * always asked. The progress rail groups these steps under both headings.
 */
export const STEPS: { id: StepId; label: string; copy: string; stage: SetupStage }[] = [
  { id: "account", label: "Your account", copy: "The account you just created.", stage: 1 },
  { id: "timeRegion", label: "Time and region", copy: "Confirm the timezone, date, time and language Deluno should use.", stage: 1 },
  { id: "signIn", label: "How you sign in", copy: "Always sign in, or skip sign-in for devices on your home network.", stage: 1 },
  { id: "addresses", label: "Addresses", copy: "Choose which addresses Deluno should answer on.", stage: 1 },
  { id: "network", label: "Other devices", copy: "Whether other devices on your network may reach Deluno.", stage: 1 },
  { id: "updates", label: "Updates", copy: "Update automatically, or ask you first.", stage: 1 },
  { id: "mode", label: "Your library", copy: "Choose what you want Deluno to manage.", stage: 2 },
  { id: "folders", label: "Folders", copy: "Tell Deluno where your media lives.", stage: 2 },
  { id: "quality", label: "Library Profiles", copy: "Choose the quality and release experience you want.", stage: 2 },
  { id: "services", label: "Find & Download", copy: "Connect search sources and downloads when you are ready.", stage: 2 },
  { id: "connectApps", label: "Connect installed apps", copy: "Connect the apps the installer put on this computer, when you are ready.", stage: 2 },
  { id: "automation", label: "Automation", copy: "What should Deluno do by itself?", stage: 2 },
  { id: "finish", label: "Start", copy: "Create your plan and open the Dashboard.", stage: 2 }
];

const QUALITY_PRESETS = {
  balanced1080p: {
    label: "Balanced 1080p",
    cutoff: "WEB 1080p",
    allowed: "WEB 720p,Bluray 720p,WEB 1080p,Bluray 1080p",
    copy: "Best default for most homes: good quality, reasonable size, broad compatibility."
  },
  premium4k: {
    label: "Premium 4K",
    cutoff: "WEB 2160p",
    allowed: "WEB 1080p,Bluray 1080p,WEB 2160p,Bluray 2160p,Remux 2160p",
    copy: "For larger displays and strong storage. Deluno still accepts 1080p while searching for better versions."
  }
} as const;

const FORMAT_GOALS = {
  simpleClean: {
    label: "Just make it work",
    bundleId: "starter-streaming-1080p",
    copy: "Reliable 1080p releases, obvious junk blocked, and no fragile HDR/audio rules.",
    bestFor: "Most first-time installs, family libraries, laptops, and users who want fewer surprises."
  },
  balanced: {
    label: "Balanced library",
    bundleId: "balanced-1080p",
    copy: "Prefers good WEB groups, useful audio, proper/repack fixes, and common bad-release blocking.",
    bestFor: "Everyday movie and TV libraries where quality matters but storage should stay sensible."
  },
  homeTheater: {
    label: "Home theatre",
    bundleId: "premium-4k-streaming",
    copy: "Prioritises 4K WEB, HDR, Dolby Vision, stronger release groups, and better living-room playback.",
    bestFor: "4K TVs, Apple TV, Shield, Plex/Jellyfin/Emby users, and premium libraries."
  },
  storageSaver: {
    label: "Storage friendly",
    bundleId: "storage-saver",
    copy: "Discourages huge files, remux-sized releases, generated HDR, and other storage-heavy matches.",
    bestFor: "NAS limits, remote users, slower upload links, and very large libraries."
  },
  anime: {
    label: "Anime",
    bundleId: "anime-balanced",
    copy: "Anime-specific release groups, dual-audio preference, uncensored boost, and raw-release blocking.",
    bestFor: "Anime TV libraries where normal scoring is not enough. Films in the same setup use Balanced."
  },
  archive: {
    label: "Archive quality",
    bundleId: "archive-4k-remux",
    copy: "Prefers remux, UHD Blu-ray groups, lossless audio, HDR, and special editions over WEB releases of any size.",
    bestFor: "Reference movie libraries and home cinema rooms that care more about quality than disk usage. TV shows in the same setup use Balanced."
  },
} as const;

/** The only format goals a saved draft can carry; anything else is a stale or foreign value. */
const FORMAT_GOAL_IDS = new Set(Object.keys(FORMAT_GOALS));

/**
 * A saved draft's formatGoal is free-form on the wire (`string`), because the
 * backend accepts anything it can normalize. Round-trip whatever this guide
 * still recognises — including "anime" (#731) — and only fall back to
 * "not chosen" for a value this guide no longer offers.
 */
export function resolveDraftFormatGoal(draftFormatGoal: string): GuideForm["formatGoal"] {
  return FORMAT_GOAL_IDS.has(draftFormatGoal) ? (draftFormatGoal as GuideForm["formatGoal"]) : "";
}

const INDEXER_SETUP_PRESETS = [
  {
    id: "torznab",
    label: "Torznab",
    protocol: "torznab" as const,
    url: "https://your-tracker.com/api",
    copy: "For torrent trackers and private sites that expose a Torznab API."
  },
  {
    id: "newznab",
    label: "Newznab",
    protocol: "newznab" as const,
    url: "https://indexer.example/api",
    copy: "Use for NZBGeek, DrunkenSlug, NZBCat, and similar Usenet indexers."
  },
  {
    id: "rss",
    label: "RSS feed",
    protocol: "rss" as const,
    url: "https://example.com/feed.xml",
    copy: "Simple feed mode for custom/public sources."
  }
] as const;

const CLIENT_SETUP_PRESETS = [
  { protocol: "qbittorrent" as const, label: "qBittorrent", port: "8080", copy: "Best default torrent client for most users." },
  { protocol: "sabnzbd" as const, label: "SABnzbd", port: "8080", copy: "Best default Usenet downloader." },
  { protocol: "transmission" as const, label: "Transmission", port: "9091", copy: "Lightweight torrent client for Linux/NAS installs." },
  { protocol: "deluge" as const, label: "Deluge", port: "8112", copy: "Flexible torrent client with Web UI support." },
  { protocol: "nzbget" as const, label: "NZBGet", port: "6789", copy: "Efficient Usenet downloader for lower-resource systems." },
  { protocol: "utorrent" as const, label: "uTorrent", port: "8080", copy: "Legacy Web UI support for migrations." }
] as const;

export async function setupGuideLoader(): Promise<SetupGuideLoaderData> {
  const [settings, libraries, qualityProfiles, customFormats, indexers, clients, metadataStatus, progress, draft, guide, allowedHosts, contentBlocklist, localNetworkAuth, updatePreferences] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles"),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients"),
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null),
    fetchJson<SetupProgressItem>("/api/setup/progress"),
    fetchJson<SetupDraftItem>("/api/setup/draft"),
    fetchTrashGuidePackage(),
    // #939: the Addresses step shows these detected addresses rather than
    // asking anyone to type one in.
    fetchJson<AllowedHostsOverview>("/api/security/allowed-hosts"),
    // #944: the setup guide asks about this rather than pre-selecting it
    // (#936), so the current saved value is needed to preserve every other
    // field (mode, patterns) when the answer is written back.
    fetchJson<ContentBlocklistSettings>("/api/download-health/content-blocklist").catch(() => emptyContentBlocklistSettings),
    // #993: the "How you sign in" step shows the current address and whether
    // it would be trusted, the same way the Allowed Hosts card does.
    fetchJson<LocalNetworkAuthOverview>("/api/security/local-network-auth"),
    // #1034: the Updates step reuses System -> Updates' own preferences
    // endpoint rather than a separate setting.
    fetchJson<UpdatePreferencesResponse>("/api/updates/preferences")
  ]);

  return { allowedHosts, clients, contentBlocklist, customFormats, draft, guide, indexers, libraries, localNetworkAuth, metadataStatus, progress, qualityProfiles, settings, updatePreferences };
}

export function SetupGuidePage() {
  const loaderData = useLoaderData() as SetupGuideLoaderData;
  const data = loaderData;
  const { user } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = params.get("return") || "/";
  const [stepIndex, setStepIndex] = useState(() => Math.min(STEPS.length - 1, Math.max(0, data.progress.lastCompletedStep)));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<SetupCompletion | null>(null);
  const [metadataResults, setMetadataResults] = useState<MetadataSearchResult[]>([]);
  const [metadataBusy, setMetadataBusy] = useState(false);
  const [rollbackMessage, setRollbackMessage] = useState<string | null>(null);
  // #1061: the apps the installer put here, and whether the person skipped connecting them. Never blocks the baseline.
  const [installedApps, setInstalledApps] = useState<BundledComponentStatus[]>([]);
  const [appsSkipped, setAppsSkipped] = useState(false);
  // #1132: the clients as they are now. The loader read them when the page opened, before "Connect all" ran, so
  // a client the installer put on and the person just connected would otherwise look missing.
  const [liveClients, setLiveClients] = useState<DownloadClientItem[]>(data.clients);
  const [addingClient, setAddingClient] = useState(false);
  useEffect(() => {
    let active = true;
    void fetchJson<BundledComponentStatus[]>("/api/system/bundled-components")
      .then((next) => { if (active && Array.isArray(next)) setInstalledApps(next.filter((app) => app.installed)); })
      .catch(() => undefined);
    return () => { active = false; };
  }, []);
  useEffect(() => {
    let active = true;
    void fetchJson<DownloadClientItem[]>("/api/download-clients")
      .then((next) => { if (active && Array.isArray(next)) setLiveClients(next); })
      .catch(() => undefined);
    return () => { active = false; };
  }, [stepIndex, installedApps]);
  const [serviceTest, setServiceTest] = useState<ServiceTestState>({
    indexer: "idle",
    client: "idle",
    indexerMessage: null,
    clientMessage: null
  });
  // #1245: a finished guide opened again shows what is set now; a first run decides nothing for the person.
  const rerun = data.progress.isCompleted;
  const [form, setForm] = useState<GuideForm>(() => ({
    mode: data.draft.mode as GuideForm["mode"],
    mediaIntent: data.draft.mediaIntent as GuideForm["mediaIntent"],
    movieRootPath: data.draft.movieRootPath || data.settings.movieRootPath || "",
    seriesRootPath: data.draft.seriesRootPath || data.settings.seriesRootPath || "",
    movieDownloadsPath: data.draft.movieDownloadsPath ?? "",
    seriesDownloadsPath: data.draft.seriesDownloadsPath ?? "",
    qualityPreset: data.draft.qualityPreset as GuideForm["qualityPreset"],
    formatGoal: resolveDraftFormatGoal(data.draft.formatGoal),
    indexerName: data.draft.indexerName || data.indexers[0]?.name,
    indexerProtocol: (data.draft.indexerProtocol || data.indexers[0]?.protocol || "torznab") as GuideForm["indexerProtocol"],
    indexerUrl: data.draft.indexerUrl || data.indexers[0]?.baseUrl || "",
    indexerApiKey: data.indexers[0]?.apiKey ?? "",
    clientName: data.draft.clientName || data.clients[0]?.name,
    clientProtocol: (data.draft.clientProtocol || data.clients[0]?.protocol || "qbittorrent") as GuideForm["clientProtocol"],
    clientHost: data.draft.clientHost || data.clients[0]?.host || "",
    clientPort: data.draft.clientPort || (data.clients[0]?.port ? String(data.clients[0].port) : "8080"),
    clientUsername: data.clients[0]?.username ?? "",
    clientPassword: "",
    backupEnabled: data.draft.backupEnabled,
    firstTitleType: data.draft.firstTitleType as GuideForm["firstTitleType"],
    firstTitle: data.draft.firstTitle,
    firstTitleYear: data.draft.firstTitleYear,
    firstTitleMonitored: data.draft.firstTitleMonitored,
    firstTitleMetadata: null,
    automation: restoreDraftAutomation(data.draft.automation),
    ...restoreDraftRefine(data.draft.automation),
    movieProcessedPath: suggestedProcessedFolder(data.draft.movieRootPath || data.settings.movieRootPath || "", "Movies"),
    seriesProcessedPath: suggestedProcessedFolder(data.draft.seriesRootPath || data.settings.seriesRootPath || "", "TV"),
    // #1245: nothing is decided for the person. A fresh install starts unanswered; a re-run shows the live setting.
    restrictAllowedHosts: rerun ? data.allowedHosts.enabled : typeof data.draft.automation?.restrictAllowedHosts === "boolean" ? data.draft.automation.restrictAllowedHosts : null,
    allowedHostsSelection: data.allowedHosts.allowedHosts.length
      ? data.allowedHosts.allowedHosts
      : [data.allowedHosts.currentHost],
    // #993: unanswered by default (#936) -- Start stays disabled until this
    // has an explicit Yes or No, same as every Automation question.
    skipSignInLocalNetwork: rerun ? data.localNetworkAuth.enabled : typeof data.draft.skipSignInLocalNetwork === "boolean" ? data.draft.skipSignInLocalNetwork : null,
    localNetworkAllowList: data.localNetworkAuth.allowList,
    // #1034 Stage 1 "Time and region": the browser/OS already knows a
    // sensible default (same fields Settings -> Interface edits) -- this is
    // a confirm-or-change question, not an on/off automation switch, so it
    // is pre-filled rather than left blank.
    uiLanguage: data.settings.uiLanguage || "en-AU",
    calendarFirstDayOfWeek: data.settings.calendarFirstDayOfWeek || "monday",
    shortDateFormat: data.settings.shortDateFormat || "dmy",
    longDateFormat: data.settings.longDateFormat || "full",
    timeFormat: data.settings.timeFormat || "12",
    timezone: data.settings.timezone || Intl.DateTimeFormat().resolvedOptions().timeZone,
    // #1034 Stage 1 "Updates": unanswered by default (#936) -- Start stays
    // disabled until this has an explicit Yes or No.
    updateAutomatically: rerun ? data.updatePreferences.mode === "download-apply-on-restart" : typeof data.draft.updateAutomatically === "boolean" ? data.draft.updateAutomatically : null,
    savedAnswers: rerun
      ? {
          restrictAllowedHosts: data.allowedHosts.enabled,
          skipSignInLocalNetwork: data.localNetworkAuth.enabled,
          updateAutomatically: data.updatePreferences.mode === "download-apply-on-restart",
          automation: restoreDraftAutomation(data.draft.automation),
          ...restoreDraftRefine(data.draft.automation)
        }
      : null
  }));

  const current = STEPS[stepIndex];
  const hasMovies = form.mediaIntent === "movies" || form.mediaIntent === "both";
  const hasTv = form.mediaIntent === "tv" || form.mediaIntent === "both";
  const canCreateMovies = hasMovies && form.movieRootPath.trim().length > 0;
  const canCreateTv = hasTv && form.seriesRootPath.trim().length > 0;
  const hasQualityChoice = Boolean(form.qualityPreset);
  const hasReleaseRuleChoice = Boolean(form.formatGoal);
  const hasReadyIndexer = data.indexers.some((item) => item.isEnabled && item.healthStatus === "healthy") || serviceTest.indexer === "passed";
  const connectedClientList = connectedDownloadClients(liveClients);
  const hasReadyClient = connectedClientList.length > 0 || serviceTest.client === "passed";
  const indexerConnectionRequested = Boolean(form.indexerUrl.trim()) && !hasReadyIndexer;
  const clientConnectionRequested = Boolean(form.clientHost.trim()) && !hasReadyClient;
  // What the baseline itself needs: somewhere to put media, and the quality
  // and release decisions that shape every later choice. Connections are not
  // in this list on purpose — requiring a passing indexer *and* client test
  // meant a user without a download client installed could never finish the
  // simple path at all, which is not something Sonarr or Radarr demand. The
  // readiness ladder already carries "configured but not operationally
  // ready", so an untested install can finish and connect later (#246).
  // Nothing automatic is pre-selected (#936): Start stays disabled until every
  // Automation question has an explicit Yes or No answer.
  const hasAllAutomationAnswers = hasAnsweredAllAutomationQuestions(form.automation);
  // #993: same rule -- Start stays disabled until "Skip sign-in for devices
  // on your home network?" has an explicit Yes or No.
  const hasSkipSignInAnswer = form.skipSignInLocalNetwork !== null;
  // #1034: same rule -- Start stays disabled until "Update automatically, or
  // ask you first?" has an explicit Yes or No.
  const hasUpdateAnswer = form.updateAutomatically !== null;
  const hasAddressesAnswer = form.restrictAllowedHosts !== null;
  const weirConnected = installedApps.some((app) => app.kind === "weir" && app.installed && app.connected);
  const hasWeirAnswers = missingWeirAnswers(form, weirConnected).length === 0;
  const canFinish = (canCreateMovies || canCreateTv) && hasQualityChoice && hasReleaseRuleChoice && hasAllAutomationAnswers && hasSkipSignInAnswer && hasAddressesAnswer && hasUpdateAnswer && hasWeirAnswers;
  // A connection typed in but never proven is different from one never
  // attempted: warn about it rather than silently baking in something broken.
  const connectionsUnproven =
    !hasReadyIndexer || !hasReadyClient || indexerConnectionRequested || clientConnectionRequested;

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void authedFetch("/api/setup/draft", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(toSetupDraft(form))
      }).catch(() => undefined);
    }, 500);
    return () => window.clearTimeout(timer);
  }, [form]);

  const completion = useMemo(() => {
    const checks = [
      { label: "Account created", done: true },
      { label: "Media root chosen", done: canCreateMovies || canCreateTv },
      { label: "Library Profile chosen", done: hasQualityChoice },
      { label: "Release preferences chosen", done: hasReleaseRuleChoice },
      { label: "Search source ready", done: hasReadyIndexer },
      { label: "Downloads ready", done: hasReadyClient },
      // #1132: skipping is not connecting. A skipped step reads "Skipped", never "Done".
      ...(installedApps.length > 0
        ? [{
            label: "Installed apps connected",
            done: installedApps.every((app) => app.connected),
            skipped: appsSkipped && !installedApps.every((app) => app.connected)
          }]
        : [])
    ] as { label: string; done: boolean; skipped?: boolean }[];
    return checks;
  }, [
    canCreateMovies,
    canCreateTv,
    hasReadyClient,
    hasReadyIndexer,
    installedApps,
    appsSkipped,
    form.formatGoal,
    form.qualityPreset,
    serviceTest.client,
    serviceTest.indexer,
  ]);

  function patch(patchValue: Partial<GuideForm>) {
    if (["indexerName", "indexerProtocol", "indexerUrl", "indexerApiKey"].some((key) => key in patchValue)) {
      setServiceTest((current) => ({ ...current, indexer: "idle", indexerMessage: null }));
    }
    if (["clientName", "clientProtocol", "clientHost", "clientPort", "clientUsername", "clientPassword"].some((key) => key in patchValue)) {
      setServiceTest((current) => ({ ...current, client: "idle", clientMessage: null }));
    }
    setForm((currentForm) => ({ ...currentForm, ...patchValue }));
  }

  function saveProgress(lastCompletedStep: number, isSkipped = false, isCompleted = false) {
    return authedFetch("/api/setup/progress", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ lastCompletedStep, isSkipped, isCompleted })
    });
  }

  function moveToStep(nextStep: number) {
    const boundedStep = Math.min(STEPS.length - 1, Math.max(0, nextStep));
    setStepIndex(boundedStep);
    // Moving between steps says nothing about whether the guide was skipped or
    // finished. Saving both as false here un-dismissed the dashboard's prompt
    // for anyone who opened the guide again to look something up.
    void saveProgress(boundedStep, data.progress.isSkipped, data.progress.isCompleted).catch(() => undefined);
  }

  function skipWizard() {
    // The expert path is an explicit choice, not an incomplete setup. The
    // persisted setup status keeps the dashboard prompt in sync per install.
    void saveProgress(stepIndex, true).catch(() => undefined);
    navigate(returnTo, { replace: true });
  }

  async function handleFinish(event?: FormEvent) {
    event?.preventDefault();
    setError(null);
    setResult(null);
    setRollbackMessage(null);

    if (!canFinish) {
      if (!canCreateMovies && !canCreateTv) {
        setError("Choose at least one Movies or TV root folder before finishing simple setup.");
        setStepIndex(1);
      } else if (!hasQualityChoice || !hasReleaseRuleChoice) {
        setError("Choose a picture quality and release preference before finishing simple setup.");
        setStepIndex(2);
      } else if (!hasAllAutomationAnswers) {
        setError("Answer every automation question before finishing setup.");
        setStepIndex(STEPS.findIndex((step) => step.id === "automation"));
      } else if (!hasSkipSignInAnswer) {
        setError("Answer \"Skip sign-in for devices on your home network?\" before finishing setup.");
        setStepIndex(STEPS.findIndex((step) => step.id === "signIn"));
      } else {
        setError("Answer \"Update automatically, or ask you first?\" before finishing setup.");
        setStepIndex(STEPS.findIndex((step) => step.id === "updates"));
      }
      return;
    }

    // #1132: everything that can be refused is checked before anything is written, so a refusal
    // leaves nothing half-applied.
    const preflight = preflightDownloadClient(liveClients, form, serviceTest.client === "passed");
    if (preflight) {
      setError(preflight);
      return;
    }

    setBusy(true);
    const createdEntities: CreatedEntity[] = [];
    try {
      const completionResult = await createBaseline({
        data,
        form,
        liveClients,
        indexerTested: serviceTest.indexer === "passed",
        clientTested: serviceTest.client === "passed",
        hasMovies,
        hasTv,
        canCreateMovies,
        canCreateTv
      }, createdEntities);
      setResult(completionResult);
    } catch (err) {
      // All or none (#1132): what was made is removed, no answer was applied, and the screen keeps every answer given.
      const rollback = createdEntities.length > 0
        ? await rollbackCreatedEntities(createdEntities)
        : "Setup failed before Deluno created anything that needed cleanup.";
      setRollbackMessage(`${rollback} None of your answers were applied. Your choices are still here, so fix the problem above and press Create baseline again.`);
      setError(err instanceof Error ? err.message : "Setup could not be completed.");
    } finally {
      setBusy(false);
    }
  }

  async function handleMetadataSearch() {
    const title = form.firstTitle.trim();
    if (!title) {
      setError("Type a movie or TV title before searching metadata.");
      return;
    }
    if (data.metadataStatus && !data.metadataStatus.isConfigured) {
      setError("Title matching is temporarily unavailable. You can still add this title manually and try metadata search again later.");
      return;
    }

    setError(null);
    setMetadataBusy(true);
    setMetadataResults([]);
    try {
      const params = new URLSearchParams({
        mediaType: form.firstTitleType,
        query: title
      });
      if (form.firstTitleYear.trim()) params.set("year", form.firstTitleYear.trim());
      const results = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      setMetadataResults(results);
      if (results.length === 0) setError("No metadata matches were found. You can still add the title manually.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Metadata lookup failed.");
    } finally {
      setMetadataBusy(false);
    }
  }

  function selectMetadataResult(item: MetadataSearchResult) {
    patch({
      firstTitle: item.title,
      firstTitleYear: item.year ? String(item.year) : form.firstTitleYear,
      firstTitleMetadata: item
    });
    void enrichSelectedMetadata(item);
  }

  async function enrichSelectedMetadata(item: MetadataSearchResult) {
    try {
      const params = new URLSearchParams({
        mediaType: form.firstTitleType,
        query: item.title,
        providerId: item.providerId
      });
      if (item.year) params.set("year", String(item.year));
      const details = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      const detailedItem = details.find((candidate) => candidate.providerId === item.providerId);
      if (!detailedItem) return;

      setForm((currentForm) =>
        currentForm.firstTitleMetadata?.provider === item.provider && currentForm.firstTitleMetadata.providerId === item.providerId
          ? {
              ...currentForm,
              firstTitle: detailedItem.title,
              firstTitleYear: detailedItem.year ? String(detailedItem.year) : currentForm.firstTitleYear,
              firstTitleMetadata: detailedItem
            }
          : currentForm);
    } catch {
      // The chosen card remains valid; enrichment will be available from the title page later.
    }
  }

  return (
    <div className="space-y-(--page-gap)">
      <section className="relative overflow-hidden rounded-3xl border border-hairline bg-card p-(--tile-pad) shadow-xs">
        <div aria-hidden className="absolute inset-x-0 top-0 h-px bg-linear-to-r/srgb from-transparent via-primary/70 to-transparent" />
        {/*
          The readiness table used to sit here as a second column. A six-row
          table is far taller than the intro beside it, so the hero stretched
          to match and left a large dead zone under the summary tiles (#254).
          Readiness now sits with the step rail, where progress belongs.
        */}
        <div>
          <div>
            {/* The topbar already names this page; a second h1 made two. */}
            <p className="mt-2 max-w-5xl font-display text-[clamp(2rem,2.4vw,3.15rem)] font-semibold leading-[0.98] tracking-tight text-foreground">
              Get Deluno working first. Tune it later.
            </p>
            <p className="mt-3 max-w-4xl text-(length:--type-body) leading-relaxed text-muted-foreground">
              This sets up the essentials: where your media lives, the quality you want, and how releases are chosen.
              Connecting a search source and a download client is part of the guide but not required to finish — the
              dashboard keeps that step waiting until you do. Advanced users can skip straight to the full settings.
            </p>
            <SummaryStrip
              className="mt-4"
              cells={[
                { label: "Folders", value: "Movies, TV, downloads" },
                { label: "Quality", value: "Simple profile choice" },
                { label: "Release preferences", value: "Plain-English rules" },
                { label: "Routing", value: "Optional source + client" }
              ]}
            />
          </div>
        </div>
      </section>

      <div className="grid grid-cols-[minmax(0,1fr)] gap-(--grid-gap) xl:grid-cols-[260px_minmax(0,1fr)]">
        <aside className="min-w-0 space-y-2">
          {/*
            #1034: the rail shows both stages, not a flat list -- Stage 1
            ("This computer and you") finishes before anything about media,
            Stage 2 ("Deluno itself") is the library configuration that
            follows it.
          */}
          {([1, 2] as const).map((stage) => (
            <div key={stage} className="space-y-2">
              <p className="px-1 pt-1 text-(length:--type-caption) font-bold uppercase tracking-[0.16em] text-muted-foreground">
                Stage {stage} &middot; {SETUP_STAGE_LABELS[stage]}
              </p>
              {STEPS.map((step, index) => {
                if (step.stage !== stage) return null;
                const active = index === stepIndex;
                const done = index < stepIndex;
                return (
                  <button
                    key={step.id}
                    type="button"
                    onClick={() => moveToStep(index)}
                    className={cn(
                      "w-full rounded-2xl border p-4 text-left transition",
                      active ? "border-primary/40 bg-primary/10" : "border-hairline bg-card hover:border-primary/25",
                      done && "opacity-85"
                    )}
                  >
                    <span className="flex items-center gap-3">
                      <span className="flex h-8 w-8 items-center justify-center rounded-xl border border-hairline bg-background text-xs font-bold tabular-nums text-foreground">
                        {done ? <CheckCircle2 className="h-4 w-4 text-success" /> : index + 1}
                      </span>
                      <span className="min-w-0">
                        <span className="block font-semibold text-foreground">{step.label}</span>
                        <span className="mt-0.5 block text-xs leading-relaxed text-muted-foreground">{step.copy}</span>
                      </span>
                    </span>
                  </button>
                );
              })}
            </div>
          ))}

          {/*
            A plain list, not a ListTable: the rail is 260px and a two-column
            table truncated every label to "Acco…" / "Medi…". The rail's own
            language is these bordered cards, so readiness matches them.
          */}
          <div className="rounded-2xl border border-hairline bg-card p-4">
            <div className="flex items-baseline justify-between gap-2">
              <span className="text-(length:--type-body-sm) font-semibold text-foreground">Readiness</span>
              <span className="text-(length:--type-caption) tabular-nums text-muted-foreground">
                {completion.filter((item) => item.done).length} of {completion.length} done
              </span>
            </div>
            <ul className="mt-3 grid gap-2">
              {completion.map((item) => (
                <li key={item.label} className="flex items-center justify-between gap-2">
                  <span className="min-w-0 text-(length:--type-caption) leading-snug text-muted-foreground">{item.label}</span>
                  <Chip tone={item.done ? "ok" : "idle"}>{item.done ? "Done" : item.skipped ? "Skipped" : "Waiting"}</Chip>
                </li>
              ))}
            </ul>
          </div>
        </aside>

        <form onSubmit={(event) => void handleFinish(event)} className="min-w-0 rounded-3xl border border-hairline bg-card shadow-xs">
          <div className="border-b border-hairline p-(--tile-pad)">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <h2 className="font-display text-2xl font-semibold tracking-tight text-foreground">{current.label}</h2>
              </div>
              <Badge variant={form.mode === "simple" ? "success" : "default"}>{form.mode === "simple" ? "Simple path" : "Advanced skip"}</Badge>
            </div>
          </div>

          <div className="min-h-[180px] p-(--tile-pad)">
            {current.id === "account" ? <AccountStep user={user} /> : null}
            {current.id === "timeRegion" ? <TimeRegionStep form={form} patch={patch} /> : null}
            {current.id === "signIn" ? (
              <LocalNetworkAuthQuestion form={form} patch={patch} currentAddress={data.localNetworkAuth.currentAddress} />
            ) : null}
            {current.id === "addresses" ? (
              <AddressesStep
                form={form}
                patch={patch}
                candidates={data.allowedHosts.detectedHosts}
                currentHost={data.allowedHosts.currentHost}
              />
            ) : null}
            {current.id === "network" ? <NetworkStep address={addressForOtherDevices(data.allowedHosts.currentHost, data.allowedHosts.detectedHosts) ?? undefined} /> : null}
            {current.id === "updates" ? <UpdatesStep form={form} patch={patch} channel={data.updatePreferences.channel} /> : null}
            {current.id === "mode" ? <ModeStep form={form} patch={patch} onSkip={skipWizard} /> : null}
            {current.id === "folders" ? <FoldersStep form={form} patch={patch} /> : null}
            {current.id === "quality" ? <QualityStep form={form} guide={data.guide} patch={patch} /> : null}
            {current.id === "services" ? (
              <ServicesStep
                form={form}
                patch={patch}
                testState={serviceTest}
                connectedClients={connectedClientList}
                addingClient={addingClient}
                onAddClient={() => { setAddingClient(true); patch({ clientHost: "", clientUsername: "", clientPassword: "" }); }}
                onTestIndexer={() => void testIndexerConnection()}
                onTestClient={() => void testClientConnection()}
              />
            ) : null}
            {current.id === "connectApps" ? (
              <ConnectAppsStep skipped={appsSkipped} onSkip={() => setAppsSkipped(true)} onChange={setInstalledApps} hasInstalled={installedApps.length > 0} form={form} patch={patch} weirConnected={weirConnected} />
            ) : null}
            {current.id === "automation" ? <AutomationStep form={form} patch={patch} /> : null}
            {current.id === "finish" ? (
          <FinishStep
            weirConnected={weirConnected}
            form={form}
            patch={patch}
            canFinish={canFinish}
            connectionTestsRequired={connectionsUnproven}
            result={result}
                error={error}
                rollbackMessage={rollbackMessage}
                metadataStatus={data.metadataStatus}
                metadataResults={metadataResults}
                metadataBusy={metadataBusy}
                existingMetadataConfigured={Boolean(data.metadataStatus?.isConfigured)}
                onMetadataSearch={() => void handleMetadataSearch()}
                onSelectMetadata={selectMetadataResult}
                route={describeDownloadRoute(liveClients, form, serviceTest.client === "passed")}
              />
            ) : null}
          </div>

          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-hairline p-(--tile-pad)">
            <Button type="button" variant="outline" onClick={() => (stepIndex === 0 ? skipWizard() : moveToStep(stepIndex - 1))} disabled={busy}>
              {stepIndex === 0 ? <Settings2 className="h-4 w-4" /> : <ArrowLeft className="h-4 w-4" />}
              {stepIndex === 0 ? "Skip to advanced" : "Back"}
            </Button>
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="ghost" asChild>
                <Link to="/settings">Open Media Management</Link>
              </Button>
              {result ? (
                <Button type="button" asChild>
                  <Link to={result.firstTitlePath ?? returnTo}>
                    Continue to Deluno
                    <ArrowRight className="h-4 w-4" />
                  </Link>
                </Button>
              ) : stepIndex < STEPS.length - 1 ? (
                <Button key="continue" type="button" onClick={() => moveToStep(stepIndex + 1)} disabled={busy || !stepIsAnswered(current.id, form, weirConnected)}>
                  Continue
                  <ArrowRight className="h-4 w-4" />
                </Button>
              ) : (
                <Button
                  key="create-baseline"
                  type="submit"
                  disabled={busy || !canFinish}
                  // A disabled primary button must say why it is disabled,
                  // next to the button — the amber banner alone read as
                  // advice rather than the reason (#246).
                  title={canFinish
                    ? "Create the libraries, profiles and rules you chose"
                    : !canCreateMovies && !canCreateTv
                      ? "Choose a Movies or TV root folder first"
                      : !hasQualityChoice || !hasReleaseRuleChoice
                        ? "Choose a picture quality and release preference first"
                        : "Answer every automation question first"}
                >
                  {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Rocket className="h-4 w-4" />}
                  Create baseline
                </Button>
              )}
            </div>
          </div>
        </form>
      </div>
    </div>
  );

  async function testIndexerConnection() {
    if (!form.indexerUrl.trim()) {
      setServiceTest((current) => ({ ...current, indexer: "failed", indexerMessage: "Enter an indexer URL before testing." }));
      return;
    }

    setServiceTest((current) => ({ ...current, indexer: "testing", indexerMessage: null }));
    try {
      const response = await authedFetch("/api/indexers/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildIndexerPayload(form))
      });
      if (!response.ok) throw new Error(await readErrorText(response, "Indexer test failed."));
      const payload = await response.json().catch(() => null) as ConnectionTestResponse | null;
      if (payload?.healthStatus !== "healthy") throw new Error(payload?.message ?? "Indexer test needs attention.");
      setServiceTest((current) => ({ ...current, indexer: "passed", indexerMessage: payload?.message ?? "Indexer test passed." }));
    } catch (err) {
      setServiceTest((current) => ({
        ...current,
        indexer: "failed",
        indexerMessage: err instanceof Error ? err.message : "Indexer test failed."
      }));
    }
  }

  async function testClientConnection() {
    if (!form.clientHost.trim()) {
      setServiceTest((current) => ({ ...current, client: "failed", clientMessage: "Enter a download client host before testing." }));
      return;
    }

    setServiceTest((current) => ({ ...current, client: "testing", clientMessage: null }));
    try {
      const response = await authedFetch("/api/download-clients/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildClientPayload(form))
      });
      if (!response.ok) throw new Error(await readErrorText(response, "Download client test failed."));
      const payload = await response.json().catch(() => null) as ConnectionTestResponse | null;
      if (payload?.healthStatus !== "healthy") throw new Error(payload?.message ?? "Download client test needs attention.");
      setServiceTest((current) => ({ ...current, client: "passed", clientMessage: payload?.message ?? "Download client test passed." }));
    } catch (err) {
      setServiceTest((current) => ({
        ...current,
        client: "failed",
        clientMessage: err instanceof Error ? err.message : "Download client test failed."
      }));
    }
  }
}

function ModeStep({ form, patch, onSkip }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void; onSkip: () => void }) {
  return (
    <div className="grid gap-(--grid-gap) lg:grid-cols-2">
      <ChoiceCard
        active={form.mode === "simple"}
        icon={Rocket}
        title="Simple configuration"
        copy="Recommended. Deluno creates a clean baseline you can trust, then you can refine advanced policies later."
        onClick={() => patch({ mode: "simple" })}
      />
      <ChoiceCard
        active={form.mode === "advanced"}
        icon={Settings2}
        title="I know what I am doing"
        copy="Skip the wizard and go straight to the full settings, routing, quality, indexer, and policy controls."
        onClick={() => {
          patch({ mode: "advanced" });
          onSkip();
        }}
      />
      <div className="lg:col-span-2 rounded-2xl border border-hairline bg-surface-1 p-4">
        <p className="text-sm font-semibold text-foreground">What Deluno will do</p>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
          It will not hide advanced controls. It simply sets safe defaults first: folders, naming, quality target,
          retry-friendly automation, optional provider routing, and backups.
        </p>
      </div>
    </div>
  );
}

function FoldersStep({ form, patch }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void }) {
  return (
    <div className="space-y-(--page-gap)">
      <div className="grid gap-3 sm:grid-cols-3">
        {(["both", "movies", "tv"] as const).map((value) => (
          <button
            key={value}
            type="button"
            onClick={() => patch({ mediaIntent: value })}
            className={cn(
              "rounded-2xl border p-4 text-left transition",
              form.mediaIntent === value ? "border-primary/40 bg-primary/10" : "border-hairline bg-surface-1"
            )}
          >
            <p className="font-semibold capitalize text-foreground">{value === "tv" ? "TV Shows" : value}</p>
            <p className="mt-1 text-xs text-muted-foreground">Create {value === "both" ? "Movies and TV" : value === "tv" ? "TV" : "Movies"} library defaults.</p>
          </button>
        ))}
      </div>

      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <p className="text-sm font-semibold text-foreground">Beginner default</p>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
          Movies use <span className="font-semibold text-foreground">{`{Movie Title} ({Release Year})`}</span> and TV shows use{" "}
          <span className="font-semibold text-foreground">{`{Series Title} ({Series Year})`}</span>. Advanced users can change naming later.
        </p>
      </div>

      <div className="grid gap-(--grid-gap) lg:grid-cols-2">
        {(form.mediaIntent === "movies" || form.mediaIntent === "both") ? (
          <FieldShell icon={FolderTree} label="Movies root" copy="Where movie folders should live. UNC, Docker mount, mapped drives, and local paths are supported.">
            <PathInput value={form.movieRootPath} onChange={(value) => patch({ movieRootPath: value })} placeholder="D:\Media\Movies or /media/movies" browseTitle="Choose movies root" />
          </FieldShell>
        ) : null}
        {(form.mediaIntent === "movies" || form.mediaIntent === "both") ? (
          <FieldShell icon={FolderTree} label="Movies downloads folder (optional)" copy="Where your download client finishes Movies. Leave it blank and Deluno reads it from the download clients you connected.">
            <PathInput value={form.movieDownloadsPath} onChange={(value) => patch({ movieDownloadsPath: value })} placeholder="D:\Downloads\Completed\films" browseTitle="Choose Movies downloads folder" />
          </FieldShell>
        ) : null}
        {(form.mediaIntent === "tv" || form.mediaIntent === "both") ? (
          <FieldShell icon={FolderTree} label="TV root" copy="Where series folders should live.">
            <PathInput value={form.seriesRootPath} onChange={(value) => patch({ seriesRootPath: value })} placeholder="D:\Media\TV or /media/tv" browseTitle="Choose TV root" />
          </FieldShell>
        ) : null}
        {(form.mediaIntent === "tv" || form.mediaIntent === "both") ? (
          <FieldShell icon={FolderTree} label="TV downloads folder (optional)" copy="Where your download client finishes TV. Blank is read from the same clients.">
            <PathInput value={form.seriesDownloadsPath} onChange={(value) => patch({ seriesDownloadsPath: value })} placeholder="D:\Downloads\Completed\shows" browseTitle="Choose TV downloads folder" />
          </FieldShell>
        ) : null}
      </div>
    </div>
  );
}

function QualityStep({ form, guide, patch }: { form: GuideForm; guide: GuidePackage; patch: (patchValue: Partial<GuideForm>) => void }) {
  const selectedGoal = form.formatGoal ? FORMAT_GOALS[form.formatGoal] : null;
  const selectedBundle = form.formatGoal ? getFormatBundle(guide, form.formatGoal) : null;

  return (
    <div className="space-y-(--page-gap)">
      <section>
        <div className="mb-3">
          <p className="text-sm font-semibold text-foreground">Picture quality</p>
          <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
            Choose a preset and Deluno handles the details. You can fine-tune everything later.
          </p>
        </div>
        <div className="grid gap-(--grid-gap) lg:grid-cols-2">
          {Object.entries(QUALITY_PRESETS).map(([id, preset]) => (
            <ChoiceCard
              key={id}
              active={form.qualityPreset === id}
              icon={ShieldCheck}
              title={preset.label}
              copy={`${preset.copy} Target: ${preset.cutoff}.`}
              onClick={() => patch({ qualityPreset: id as Exclude<GuideForm["qualityPreset"], ""> })}
            />
          ))}
        </div>
      </section>

      <section>
        <div className="mb-3">
          <p className="text-sm font-semibold text-foreground">Release preference</p>
          <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
            This is custom formats in plain English. Deluno adds the matching rules automatically; advanced users can edit them later.
          </p>
        </div>
        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {Object.entries(FORMAT_GOALS).map(([id, goal]) => (
            <button
              key={id}
              type="button"
              onClick={() => patch({ formatGoal: id as Exclude<GuideForm["formatGoal"], ""> })}
              className={cn(
                "rounded-2xl border p-4 text-left transition hover:-translate-y-0.5",
                form.formatGoal === id ? "border-primary/40 bg-primary/10" : "border-hairline bg-surface-1 hover:border-primary/25"
              )}
            >
              <Sparkles className={cn("h-4 w-4", form.formatGoal === id ? "text-primary" : "text-muted-foreground")} />
              <p className="mt-3 font-semibold text-foreground">{goal.label}</p>
              <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{goal.copy}</p>
              <p className="mt-3 text-xs leading-relaxed text-muted-foreground">{goal.bestFor}</p>
            </button>
          ))}
        </div>
      </section>

      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-sm font-semibold text-foreground">What will be created</p>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              {selectedGoal ? `${selectedGoal.label}: ${selectedBundle?.description ?? selectedGoal.copy}` : "Choose a release preference to preview the rules Deluno will create."}
            </p>
          </div>
          <Badge variant="default">{selectedBundle?.includes.length ?? 0} rules</Badge>
        </div>
        <div className="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
          {(form.formatGoal ? previewFormats(guide, form.formatGoal) : []).map((format) => (
            <div key={format.trashId} className="rounded-xl border border-hairline bg-background/35 px-3 py-2">
              <p className="text-sm font-semibold text-foreground">{format.name}</p>
              <p className="mt-1 text-xs leading-relaxed text-muted-foreground [overflow-wrap:anywhere]">{format.description}</p>
            </div>
          ))}
        </div>
        <p className="mt-4 text-sm leading-relaxed text-muted-foreground">
          Automation defaults: missing searches, upgrade searches, 12 hour scheduled checks, 6 hour retry delay, and a small per-run cap so indexers are not hammered.
        </p>
      </div>
    </div>
  );
}

function ServicesStep({
  form,
  patch,
  testState,
  connectedClients,
  addingClient,
  onAddClient,
  onTestIndexer,
  onTestClient
}: {
  form: GuideForm;
  patch: (patchValue: Partial<GuideForm>) => void;
  testState: ServiceTestState;
  connectedClients: DownloadClientItem[];
  addingClient: boolean;
  onAddClient: () => void;
  onTestIndexer: () => void;
  onTestClient: () => void;
}) {
  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-(--grid-gap) xl:grid-cols-2">
      <FieldShell icon={RadioTower} label="Search source" copy="Required for operational readiness. Connect and test the source Deluno will use to find releases.">
        <div className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-3">
          <PresetRadioGroup
            label="Search source presets"
            options={INDEXER_SETUP_PRESETS.map((preset) => ({
              id: preset.id,
              label: preset.label,
              copy: preset.copy,
              active: form.indexerProtocol === preset.protocol && form.indexerName === preset.label,
              onSelect: () => patch({
                indexerName: preset.label,
                indexerProtocol: preset.protocol,
                indexerUrl: preset.url
              })
            }))}
          />
          <Field label="Indexer protocol" hideLabel>
            <Select value={form.indexerProtocol} onChange={(event) => patch({ indexerProtocol: event.target.value as GuideForm["indexerProtocol"] })}>
              <option value="torznab">Torznab</option>
              <option value="newznab">Newznab (Usenet indexer)</option>
              <option value="rss">RSS feed</option>
            </Select>
          </Field>
          <Field label="Indexer URL" hideLabel>
            <Input value={form.indexerUrl} onChange={(event) => patch({ indexerUrl: event.target.value })} placeholder="https://indexer.example/api" />
          </Field>
          <Field label="Indexer API key" hideLabel>
            <Input value={form.indexerApiKey} onChange={(event) => patch({ indexerApiKey: event.target.value })} placeholder="API key, if required" />
          </Field>
          <ServiceTestButton
            status={testState.indexer}
            message={testState.indexerMessage}
            onClick={onTestIndexer}
            label="Test search source"
          />
        </div>
      </FieldShell>

      <FieldShell icon={DownloadCloud} label="Download client" copy="The external client owns downloading; Deluno orchestrates, observes, and imports.">
        <div className="grid grid-cols-[minmax(0,1fr)] gap-3">
          {connectedClients.length > 0 ? (
            // #1132: clients that are already connected are used as they are. Nothing is asked again.
            <div className="grid grid-cols-[minmax(0,1fr)] gap-2 rounded-xl border border-hairline bg-surface-2 p-3">
              <p className="text-sm font-semibold text-foreground">Already connected. Deluno will use these.</p>
              <ul className="grid grid-cols-[minmax(0,1fr)] gap-1.5">
                {connectedClients.map((client) => (
                  <li key={client.id} className="flex flex-wrap items-center justify-between gap-2 text-sm text-foreground">
                    <span title={`${client.name}${client.host ? ` (${client.host}${client.port ? `:${client.port}` : ""})` : ""}`} className="min-w-0 truncate max-md:whitespace-normal max-md:break-words">{client.name}{client.host ? ` (${client.host}${client.port ? `:${client.port}` : ""})` : ""}</span>
                    <Chip tone="ok">Connected</Chip>
                  </li>
                ))}
              </ul>
              {!addingClient ? (
                <div>
                  <Button type="button" variant="outline" size="sm" onClick={onAddClient}>
                    Add a different download client
                  </Button>
                </div>
              ) : null}
            </div>
          ) : (
            // #506: the first-run offer to install one, rather than find, install and wire it by hand.
            <p className="text-(length:--type-caption) text-muted-foreground">
              Not installed yet? <Link className="font-medium text-primary hover:underline" to="/indexers/companion-tools">Let Deluno install and connect SABnzbd, qBittorrent or Weir</Link>, then come back.
            </p>
          )}
          {connectedClients.length === 0 || addingClient ? (
            <div className="grid grid-cols-[minmax(0,1fr)] gap-3">
          <PresetRadioGroup
            label="Download client presets"
            options={CLIENT_SETUP_PRESETS.map((preset) => ({
              id: preset.protocol,
              label: preset.label,
              copy: preset.copy,
              active: form.clientProtocol === preset.protocol,
              onSelect: () => patch({
                clientName: preset.label,
                clientProtocol: preset.protocol,
                clientPort: preset.port
              })
            }))}
          />
          <Field label="Download client protocol" hideLabel>
            <Select value={form.clientProtocol} onChange={(event) => patch({ clientProtocol: event.target.value as GuideForm["clientProtocol"], clientPort: defaultClientPort(event.target.value) })}>
              <option value="qbittorrent">qBittorrent</option>
              <option value="sabnzbd">SABnzbd</option>
              <option value="transmission">Transmission</option>
              <option value="deluge">Deluge</option>
              <option value="nzbget">NZBGet</option>
              <option value="utorrent">uTorrent</option>
            </Select>
          </Field>
          <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_120px]">
            <Field label="Download client host" hideLabel>
              <Input value={form.clientHost} onChange={(event) => patch({ clientHost: event.target.value })} placeholder="localhost or docker host" />
            </Field>
            <Field label="Download client port" hideLabel>
              <Input value={form.clientPort} onChange={(event) => patch({ clientPort: event.target.value })} placeholder="8080" />
            </Field>
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Download client username" hideLabel>
              <Input value={form.clientUsername} onChange={(event) => patch({ clientUsername: event.target.value })} placeholder="Username, if required" />
            </Field>
            <Field label="Download client password or API key" hideLabel>
              <Input value={form.clientPassword} onChange={(event) => patch({ clientPassword: event.target.value })} placeholder="Password/API key" type="password" />
            </Field>
          </div>
          <ServiceTestButton
            status={testState.client}
            message={testState.clientMessage}
            onClick={onTestClient}
            label="Test download client"
          />
            </div>
          ) : null}
        </div>
      </FieldShell>
    </div>
  );
}

/**
 * Asked once Weir is connected, per library type: should its downloads be cleaned by Weir before they go into the
 * library? Nothing is pre-selected (#936). A Yes sets the library to refine before import and links it to the
 * connected Weir when the baseline is created; a No leaves the library importing as it is.
 */
function RefineWithWeirQuestions({ form, patch }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void }) {
  const types = [
    { label: "Movies" as const, used: form.mediaIntent !== "tv", answer: form.refineMovies, path: form.movieProcessedPath, answerKey: "refineMovies" as const, pathKey: "movieProcessedPath" as const },
    { label: "TV" as const, used: form.mediaIntent !== "movies", answer: form.refineTv, path: form.seriesProcessedPath, answerKey: "refineTv" as const, pathKey: "seriesProcessedPath" as const }
  ].filter((type) => type.used);
  const question = (label: string) => `${label}: Clean files with Weir before they go into your library?`;
  return (
    <FieldShell
      icon={ShieldCheck}
      label="Clean files with Weir before they go into your library?"
      copy="Weir is ready to clean your downloads. Answer Yes or No for each kind of library. Yes sends its downloads to Weir first and imports the cleaned file; No imports the finished download as it is."
    >
      <div className="divide-y divide-hairline">
        {types.map((type) => (
          <div key={type.label} className="flex flex-col gap-3 py-4 first:pt-0 last:pb-0">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <p className="font-medium text-foreground">{type.label}</p>
                <CurrentSettingNote value={form.savedAnswers?.[type.answerKey]} />
              </div>
              <div className="flex shrink-0 gap-2" role="radiogroup" aria-label={question(type.label)}>
                <Button type="button" size="sm" variant={type.answer === true ? "default" : "outline"} role="radio" aria-checked={type.answer === true} aria-label={`${question(type.label)} Yes`} onClick={() => patch({ [type.answerKey]: true })}>Yes</Button>
                <Button type="button" size="sm" variant={type.answer === false ? "default" : "outline"} role="radio" aria-checked={type.answer === false} aria-label={`${question(type.label)} No`} onClick={() => patch({ [type.answerKey]: false })}>No</Button>
              </div>
            </div>
            {type.answer === true ? (
              <Field label={`Where Weir leaves cleaned ${type.label} files`} help="The folder must already exist. Weir sets up its Movies and TV workflows from this by itself.">
                <PathInput value={type.path} onChange={(value) => patch({ [type.pathKey]: value })} placeholder="D:\Weir\Ready" browseTitle={`Choose the folder Weir leaves cleaned ${type.label} files in`} />
              </Field>
            ) : null}
          </div>
        ))}
      </div>
    </FieldShell>
  );
}

/**
 * #1061: Deluno connects nothing by itself after install. This step lists the
 * apps the installer put on this computer, one Connect button each plus
 * "Connect all". It is done when every installed app is connected or the person
 * skips it, and it never blocks the baseline. Shows a plain line when nothing
 * was installed.
 */
export function ConnectAppsStep({
  skipped,
  onSkip,
  onChange,
  hasInstalled,
  form,
  patch,
  weirConnected = false
}: {
  skipped: boolean;
  onSkip: () => void;
  onChange: (apps: BundledComponentStatus[]) => void;
  hasInstalled: boolean;
  form?: GuideForm;
  patch?: (patchValue: Partial<GuideForm>) => void;
  /** Weir is installed and connected: the per-library question below is asked. */
  weirConnected?: boolean;
}) {
  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-3">
      <ConnectInstalledApps onChange={onChange} />
      {weirConnected && form && patch ? <RefineWithWeirQuestions form={form} patch={patch} /> : null}
      {hasInstalled ? (
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onSkip} disabled={skipped}>
            Skip connecting for now
          </Button>
          <span className="text-(length:--type-caption) text-muted-foreground">
            {skipped ? "Skipped. You can connect them later from Connections." : "Skipping does not stop you finishing setup."}
          </span>
        </div>
      ) : (
        <p className="text-(length:--type-caption) text-muted-foreground">
          The installer did not put any download client or processor on this computer, so there is nothing to connect here.
        </p>
      )}
    </div>
  );
}

function AutomationStep({
  form,
  patch
}: {
  form: GuideForm;
  patch: (patchValue: Partial<GuideForm>) => void;
}) {
  function setAnswer(key: AutomationChoiceKey, value: boolean) {
    patch({ automation: { ...form.automation, [key]: value } });
  }

  return (
    <FieldShell
      icon={ShieldCheck}
      label="What should Deluno do by itself?"
      copy="Nothing is chosen yet. Answer Yes or No for each — you can change any of these later in Settings."
    >
      <div className="divide-y divide-hairline">
        {AUTOMATION_QUESTIONS.map((item) => {
          const value = form.automation[item.key];
          return (
            <div key={item.key} className="flex flex-col gap-3 py-4 first:pt-0 last:pb-0 sm:flex-row sm:items-center sm:justify-between">
              <div className="max-w-xl">
                <p className="font-medium text-foreground">{item.question}</p>
                <p className="mt-0.5 text-(length:--type-caption) text-muted-foreground">{item.help}</p>
                <CurrentSettingNote value={form.savedAnswers?.automation[item.key]} />
              </div>
              {/* Unique accessible names per row: plain "Yes"/"No" would repeat
                  across eight rows and break assistive tech and the smoke tests. */}
              <div className="flex shrink-0 gap-2" role="radiogroup" aria-label={item.question}>
                <Button
                  type="button"
                  variant={value === true ? "default" : "outline"}
                  size="sm"
                  role="radio"
                  aria-checked={value === true}
                  aria-label={`${item.question} Yes`}
                  onClick={() => setAnswer(item.key, true)}
                >
                  Yes
                </Button>
                <Button
                  type="button"
                  variant={value === false ? "default" : "outline"}
                  size="sm"
                  role="radio"
                  aria-checked={value === false}
                  aria-label={`${item.question} No`}
                  onClick={() => setAnswer(item.key, false)}
                >
                  No
                </Button>
              </div>
            </div>
          );
        })}
      </div>
    </FieldShell>
  );
}

/** The account created on the previous screen (setup-page.tsx). Nothing to answer here -- just a confirmation before Stage 1's other questions. */
function AccountStep({ user }: { user: { displayName: string; username: string } | null }) {
  return (
    <FieldShell icon={UserCircle2} label="Your account" copy="You are signed in with the account you just created. Change your name or password later from Settings.">
      <div className="rounded-xl border border-hairline bg-background/35 p-3">
        <p className="text-sm font-semibold text-foreground">{user?.displayName ?? "Your account"}</p>
        <p className="mt-1 text-xs text-muted-foreground">{user ? `Signed in as ${user.username}` : "Signed in"}</p>
      </div>
    </FieldShell>
  );
}

/**
 * #1034 Stage 1 "Time and region": timezone, date/time formats, first day of
 * week and language, detected from the browser/OS and confirmed or changed
 * here. Reuses the same PlatformSettingsSnapshot fields Settings -> Interface
 * already edits (see settings-ui-page.tsx), so nothing here introduces a new
 * setting -- it only asks the question earlier, during first run.
 */
function TimeRegionStep({ form, patch }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void }) {
  return (
    <div className="space-y-(--grid-gap)">
      <FieldShell icon={Globe} label="Language" copy="Controls Deluno's labels and display formatting.">
        <SegmentedControl<string>
          aria-label="Interface language"
          value={form.uiLanguage}
          onValueChange={(value) => patch({ uiLanguage: value })}
          options={[
            { value: "en-AU", label: "English (Australia)" },
            { value: "en-US", label: "English (United States)" }
          ]}
        />
      </FieldShell>

      <FieldShell icon={Clock} label="Timezone" copy="Converts episode air times to when they actually air for you.">
        <div className="flex max-w-md items-center gap-2">
          <Input
            aria-label="Timezone"
            placeholder="e.g. Australia/Sydney"
            value={form.timezone}
            onChange={(event) => patch({ timezone: event.target.value })}
          />
          <Button
            type="button"
            variant="outline"
            onClick={() => patch({ timezone: Intl.DateTimeFormat().resolvedOptions().timeZone })}
          >
            Use this browser
          </Button>
        </div>
      </FieldShell>

      <FieldShell icon={Clock} label="Date, time and week" copy="Used everywhere Deluno shows a date or time.">
        <div className="space-y-3">
          <FieldRow>
            <Field label="First day of week">
              <SegmentedControl<string>
                aria-label="First day of week"
                value={form.calendarFirstDayOfWeek}
                onValueChange={(value) => patch({ calendarFirstDayOfWeek: value })}
                options={[
                  { value: "monday", label: "Monday" },
                  { value: "sunday", label: "Sunday" }
                ]}
              />
            </Field>
            <Field label="Time">
              <SegmentedControl<string>
                aria-label="Time format"
                value={form.timeFormat}
                onValueChange={(value) => patch({ timeFormat: value })}
                options={[
                  { value: "12", label: "12-hour" },
                  { value: "24", label: "24-hour" }
                ]}
              />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Short date">
              <SegmentedControl<string>
                aria-label="Short date format"
                value={form.shortDateFormat}
                onValueChange={(value) => patch({ shortDateFormat: value })}
                options={[
                  { value: "dmy", label: "25/03/2026" },
                  { value: "mdy", label: "03/25/2026" },
                  { value: "iso", label: "2026-03-25" }
                ]}
              />
            </Field>
            <Field label="Long date">
              <SegmentedControl<string>
                aria-label="Long date format"
                value={form.longDateFormat}
                onValueChange={(value) => patch({ longDateFormat: value })}
                options={[
                  { value: "full", label: "Wednesday, 25 March" },
                  { value: "mdy", label: "Wednesday, March 25" }
                ]}
              />
            </Field>
          </FieldRow>
        </div>
      </FieldShell>
    </div>
  );
}

/**
 * #939, asked the same "nothing chosen yet" way #936 asks automation
 * questions. Every address offered is detected, never typed, and answering
 * No here (or leaving the guide before this step) leaves Deluno exactly as
 * it has always behaved -- it is only turned on if this is answered Yes.
 */
/** On a re-run: what the question is set to now, so the person sees the saved answer for what it is (#1245). */
function CurrentSettingNote({ value }: { value: boolean | null | undefined }) {
  if (typeof value !== "boolean") return null;
  return <p className="mt-1 text-(length:--type-caption) text-muted-foreground">Current setting: {value ? "Yes" : "No"}</p>;
}

function AddressesStep({
  form,
  patch,
  candidates,
  currentHost
}: {
  form: GuideForm;
  patch: (patchValue: Partial<GuideForm>) => void;
  candidates: { host: string; reason: string }[];
  currentHost: string;
}) {
  function toggleHost(host: string, checked: boolean) {
    patch({
      allowedHostsSelection: checked
        ? [...form.allowedHostsSelection, host]
        : form.allowedHostsSelection.filter((item) => item.toLowerCase() !== host.toLowerCase())
    });
  }

  const currentHostChecked = form.allowedHostsSelection.some((host) => host.toLowerCase() === currentHost.toLowerCase());

  return (
    <FieldShell
      icon={ShieldCheck}
      label="Only answer on the addresses you use?"
      copy="Off, Deluno answers on any address -- exactly what it has always done. Yes, it only answers on what you tick below, which is safer if this computer is ever reachable from somewhere you don't control."
    >
      <div className="mb-4 flex gap-2" role="radiogroup" aria-label="Only answer on the addresses you use?">
        <Button
          type="button"
          variant={form.restrictAllowedHosts === true ? "default" : "outline"}
          size="sm"
          role="radio"
          aria-checked={form.restrictAllowedHosts === true}
          aria-label="Only answer on the addresses you use? Yes"
          onClick={() => patch({ restrictAllowedHosts: true })}
        >
          Yes
        </Button>
        <Button
          type="button"
          variant={form.restrictAllowedHosts === false ? "default" : "outline"}
          size="sm"
          role="radio"
          aria-checked={form.restrictAllowedHosts === false}
          aria-label="Only answer on the addresses you use? No"
          onClick={() => patch({ restrictAllowedHosts: false })}
        >
          No
        </Button>
      </div>

      <CurrentSettingNote value={form.savedAnswers?.restrictAllowedHosts} />

      {form.restrictAllowedHosts ? (
        <div className="space-y-1">
          {candidates.map((candidate) => (
            <CheckboxRow
              key={candidate.host}
              label={candidate.host}
              description={candidate.reason}
              checked={form.allowedHostsSelection.some((host) => host.toLowerCase() === candidate.host.toLowerCase())}
              onCheckedChange={(checked) => toggleHost(candidate.host, checked)}
            />
          ))}
          {!currentHostChecked ? (
            <p className="mt-2 rounded-lg border border-warning/35 bg-warning/10 px-3 py-2 text-xs leading-relaxed text-warning">
              Tick {currentHost} -- the address you're using right now -- or finishing setup will leave this turned off rather than lock you out.
            </p>
          ) : null}
          <p className="mt-2 text-xs text-muted-foreground">
            localhost always works from this computer. Change this anytime from System &rarr; Security.
          </p>
        </div>
      ) : null}
    </FieldShell>
  );
}

/**
 * #1034 Stage 1 "Other devices on your network": the same Windows Firewall
 * check and one-click allow the tray already runs on first run (#623),
 * explained and offered here too. LanAccessNotice shows nothing when the
 * firewall already lets other devices reach Deluno, or on a host with no
 * firewall for Deluno to look after -- there is nothing to ask in that case.
 */
function NetworkStep({ address }: { address?: string }) {
  return (
    <FieldShell
      icon={MonitorSmartphone}
      label="Other devices on your network"
      copy="Phones, tablets and other computers on your network can only reach Deluno if this computer's firewall allows it. Deluno checks that now."
    >
      <LanAccessNotice showResult address={address} />
      <p className="mt-3 text-xs text-muted-foreground">
        Nothing to do if you only ever use Deluno from this computer. Change this anytime from System.
      </p>
    </FieldShell>
  );
}

/**
 * #1034 Stage 1 "Updates": unanswered by default (#936), the same "nothing
 * chosen yet" way every other setup question is asked. Reuses System ->
 * Updates' own preferences (mode/autoCheck), so Settings keeps a single
 * source of truth for this behaviour.
 */
function UpdatesStep({ form, patch, channel }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void; channel: string }) {
  return (
    <FieldShell
      icon={RefreshCw}
      label="Update Deluno automatically?"
      copy={`Yes, Deluno downloads and applies updates on its own and restarts to use them. No, Deluno checks for updates and tells you first. Either way, you stay on the ${channel} channel; change that from System -> Updates.`}
    >
      <div className="flex gap-2" role="radiogroup" aria-label="Update Deluno automatically?">
        <Button
          type="button"
          variant={form.updateAutomatically === true ? "default" : "outline"}
          size="sm"
          role="radio"
          aria-checked={form.updateAutomatically === true}
          aria-label="Update Deluno automatically? Yes"
          onClick={() => patch({ updateAutomatically: true })}
        >
          Yes
        </Button>
        <Button
          type="button"
          variant={form.updateAutomatically === false ? "default" : "outline"}
          size="sm"
          role="radio"
          aria-checked={form.updateAutomatically === false}
          aria-label="Update Deluno automatically? No"
          onClick={() => patch({ updateAutomatically: false })}
        >
          No
        </Button>
      </div>
      <CurrentSettingNote value={form.savedAnswers?.updateAutomatically} />
    </FieldShell>
  );
}

/**
 * #993, asked the same "nothing chosen yet" way #936 asks every setup
 * question, unlike Allowed Hosts an explicit No here still writes the real
 * (off) setting when setup finishes -- there is no lock-out risk to guard
 * against, so there is nothing to leave unwritten.
 */
function LocalNetworkAuthQuestion({
  form,
  patch,
  currentAddress
}: {
  form: GuideForm;
  patch: (patchValue: Partial<GuideForm>) => void;
  currentAddress: string;
}) {
  const [newEntry, setNewEntry] = useState("");
  const [entryError, setEntryError] = useState<string | null>(null);

  function addEntry() {
    const trimmed = newEntry.trim();
    if (!trimmed) return;
    if (!isValidAddressOrSubnet(trimmed)) {
      setEntryError(`"${trimmed}" is not a valid address or subnet. Use an IP address (192.168.1.20) or a CIDR range (192.168.1.0/28).`);
      return;
    }
    setEntryError(null);
    if (!form.localNetworkAllowList.some((item) => item.toLowerCase() === trimmed.toLowerCase())) {
      patch({ localNetworkAllowList: [...form.localNetworkAllowList, trimmed] });
    }
    setNewEntry("");
  }

  function removeEntry(entry: string) {
    patch({ localNetworkAllowList: form.localNetworkAllowList.filter((item) => item.toLowerCase() !== entry.toLowerCase()) });
  }

  return (
    <FieldShell
      icon={Wifi}
      label="Skip sign-in for devices on your home network?"
      copy={`Off, every device signs in, exactly as Deluno has always done. Yes, a device Deluno can prove is on your local network (not one behind a reverse proxy from outside) skips sign-in. You are connecting from ${plainAddress(currentAddress)}.`}
    >
      <div className="mb-4 flex gap-2" role="radiogroup" aria-label="Skip sign-in for devices on your home network?">
        <Button
          type="button"
          variant={form.skipSignInLocalNetwork === true ? "default" : "outline"}
          size="sm"
          role="radio"
          aria-checked={form.skipSignInLocalNetwork === true}
          aria-label="Skip sign-in for devices on your home network? Yes"
          onClick={() => patch({ skipSignInLocalNetwork: true })}
        >
          Yes
        </Button>
        <Button
          type="button"
          variant={form.skipSignInLocalNetwork === false ? "default" : "outline"}
          size="sm"
          role="radio"
          aria-checked={form.skipSignInLocalNetwork === false}
          aria-label="Skip sign-in for devices on your home network? No"
          onClick={() => patch({ skipSignInLocalNetwork: false })}
        >
          No
        </Button>
      </div>

      <CurrentSettingNote value={form.savedAnswers?.skipSignInLocalNetwork} />

      {form.skipSignInLocalNetwork ? (
        <div className="space-y-2">
          <p className="text-sm font-semibold text-foreground">Narrow this to specific addresses (optional)</p>
          <p className="text-xs text-muted-foreground">
            Leave empty and every local device skips sign-in. Add addresses here and only those skip sign-in -- still only if they are also local.
          </p>
          <div className="space-y-1">
            {form.localNetworkAllowList.map((entry) => (
              <div key={entry} className="flex items-center justify-between gap-2 rounded-lg border border-hairline bg-surface-1 px-3 py-2">
                <span className="text-sm text-foreground">{entry}</span>
                <Button type="button" variant="ghost" size="sm" onClick={() => removeEntry(entry)} aria-label={`Remove ${entry} from the allow list`}>
                  <X className="h-4 w-4" />
                </Button>
              </div>
            ))}
          </div>
          <div className="flex flex-wrap items-end gap-2">
            <Field label="Add an address or subnet" help="192.168.1.20 or 192.168.1.0/28" className="min-w-[16rem] flex-1">
              <Input
                value={newEntry}
                onChange={(event) => {
                  setNewEntry(event.target.value);
                  setEntryError(null);
                }}
                placeholder="192.168.1.0/28"
                onKeyDown={(event) => {
                  if (event.key === "Enter") {
                    event.preventDefault();
                    addEntry();
                  }
                }}
              />
            </Field>
            <Button type="button" variant="outline" onClick={addEntry} disabled={!newEntry.trim()} aria-label="Add address to the allow list">
              <Plus className="h-4 w-4" />
              Add
            </Button>
          </div>
          {entryError ? <p className="text-xs text-destructive">{entryError}</p> : null}
        </div>
      ) : null}
    </FieldShell>
  );
}

/** Same IP/CIDR shape the backend accepts (LocalNetworkMatcher.TryParseCidr). */
function isValidAddressOrSubnet(value: string): boolean {
  const [address, prefix] = value.split("/");
  if (prefix !== undefined && !/^\d+$/.test(prefix)) return false;
  const isIPv4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.test(address);
  if (isIPv4) {
    const octets = address.split(".").map(Number);
    if (octets.some((octet) => octet < 0 || octet > 255)) return false;
    if (prefix !== undefined && Number(prefix) > 32) return false;
    return true;
  }
  // IPv6: a permissive shape check -- the server is the source of truth for full validation.
  const isIPv6 = /^[0-9a-fA-F:]+$/.test(address) && address.includes(":");
  if (isIPv6) {
    if (prefix !== undefined && Number(prefix) > 128) return false;
    return true;
  }
  return false;
}

function FinishStep({
  form,
  patch,
  weirConnected,
  canFinish,
  connectionTestsRequired,
  result,
  error,
  rollbackMessage,
  metadataStatus,
  metadataResults,
  metadataBusy,
  existingMetadataConfigured,
  onMetadataSearch,
  onSelectMetadata,
  route
}: {
  route: { names: string[]; copy: string };
  weirConnected: boolean;
  form: GuideForm;
  patch: (patchValue: Partial<GuideForm>) => void;
  canFinish: boolean;
  connectionTestsRequired: boolean;
  result: SetupCompletion | null;
  error: string | null;
  rollbackMessage: string | null;
  metadataStatus: MetadataProviderStatus | null;
  metadataResults: MetadataSearchResult[];
  metadataBusy: boolean;
  existingMetadataConfigured: boolean;
  onMetadataSearch: () => void;
  onSelectMetadata: (item: MetadataSearchResult) => void;
}) {
  if (result) {
    return <SetupComplete result={result} />;
  }

  return (
    <div className="space-y-(--page-gap)">
      <SummaryStrip
        cells={[
          { label: "Libraries", value: form.mediaIntent === "both" ? "Movies + TV" : form.mediaIntent === "tv" ? "TV" : "Movies" },
          { label: "Quality", value: form.qualityPreset ? QUALITY_PRESETS[form.qualityPreset].label : "Not chosen", tone: form.qualityPreset ? undefined : "warning" },
          { label: "Release preferences", value: form.formatGoal ? FORMAT_GOALS[form.formatGoal].label : "Not chosen", tone: form.formatGoal ? undefined : "warning" },
          // The connection's name, not its protocol id: this read "torznab"
          // and "qbittorrent" where the user had just named them (#259).
          { label: "Search source", value: form.indexerUrl.trim() ? form.indexerName || form.indexerProtocol : "Later" },
          { label: "Download client", value: route.names.length > 2 ? `${route.names.length} clients` : route.names.length > 0 ? route.names.join(", ") : "Later", help: route.names.length > 2 ? "Named below" : undefined }
        ]}
      />
      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-sm font-semibold text-foreground">Import decision preview</p>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              This is the plain-English route Deluno will use before importing anything.
            </p>
          </div>
          <Badge variant={route.names.length > 0 ? "success" : "warning"}>
            {route.names.length > 0 ? "Client route ready" : "Client can be added later"}
          </Badge>
        </div>
        <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
          {buildImportPreviewRows(form, route).map((row) => (
            <div key={row.label} className="rounded-xl border border-hairline bg-background/35 p-3">
              <p className="text-(length:--type-caption) font-bold uppercase tracking-[0.16em] text-muted-foreground">{row.label}</p>
              <p className="mt-1 text-sm font-semibold text-foreground">{row.value}</p>
              <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{row.copy}</p>
            </div>
          ))}
        </div>
      </div>
      {!canFinish ? (
        <p className="rounded-xl border border-warning/25 bg-warning/10 p-4 text-sm text-warning">
          {describeMissingForFinish(form, weirConnected)}
        </p>
      ) : connectionTestsRequired ? (
        // Not a blocker: the baseline can be created now and connected later.
        // Say what it means rather than reading like a requirement (#246).
        <p className="rounded-xl border border-hairline bg-surface-1 p-4 text-sm text-muted-foreground">
          You can create the baseline now. Deluno will not be operationally ready until a search source and a download
          client are connected and tested — the dashboard keeps that step waiting for you.
        </p>
      ) : null}
      {error ? <p className="rounded-xl border border-destructive/25 bg-destructive/10 p-4 text-sm text-destructive">{error}</p> : null}
      {rollbackMessage ? <p className="rounded-xl border border-warning/25 bg-warning/10 p-4 text-sm text-warning">{rollbackMessage}</p> : null}
      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="flex items-center gap-2 text-sm font-semibold text-foreground">
              <Plus className="h-4 w-4 text-primary" />
              Add your first title
            </p>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              Optional. Add one movie or show now so Deluno can immediately start monitoring it after setup.
            </p>
          </div>
          <div className="flex rounded-xl border border-hairline bg-background/35 p-1">
            {(["movies", "tv"] as const).map((value) => (
              <button
                key={value}
                type="button"
                onClick={() => patch({ firstTitleType: value })}
                className={cn(
                  "rounded-lg px-3 py-1.5 text-sm font-semibold transition",
                  form.firstTitleType === value ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:text-foreground"
                )}
              >
                {value === "movies" ? "Movie" : "TV"}
              </button>
            ))}
          </div>
        </div>
        <div className="mt-4 grid gap-3 lg:grid-cols-[minmax(0,1fr)_120px_160px]">
          <Input
            value={form.firstTitle}
            onChange={(event) => patch({ firstTitle: event.target.value })}
            placeholder={form.firstTitleType === "movies" ? "Dune: Part Two" : "Severance"}
          />
          <Input
            value={form.firstTitleYear}
            onChange={(event) => patch({ firstTitleYear: event.target.value.replace(/\D/g, "").slice(0, 4) })}
            placeholder="Year"
            inputMode="numeric"
          />
          <label className="flex h-(--control-height) items-center gap-2 rounded-xl border border-hairline bg-surface-2 px-3 text-sm font-semibold text-foreground">
            <Checkbox
              checked={form.firstTitleMonitored}
              onCheckedChange={(firstTitleMonitored) => patch({ firstTitleMonitored })}
            />
            Monitor
          </label>
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          {!existingMetadataConfigured ? (
            <p className="w-full rounded-xl border border-warning/25 bg-warning/10 p-3 text-xs leading-relaxed text-warning">
              Title matching is not available on this Deluno installation yet. You can finish setup and add this title manually; the administrator can restore matching later without changing your library setup.
            </p>
          ) : null}
          <Button
            type="button"
            variant="secondary"
            onClick={onMetadataSearch}
            disabled={metadataBusy || !existingMetadataConfigured}
            className="gap-2"
          >
            {metadataBusy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Search metadata
          </Button>
          <Badge variant={metadataStatus?.isConfigured ? "success" : "warning"}>
            {metadataStatus?.isConfigured ? "Title matching ready" : "Manual title entry"}
          </Badge>
          {form.firstTitleMetadata ? (
            <Badge variant="info">{form.firstTitleMetadata.provider.toUpperCase()} #{form.firstTitleMetadata.providerId}</Badge>
          ) : null}
        </div>
        {metadataResults.length > 0 ? (
          <div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-3">
            {metadataResults.slice(0, 6).map((item) => (
              <button
                key={`${item.provider}:${item.providerId}`}
                type="button"
                onClick={() => onSelectMetadata(item)}
                className={cn(
                  "flex min-w-0 gap-3 rounded-xl border p-2 text-left transition hover:border-primary/35",
                  form.firstTitleMetadata?.provider === item.provider && form.firstTitleMetadata.providerId === item.providerId
                    ? "border-primary/45 bg-primary/10"
                    : "border-hairline bg-background/35"
                )}
              >
                {item.posterUrl ? (
                  <ArtworkImage src={item.posterUrl} alt="" className="h-16 w-11 rounded-lg object-cover" />
                ) : (
                  <div className="flex h-16 w-11 items-center justify-center rounded-lg bg-muted text-xs text-muted-foreground">No art</div>
                )}
                <span className="min-w-0">
                  <span title={item.title} className="block truncate text-sm font-semibold text-foreground">{item.title}</span>
                  <span className="mt-0.5 block text-xs text-muted-foreground">{item.year ?? "Unknown year"} · {item.provider.toUpperCase()}</span>
                  {item.rating ? <span className="mt-1 block text-(length:--type-caption) tabular-nums text-primary">{item.rating.toFixed(1)} rating</span> : null}
                  {/*
                    The same silence the Add screen had (#424). Setup normally
                    runs against an empty library, but this page is a route like
                    any other - walk it again later and the first-title picker
                    will happily offer something already on the shelf.
                  */}
                  {item.libraryEntryId ? <span className="mt-1 block text-(length:--type-caption) font-semibold text-success">Already in your library</span> : null}
                </span>
              </button>
            ))}
          </div>
        ) : null}
      </div>
      <div className="grid gap-3 lg:grid-cols-3">
        <HandoffTile
          title="Review routing"
          copy="See which indexer and download client each library uses."
          to="/indexers"
        />
        <HandoffTile
          title="Tune quality"
          copy="Adjust generated profiles and custom formats when you are ready."
          to="/settings/profiles"
        />
        <HandoffTile
          title="Watch automation"
          copy="Track scheduled searches, retry windows, queue health, and audit events."
          to="/system"
        />
      </div>
      <p className="text-sm leading-relaxed text-muted-foreground">
        Nothing here is permanent. After setup, open Media Management to refine final destinations, release preferences, quality profiles,
        metadata preferences, tags, multi-library routing, and automation.
      </p>
    </div>
  );
}

function SetupComplete({ result }: { result: SetupCompletion }) {
  return (
    <div className="space-y-(--page-gap)">
      <div className="rounded-2xl border border-success/25 bg-success/10 p-5">
        <p className="flex items-center gap-2 font-display text-2xl font-semibold tracking-tight text-foreground">
          <CheckCircle2 className="h-5 w-5 text-success" />
          Setup complete
        </p>
        <p className="mt-2 text-sm leading-relaxed text-muted-foreground">{result.message} Review the summary below before moving on.</p>
      </div>
      <SummaryStrip
        cells={[
          { label: "Libraries", value: result.libraries.join(" + ") || "None" },
          { label: "Quality Profiles", value: result.qualityProfiles.join(" + ") || "Existing" },
          { label: "Release rules", value: result.customFormatCount > 0 ? `${result.customFormatCount} created` : "Reused existing" },
          { label: "Search source", value: result.indexerName ?? "Later", help: result.indexerName ? "connected" : "add one when you are ready" },
          // The title came from the metadata provider; the client is what will
          // download it. "via {client}" credited the wrong thing (#259).
          { label: "First title", value: result.firstTitle ?? "Skipped", help: result.firstTitle ? "added and monitored" : "add one whenever you like" }
        ]}
      />
      <div className="grid gap-3 lg:grid-cols-4">
        <HandoffTile title="Dashboard" copy="See automation, provider health, queue, and recent activity." to="/" />
        <HandoffTile title="Add titles" copy="Browse and monitor the library you just created." to={result.firstTitlePath ?? "/movies"} />
        <HandoffTile title="Review routing" copy="Confirm indexer and download-client routing." to="/indexers" />
        <HandoffTile title="Media Management" copy="Tune plans, quality, final destinations, metadata, and automation." to="/settings" />
      </div>
    </div>
  );
}

function HandoffTile({ title, copy, to }: { title: string; copy: string; to: string }) {
  return (
    <Link
      to={to}
      className="rounded-2xl border border-hairline bg-surface-1 p-4 transition hover:-translate-y-0.5 hover:border-primary/35"
    >
      <p className="text-sm font-semibold text-foreground">{title}</p>
      <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{copy}</p>
    </Link>
  );
}

function ServiceTestButton({
  status,
  message,
  label,
  onClick
}: {
  status: ServiceTestState["indexer"];
  message: string | null;
  label: string;
  onClick: () => void;
}) {
  const tone =
    status === "passed"
      ? "text-success"
      : status === "failed"
        ? "text-destructive"
        : "text-muted-foreground";

  return (
    <div className="rounded-xl border border-hairline bg-background/35 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Button type="button" variant="secondary" onClick={onClick} disabled={status === "testing"} className="gap-2">
          {status === "testing" ? <Loader2 className="h-4 w-4 animate-spin" /> : <Wifi className="h-4 w-4" />}
          {label}
        </Button>
        <span className={cn("text-xs font-semibold", tone)}>
          {status === "idle" ? "Not tested" : status === "testing" ? "Testing..." : status === "passed" ? "Passed" : "Failed"}
        </span>
      </div>
      {message ? <p className={cn("mt-2 text-xs leading-relaxed", tone)}>{message}</p> : null}
    </div>
  );
}

interface PresetRadioOption {
  id: string;
  label: string;
  copy: string;
  active: boolean;
  onSelect: () => void;
}

function PresetRadioGroup({ label, options }: { label: string; options: PresetRadioOption[] }) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);
  const activeIndex = options.findIndex((option) => option.active);
  const tabIndex = activeIndex >= 0 ? activeIndex : 0;

  function moveFocus(index: number, direction: -1 | 1) {
    if (options.length === 0) return;
    const nextIndex = (index + direction + options.length) % options.length;
    refs.current[nextIndex]?.focus();
    options[nextIndex]?.onSelect();
  }

  return (
    <div role="radiogroup" aria-label={label} className="grid grid-cols-[minmax(0,1fr)] gap-2 sm:grid-cols-2">
      {options.map((option, index) => (
        <button
          key={option.id}
          ref={(element) => { refs.current[index] = element; }}
          type="button"
          role="radio"
          aria-checked={option.active}
          tabIndex={index === tabIndex ? 0 : -1}
          onClick={option.onSelect}
          onKeyDown={(event) => {
            if (event.key === "ArrowRight" || event.key === "ArrowDown") {
              event.preventDefault();
              moveFocus(index, 1);
            } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
              event.preventDefault();
              moveFocus(index, -1);
            }
          }}
          className={cn(
            "rounded-xl border p-3 text-left transition hover:border-primary/35",
            option.active ? "border-primary/45 bg-primary/10" : "border-hairline bg-background/35"
          )}
        >
          <p className="text-sm font-semibold text-foreground">{option.label}</p>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{option.copy}</p>
        </button>
      ))}
    </div>
  );
}

function ChoiceCard({ active, icon: Icon, title, copy, onClick }: { active: boolean; icon: LucideIcon; title: string; copy: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-2xl border p-5 text-left transition hover:-translate-y-0.5",
        active ? "border-primary/40 bg-primary/10" : "border-hairline bg-surface-1 hover:border-primary/25"
      )}
    >
      <Icon className={cn("h-5 w-5", active ? "text-primary" : "text-muted-foreground")} />
      <p className="mt-4 font-display text-xl font-semibold tracking-tight text-foreground">{title}</p>
      <p className="mt-2 text-sm leading-relaxed text-muted-foreground">{copy}</p>
    </button>
  );
}

function FieldShell({ icon: Icon, label, copy, children }: { icon: LucideIcon; label: string; copy: string; children: ReactNode }) {
  return (
    <div className="min-w-0 rounded-2xl border border-hairline bg-surface-1 p-4">
      <div className="mb-4 flex gap-3">
        <Icon className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
        <div className="min-w-0">
          <p className="font-semibold text-foreground">{label}</p>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{copy}</p>
        </div>
      </div>
      {children}
    </div>
  );
}

function buildImportPreviewRows(form: GuideForm, route: { names: string[]; copy: string }) {
  const roots = [
    form.mediaIntent !== "tv" && form.movieRootPath.trim() ? `Movies -> ${form.movieRootPath.trim()}` : null,
    form.mediaIntent !== "movies" && form.seriesRootPath.trim() ? `TV -> ${form.seriesRootPath.trim()}` : null
  ].filter(Boolean).join(" / ");

  return [
    {
      label: "Search",
      value: form.indexerUrl.trim() ? form.indexerName || form.indexerProtocol : "Search source required",
      copy: form.indexerUrl.trim()
        ? `Uses ${form.indexerProtocol.toUpperCase()} categories for ${form.mediaIntent === "both" ? "movies and TV" : form.mediaIntent}.`
        : "Deluno cannot be marked operationally ready until a search source is configured and tested."
    },
    {
      label: "Download",
      value: route.names.length > 0 ? route.names.join(", ") : "Download client required",
      copy: route.copy
    },
    {
      label: "Destination",
      value: roots || "Choose root folders",
      copy: "Destination rules can later override these roots by genre, tag, language, studio, or title."
    },
    {
      label: "Transfer",
      value: form.backupEnabled ? "Single-copy link first + backups" : "Single-copy link first",
      copy: "Imports prefer a single-copy link (hardlink) when storage allows, then fall back to safe copy or move behaviour."
    }
  ];
}

async function recordSetupCompleted(result: SetupCompletion) {
  const response = await authedFetch("/api/setup/completed", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      libraries: result.libraries,
      qualityProfiles: result.qualityProfiles,
      customFormatCount: result.customFormatCount,
      indexerName: result.indexerName,
      clientName: result.clientName,
      firstTitle: result.firstTitle
    })
  });

  if (!response.ok) {
    throw new Error(await readErrorText(response, "Setup audit event could not be recorded."));
  }
}

async function saveSettings(settings: PlatformSettingsSnapshot, form: GuideForm, automation: AutomationChoices) {
  const automationSettings = buildAutomationSettingsPayload(automation);
  const payload = {
    movieRootPath: form.movieRootPath.trim() || settings.movieRootPath,
    seriesRootPath: form.seriesRootPath.trim() || settings.seriesRootPath,
    downloadsPath: settings.downloadsPath,
    incompleteDownloadsPath: settings.incompleteDownloadsPath,
    enableNotifications: settings.enableNotifications,
    useHardlinks: settings.useHardlinks,
    ...automationSettings,
    movieFolderFormat: settings.movieFolderFormat || "{Movie Title} ({Release Year})",
    seriesFolderFormat: settings.seriesFolderFormat || "{Series Title} ({Series Year})",
    episodeFileFormat: settings.episodeFileFormat || "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
    hostBindAddress: settings.hostBindAddress || "127.0.0.1",
    hostPort: settings.hostPort || 5099,
    urlBase: settings.urlBase || "",
    requireAuthentication: true,
    uiTheme: settings.uiTheme || "system",
    defaultMovieView: settings.defaultMovieView || "grid",
    defaultShowView: settings.defaultShowView || "grid",
    metadataCertificationCountry: settings.metadataCertificationCountry || "US",
    metadataLanguage: settings.metadataLanguage || "en",
    metadataProviderMode: settings.metadataProviderMode || "direct",
    metadataBrokerUrl: settings.metadataBrokerUrl || "",
    // #1034 Stage 1 "Time and region": confirmed or changed on the guide's
    // own step, the same fields Settings -> Interface edits.
    uiLanguage: form.uiLanguage || settings.uiLanguage || "en-AU",
    calendarFirstDayOfWeek: form.calendarFirstDayOfWeek || settings.calendarFirstDayOfWeek || "monday",
    shortDateFormat: form.shortDateFormat || settings.shortDateFormat || "dmy",
    longDateFormat: form.longDateFormat || settings.longDateFormat || "full",
    timeFormat: form.timeFormat || settings.timeFormat || "12",
    timezone: form.timezone || settings.timezone || ""
  };

  return await fetchJson<PlatformSettingsSnapshot>("/api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
}

export async function saveAllowedHosts(form: GuideForm) {
  if (!form.restrictAllowedHosts) return;
  await fetchJson("/api/security/allowed-hosts", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enabled: true, allowedHosts: form.allowedHostsSelection })
  });
}

/**
 * #993: "Skip sign-in for devices on your home network?" always writes the
 * real answer, Yes or No -- an existing install's setting cannot already be
 * frozen on the way #936's automation defaults can, so there is no
 * "only ever turns on" rule to preserve here.
 */
export async function saveLocalNetworkAuth(form: GuideForm) {
  if (form.skipSignInLocalNetwork === null) return;
  await fetchJson("/api/security/local-network-auth", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      enabled: form.skipSignInLocalNetwork,
      additionalLocalNetworks: [],
      allowList: form.localNetworkAllowList,
      trustedProxies: []
    })
  });
}

/**
 * #1034: "Update automatically, or ask you first?" reuses System -> Updates'
 * own preferences endpoint. Yes maps to "download and apply on next
 * restart" (the closest existing mode to "just handle it"), No maps to
 * "notify only" -- Deluno still checks either way (autoCheck stays on), it
 * only changes whether an update is applied without asking. The channel the
 * install already has is kept untouched.
 */
export async function saveUpdatePreferences(form: GuideForm, loaded: UpdatePreferencesResponse) {
  if (form.updateAutomatically === null) return;
  await fetchJson("/api/updates/preferences", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      mode: form.updateAutomatically ? "download-apply-on-restart" : "notify-only",
      channel: loaded.channel,
      autoCheck: true
    })
  });
}

/**
 * #944: the content blocklist (#816) is fetched and saved through its own
 * endpoint, not the big flat settings object (see `ContentBlocklistSettings`
 * in lib/api). Every other field (mode, patterns, sync) is kept exactly as
 * loaded - only `enabled` changes, from this Yes/No answer.
 */
export async function saveDangerousFileCheck(form: GuideForm, loaded: ContentBlocklistSettings) {
  if (form.automation.dangerousFileCheck === null) return;
  await fetchJson("/api/download-health/content-blocklist", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ...loaded, enabled: form.automation.dangerousFileCheck === true })
  });
}

/**
 * #1194: a Yes or No to the stall question always writes the real rules. The settings above only seed rules that
 * Deluno has not yet read, so this sets the No progress and Too slow rules themselves: a Yes turns both on at the
 * strike count and speed limit the question showed, a No leaves any such rule off.
 */
export async function saveStallStrikeRules(form: GuideForm) {
  const answer = form.automation.stallStrikes;
  if (answer === null) return;
  const rules = await fetchJson<DownloadHealthRule[]>("/api/download-health/rules");
  for (const kind of ["no-progress", "too-slow"] as const) {
    const existing = rules.find((rule) => rule.kind === kind);
    if (!answer && (!existing || !existing.enabled)) continue;
    const base = existing ?? {
      id: "new", kind, enabled: false, strikeLimit: STALL_STRIKE_COUNT, timerMinutes: STALL_CHECK_EVERY_MINUTES, appliesTo: "both",
      minCompletionPercent: 0, maxCompletionPercent: 100, resetStrikesOnProgress: true, actionPreset: "replace", threshold: 0,
      privacyScope: "both", tooSlowMaxTotalMinutes: null, ignoreAboveSizeBytes: null, skipWhileAlternateSpeedLimitOn: false,
      resetStrikesMinimumProgressBytes: null, privateAction: "remove", blocklistWhenMoved: true
    };
    await fetchJson(`/api/download-health/rules/${existing?.id ?? "new"}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        ...base,
        enabled: answer,
        ...(answer ? { strikeLimit: STALL_STRIKE_COUNT, actionPreset: "replace" } : {}),
        ...(answer && kind === "too-slow" ? { threshold: STALL_SPEED_LIMIT_KBPS } : {})
      })
    });
  }
}

type SelectedFormatGoal = Exclude<GuideForm["formatGoal"], "">;
type SelectedQualityPreset = Exclude<GuideForm["qualityPreset"], "">;

function getFormatBundle(guide: GuidePackage, goal: SelectedFormatGoal): GuideFormatBundle | undefined {
  return guide.bundles.find((bundle) => bundle.id === FORMAT_GOALS[goal].bundleId);
}

export function getEffectiveFormatGoal(mediaType: "movies" | "tv", form: GuideForm): SelectedFormatGoal | null {
  if (!form.formatGoal) return null;
  // The anime bundle is TV-only; films in an anime setup get Balanced rather
  // than a profile with no release rules in it.
  if (form.formatGoal === "anime" && mediaType === "movies") return "balanced";
  // archive-4k-remux is a movies-only bundle; TV shows in an archive setup
  // get Balanced for the same reason.
  if (form.formatGoal === "archive" && mediaType === "tv") return "balanced";
  return form.formatGoal;
}

function getEffectiveFormatBundle(guide: GuidePackage, mediaType: "movies" | "tv", form: GuideForm): GuideFormatBundle | undefined {
  const goal = getEffectiveFormatGoal(mediaType, form);
  return goal ? getFormatBundle(guide, goal) : undefined;
}

function previewFormats(guide: GuidePackage, goal: SelectedFormatGoal): GuideCustomFormat[] {
  const bundle = getFormatBundle(guide, goal);
  if (!bundle) return [];
  // TRaSH's movies and TV copies of a format share a name; show each once.
  const seen = new Set<string>();
  return guideBundleTargets(bundle.includes, bundle.mediaType, guide)
    .map((target) => target.format)
    .filter((format) => !seen.has(format.name) && Boolean(seen.add(format.name)))
    .slice(0, 6);
}

export type CreateBaselineInput = {
  data: SetupGuideLoaderData;
  form: GuideForm;
  liveClients: DownloadClientItem[];
  indexerTested: boolean;
  clientTested: boolean;
  hasMovies: boolean;
  hasTv: boolean;
  canCreateMovies: boolean;
  canCreateTv: boolean;
};

/**
 * Creates the baseline (#1132): the profiles, the sources and clients it routes to, the libraries, and then
 * the answers. Throws on the first problem; every entity made so far is in <paramref name="createdEntities"/>
 * so the caller can take them back, and the answers are applied last so a failure applies none of them.
 */
export async function createBaseline(input: CreateBaselineInput, createdEntities: CreatedEntity[]): Promise<SetupCompletion> {
  const { data, form, hasMovies, hasTv, canCreateMovies, canCreateTv } = input;
  const qualityProfileCache = [...data.qualityProfiles];
  const customFormatCache = [...data.customFormats];
  const movieCustomFormatIds = hasMovies ? await ensureCustomFormats(customFormatCache, "movies", form, data.guide, createdEntities) : [];
  const tvCustomFormatIds = hasTv ? await ensureCustomFormats(customFormatCache, "tv", form, data.guide, createdEntities) : [];
  const movieProfile = hasMovies ? await ensureQualityProfile(qualityProfileCache, "movies", form, movieCustomFormatIds, createdEntities) : null;
  const tvProfile = hasTv ? await ensureQualityProfile(qualityProfileCache, "tv", form, tvCustomFormatIds, createdEntities) : null;
  const indexer = await ensureIndexer(data.indexers, form, input.indexerTested, createdEntities);
  const clients = await ensureClients(input.liveClients, form, input.clientTested, createdEntities);
  const movieLibrary = canCreateMovies
    ? await ensureLibrary(data.libraries, "movies", form.movieRootPath, form.movieDownloadsPath, movieProfile?.id ?? null, form, createdEntities)
    : null;
  const tvLibrary = canCreateTv
    ? await ensureLibrary(data.libraries, "tv", form.seriesRootPath, form.seriesDownloadsPath, tvProfile?.id ?? null, form, createdEntities)
    : null;

  // A library made a moment ago has no sources to lose; one that was already there keeps its own unless an indexer is named.
  const routable = (library: LibraryItem | null) =>
    library && clients.length > 0 && (indexer || createdEntities.some((entity) => entity.kind === "library" && entity.id === library.id));
  await Promise.all([
    routable(movieLibrary) ? saveRouting(movieLibrary!.id, indexer?.id ?? null, clients.map((item) => item.id)) : Promise.resolve(),
    routable(tvLibrary) ? saveRouting(tvLibrary!.id, indexer?.id ?? null, clients.map((item) => item.id)) : Promise.resolve()
  ]);

  // The folder each new library's downloads finish in: the one typed, else the one the connected
  // clients already save into (the picker's completed folder with Movies and TV). Without it the
  // folder check says "Can't tell" and a processor has no folder to watch.
  const noDownloadsFolder: string[] = [];
  for (const [library, typed, rootPath, label] of [
    [movieLibrary, form.movieDownloadsPath, form.movieRootPath, "Movies"],
    [tvLibrary, form.seriesDownloadsPath, form.seriesRootPath, "TV"]
  ] as const) {
    const isNew = library && createdEntities.some((entity) => entity.kind === "library" && entity.id === library.id);
    if (!library || !isNew || typed.trim()) continue;
    const folder = clients.length > 0 ? await detectLibraryDownloadsFolder(library.id).catch(() => null) : null;
    const saved = folder ? await saveLibraryDownloadsFolder(library, rootPath, folder).then(() => true, () => false) : false;
    if (!saved) noDownloadsFolder.push(label);
  }

  // The answers go in last, and the one that can refuse goes first: if it refuses, the
  // libraries above are removed again and none of the answers has been applied.
  await saveSettings(data.settings, form, form.automation);
  const notApplied: string[] = [];
  // #939: only ever turned on by an explicit Yes here, and refused
  // server-side (so left off) if the address answering this very
  // request was not ticked -- setup can never lock itself out.
  await saveAllowedHosts(form).catch(() => notApplied.push("which addresses Deluno answers on"));
  // #993: unlike Allowed Hosts, an explicit No here always writes the
  // real (off) setting -- there is no lock-out risk to guard against.
  await saveLocalNetworkAuth(form).catch(() => notApplied.push("skipping sign-in on your home network"));
  // #1034: same as Local Network Auth -- an explicit Yes or No here
  // always writes the real setting.
  await saveUpdatePreferences(form, data.updatePreferences).catch(() => notApplied.push("how Deluno updates"));
  // #944: an explicit Yes or No here always writes the real setting,
  // unlike Allowed Hosts (which only ever turns on) - an existing
  // install's migration may have already frozen this on, and a plain
  // "No" answer here should still be honored.
  await saveDangerousFileCheck(form, data.contentBlocklist).catch(() => notApplied.push("the dangerous-file check"));
  // #1194: an explicit Yes or No always writes the real stall and slow-speed rules.
  await saveStallStrikeRules(form).catch(() => notApplied.push("the stalled and slow download rules"));
  // "Clean files with Weir before they go into your library?" A Yes sets that library to refine before import and
  // links it to the connected Weir; a No (or no answer) leaves the library as it is.
  for (const [library, answer, outputPath, label] of [
    [movieLibrary, form.refineMovies, form.movieProcessedPath, "Movies"],
    [tvLibrary, form.refineTv, form.seriesProcessedPath, "TV"]
  ] as const) {
    if (!library || answer !== true) continue;
    await refineLibraryWithWeir(library, outputPath, form).catch((problem) =>
      notApplied.push(`cleaning ${label} files with Weir (${problem instanceof Error ? problem.message : "it could not be set up"})`));
  }

  // The first title is a bonus. If it cannot be added the baseline stands, and the screen says so.
  let firstTitle: { id: string; title: string } | null = null;
  let firstTitleProblem: string | null = null;
  if (form.firstTitle.trim()) {
    try {
      firstTitle = await createFirstTitle(form, createdEntities);
    } catch (titleError) {
      firstTitleProblem = titleError instanceof Error ? titleError.message : `Could not add ${form.firstTitle.trim()}.`;
    }
  }

  const created = [movieLibrary ? "Movies" : null, tvLibrary ? "TV Shows" : null].filter(Boolean).join(" and ");
  const completionResult = {
    message: `${created || "Library"} baseline created${firstTitle ? ` and "${firstTitle.title}" was added` : ""}.${
      firstTitleProblem ? ` The first title was not added: ${firstTitleProblem}` : ""
    }${notApplied.length > 0 ? ` These answers could not be saved, so set them in Settings: ${notApplied.join(", ")}.` : ""}${
      noDownloadsFolder.length > 0 ? ` Deluno could not tell where your download clients finish ${noDownloadsFolder.join(" and ")}, so no downloads folder is set yet. Set it on the library so the folder check and your processor can work.` : ""
    }`,
    libraries: [movieLibrary?.name, tvLibrary?.name].filter((item): item is string => Boolean(item)),
    qualityProfiles: [movieProfile?.name, tvProfile?.name].filter((item, index, arr): item is string => Boolean(item) && arr.indexOf(item) === index),
    customFormatCount: createdEntities.filter((item) => item.kind === "customFormat").length,
    indexerName: indexer?.name ?? null,
    clientName: clients.length > 0 ? clients.map((item) => item.name).join(", ") : null,
    firstTitle: firstTitle?.title ?? null,
    firstTitlePath: firstTitle ? (form.firstTitleType === "movies" ? "/movies" : "/tv") : null
  };

  await recordSetupCompleted(completionResult).catch(() => undefined);
  await authedFetch("/api/setup/draft", { method: "DELETE" }).catch(() => undefined);
  return completionResult;
}

/**
 * Sets a library to refine before import and links it to the connected Weir, with the folder Weir leaves the cleaned
 * files in. Weir then sets up its own workflow for the library from this, so nothing is typed in Weir.
 */
export async function refineLibraryWithWeir(library: LibraryItem, outputPath: string, form: GuideForm): Promise<void> {
  const connections = await fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections");
  const weir = (connections ?? []).find((connection) => connection.isEnabled && /weir/i.test(connection.name));
  if (!weir) throw new Error("Weir is not connected to Deluno yet");
  const response = await authedFetch(`/api/libraries/${library.id}/workflow`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      importWorkflow: "refine-before-import",
      processorConnectionId: weir.id,
      processorName: weir.name,
      processorOutputPath: outputPath.trim(),
      processorTimeoutMinutes: library.processorTimeoutMinutes || 2880,
      processorFailureMode: library.processorFailureMode || "block",
      cleanupMode: library.cleanupMode || "keep-source",
      removeEmptySourceFolders: library.removeEmptySourceFolders ?? form.automation.leftoverCleanup === true
    })
  });
  if (!response.ok) throw new Error(await readErrorText(response, "the folder was refused"));
}

async function ensureCustomFormats(existing: CustomFormatItem[], mediaType: "movies" | "tv", form: GuideForm, guide: GuidePackage, createdEntities: CreatedEntity[]): Promise<string[]> {
  const bundle = getEffectiveFormatBundle(guide, mediaType, form);
  if (!bundle) return [];

  const ids: string[] = [];
  // This media type's copy of each format: TRaSH's TV AMZN is not its movies AMZN.
  for (const target of guideBundleTargets(bundle.includes, bundle.mediaType === "all" ? "all" : bundle.mediaType, guide)) {
    if (target.mediaType !== mediaType) continue;
    const format = target.format;

    const existingFormat = existing.find((item) => item.trashId === format.trashId && item.mediaType === mediaType);
    if (existingFormat) {
      ids.push(existingFormat.id);
      continue;
    }

    const created = await createRuleFromGuide(format, mediaType, target.score);
    existing.push(created);
    createdEntities.push({ kind: "customFormat", id: created.id });
    ids.push(created.id);
  }

  return ids;
}

async function ensureQualityProfile(
  existing: QualityProfileItem[],
  mediaType: "movies" | "tv",
  form: GuideForm,
  customFormatIds: string[],
  createdEntities: CreatedEntity[]
) {
  const reusableProfile = existing.find((item) => item.mediaType === mediaType);
  if (!form.qualityPreset) {
    if (reusableProfile) return reusableProfile;
    throw new Error("Choose a picture quality before Deluno can create a quality profile.");
  }
  const preset = QUALITY_PRESETS[form.qualityPreset as SelectedQualityPreset];
  const effectiveGoal = getEffectiveFormatGoal(mediaType, form);
  if (!effectiveGoal) {
    if (reusableProfile) return reusableProfile;
    throw new Error("Choose a release preference before Deluno can create release rules.");
  }
  const goal = FORMAT_GOALS[effectiveGoal];
  const profileName = `${preset.label} - ${goal.label}`;
  const existingProfile = existing.find((item) => item.mediaType === mediaType && item.name === profileName);
  if (existingProfile) return existingProfile;

  const created = await fetchJson<QualityProfileItem>("/api/quality-profiles", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: profileName,
      mediaType,
      cutoffQuality: preset.cutoff,
      allowedQualities: preset.allowed,
      customFormatIds: customFormatIds.join(","),
      upgradeUntilCutoff: true,
      upgradeUnknownItems: form.qualityPreset === "premium4k"
    })
  });
  existing.push(created);
  createdEntities.push({ kind: "qualityProfile", id: created.id });
  return created;
}

async function ensureIndexer(existing: IndexerItem[], form: GuideForm, tested: boolean, createdEntities: CreatedEntity[]) {
  if (!form.indexerUrl.trim()) return null;
  if (!tested) throw new Error("Test the search source before saving it.");

  const item = existing[0]
    ? await fetchJson<IndexerItem>(`/api/indexers/${existing[0].id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildIndexerPayload(form))
      })
    : await fetchJson<IndexerItem>("/api/indexers", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildIndexerPayload(form))
      });
  if (!existing[0]) createdEntities.push({ kind: "indexer", id: item.id });

  const persistedTest = await fetchJson<ConnectionTestResponse>(`/api/indexers/${item.id}/test`, { method: "POST" });
  if (persistedTest.healthStatus !== "healthy") {
    throw new Error(persistedTest.message || "Search source did not pass its persisted connection test.");
  }
  return item;
}

/** A Usenet client that answers and signs in but has no news server yet: connected, and waiting only for the person's provider. */
export function waitingForUsenetProvider(client: Pick<DownloadClientItem, "protocol" | "healthStatus" | "lastHealthMessage">): boolean {
  return (client.protocol === "sabnzbd" || client.protocol === "nzbget")
    && client.healthStatus === "degraded"
    && (client.lastHealthMessage ?? "").startsWith("No Usenet provider set up");
}

/**
 * Clients that are on and connected: what the guide routes to without asking again (#1132). A healthy one is
 * ready; a Usenet client still waiting for its provider is routed too, like the torrent clients, so the
 * libraries already send to it the moment the provider is set (it reads "No Usenet provider set up" until then).
 */
export function connectedDownloadClients(clients: DownloadClientItem[]): DownloadClientItem[] {
  return clients.filter((item) => item.isEnabled && (item.healthStatus === "healthy" || waitingForUsenetProvider(item)));
}

/**
 * Whether a download client is in the way of finishing, decided before anything is written (#1132).
 * Connected clients are used as they are. A form is only needed to add one that is not connected yet,
 * and a form typed in but never tested is refused rather than saved untested.
 */
export function preflightDownloadClient(clients: DownloadClientItem[], form: Pick<GuideForm, "clientHost">, formTested: boolean): string | null {
  if (formTested || connectedDownloadClients(clients).length > 0) return null;
  return form.clientHost.trim()
    ? "Test the download client you entered, or clear it, before creating the baseline."
    : null;
}

/** What the Start step says Movies and TV will be routed to: the connected clients, plus a new one if it was tested. */
export function describeDownloadRoute(clients: DownloadClientItem[], form: Pick<GuideForm, "clientHost" | "clientName" | "clientProtocol">, formTested: boolean): { names: string[]; copy: string } {
  const names = connectedDownloadClients(clients).map((item) => item.name);
  if (formTested && form.clientHost.trim()) {
    const own = form.clientName || form.clientProtocol;
    if (!names.includes(own)) names.push(own);
  }
  return names.length === 0
    ? { names, copy: "Approved releases will not be sent anywhere until a download client is configured." }
    : { names, copy: `Movies and TV downloads go to ${names.join(" and ")}, each with its own categories.` };
}

/** The clients Movies and TV are routed to: every connected one, plus the new one when it was tested (#1132). */
async function ensureClients(existing: DownloadClientItem[], form: GuideForm, tested: boolean, createdEntities: CreatedEntity[]): Promise<DownloadClientItem[]> {
  const connected = connectedDownloadClients(existing);
  if (!tested || !form.clientHost.trim()) return connected;

  const port = Number(form.clientPort || defaultClientPort(form.clientProtocol));
  const same = existing.find((item) =>
    item.protocol === form.clientProtocol
    && (item.host ?? "").toLowerCase() === form.clientHost.trim().toLowerCase()
    && (item.port ?? port) === port);
  // A client already saved at this address is left as it is unless a password was typed; saving the blank form over it would erase its login.
  const item = same
    ? form.clientPassword || form.clientUsername.trim() !== (same.username ?? "")
      ? await fetchJson<DownloadClientItem>(`/api/download-clients/${same.id}`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(buildClientPayload(form))
        })
      : same
    : await fetchJson<DownloadClientItem>("/api/download-clients", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildClientPayload(form))
      });
  if (!same) createdEntities.push({ kind: "client", id: item.id });

  const persistedTest = await fetchJson<ConnectionTestResponse>(`/api/download-clients/${item.id}/test`, { method: "POST" });
  if (persistedTest.healthStatus !== "healthy") {
    throw new Error(persistedTest.message || "Download destination did not pass its persisted connection test.");
  }
  return [...connected.filter((client) => client.id !== item.id), item];
}

function buildIndexerPayload(form: GuideForm) {
  return {
    protocol: form.indexerProtocol,
    baseUrl: form.indexerUrl.trim(),
    apiKey: form.indexerApiKey.trim() || null,
    priority: 10,
    categories: form.mediaIntent === "tv"
      ? "5000,5010,5020,5030,5040,5045,5050"
      : form.mediaIntent === "movies"
        ? "2000,2010,2020,2030,2040,2045,2050"
        : "2000,2010,2020,2030,2040,2045,2050,5000,5010,5020,5030,5040,5045,5050",
    tags: "",
    mediaScope: form.mediaIntent === "both" ? "both" : form.mediaIntent,
    isEnabled: true,
    // #936: nothing automatic is on until the Automation step says so.
    ...buildIndexerAutomationFields(form.automation)
  };
}

function buildClientPayload(form: GuideForm) {
  return {
    protocol: form.clientProtocol,
    host: form.clientHost.trim(),
    port: Number(form.clientPort || defaultClientPort(form.clientProtocol)),
    username: form.clientUsername.trim() || null,
    password: form.clientPassword || null,
    endpointUrl: null,
    moviesCategory: "deluno-movies",
    tvCategory: "deluno-tv",
    categoryTemplate: "deluno-{mediaType}",
    priority: 10,
    isEnabled: true
  };
}

/**
 * The one folder every connected client that has a folder of its own for this library's category saves into
 * (#1132). Null when none says, or when they disagree (the folder check then names the clash).
 */
export async function detectLibraryDownloadsFolder(libraryId: string): Promise<string | null> {
  const report = await fetchJson<{ libraries?: { destinations?: { saveFolder?: string | null; savedBy?: string }[] }[] }>(
    `/api/integrations/processors/download-destinations?libraryId=${encodeURIComponent(libraryId)}`
  );
  const folders = (report?.libraries ?? [])
    .flatMap((item) => item.destinations ?? [])
    .filter((destination) => destination.savedBy === "client-category" && destination.saveFolder?.trim())
    .map((destination) => destination.saveFolder!.trim());
  return folders.length > 0 && folders.every((folder) => samePath(folder, folders[0])) ? folders[0] : null;
}

async function saveLibraryDownloadsFolder(library: LibraryItem, rootPath: string, downloadsPath: string) {
  const response = await authedFetch(`/api/libraries/${library.id}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name: library.name, rootPath: library.rootPath || rootPath, downloadsPath })
  });
  if (!response.ok) {
    throw new Error(await readErrorText(response, "The downloads folder could not be saved."));
  }
}

async function ensureLibrary(
  existing: LibraryItem[],
  mediaType: "movies" | "tv",
  rootPath: string,
  downloadsPath: string,
  qualityProfileId: string | null,
  form: GuideForm,
  createdEntities: CreatedEntity[]
) {
  const existingLibrary = existing.find((item) => item.mediaType === mediaType);
  if (existingLibrary) return existingLibrary;

  const created = await fetchJson<LibraryItem>("/api/libraries", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: mediaType === "movies" ? "Movies" : "TV Shows",
      mediaType,
      purpose: "Main library",
      rootPath,
      downloadsPath: downloadsPath.trim() || null,
      qualityProfileId,
      // #936: nothing automatic is on until the Automation step says so.
      ...buildLibraryAutomationFields(form.automation),
      searchIntervalMinutes: 720,
      retryDelayHours: 6,
      maxItemsPerRun: form.qualityPreset === "premium4k" ? 15 : 10
    })
  });
  createdEntities.push({ kind: "library", id: created.id });
  return created;
}

async function createFirstTitle(form: GuideForm, createdEntities: CreatedEntity[]): Promise<{ id: string; title: string }> {
  const title = form.firstTitle.trim();
  if (!title) throw new Error("First title is empty.");

  const year = form.firstTitleYear.trim() ? Number(form.firstTitleYear.trim()) : null;
  const endpoint = form.firstTitleType === "movies" ? "/api/movies" : "/api/series";
  const payload =
    form.firstTitleType === "movies"
      ? {
          title,
          releaseYear: year,
          imdbId: form.firstTitleMetadata?.imdbId ?? null,
          monitored: form.firstTitleMonitored,
          ...metadataCreatePayload(form.firstTitleMetadata)
        }
      : {
          title,
          startYear: year,
          imdbId: form.firstTitleMetadata?.imdbId ?? null,
          monitored: form.firstTitleMonitored,
          ...metadataCreatePayload(form.firstTitleMetadata)
        };

  const response = await authedFetch(endpoint, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    throw new Error(await readErrorText(response, `Could not add ${title}.`));
  }

  const created = await response.json() as { id: string; title: string };
  createdEntities.push({ kind: form.firstTitleType === "movies" ? "movie" : "series", id: created.id });
  return created;
}

async function rollbackCreatedEntities(createdEntities: CreatedEntity[]) {
  const reversed = [...createdEntities].reverse();
  let removed = 0;
  for (const entity of reversed) {
    const endpoint = rollbackEndpoint(entity);
    if (!endpoint) continue;
    try {
      const response = await authedFetch(endpoint, { method: "DELETE" });
      if (response.ok || response.status === 404 || response.status === 204) removed += 1;
    } catch {
      // Best effort rollback. The error shown to the user remains the original setup failure.
    }
  }
  return removed > 0
    ? `Setup failed, so Deluno cleaned up ${removed} partially created item${removed === 1 ? "" : "s"}.`
    : "Setup failed before Deluno created anything that needed cleanup.";
}

function rollbackEndpoint(entity: CreatedEntity) {
  switch (entity.kind) {
    case "library":
      return `/api/libraries/${entity.id}`;
    case "indexer":
      return `/api/indexers/${entity.id}`;
    case "client":
      return `/api/download-clients/${entity.id}`;
    case "qualityProfile":
      return `/api/quality-profiles/${entity.id}`;
    case "customFormat":
      return `/api/custom-formats/${entity.id}`;
    default:
      return null;
  }
}

async function saveRouting(libraryId: string, indexerId: string | null, downloadClientIds: string[]) {
  const response = await authedFetch(`/api/libraries/${libraryId}/routing`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      sources: indexerId ? [{ indexerId, priority: 1, requiredTags: "", excludedTags: "" }] : [],
      downloadClients: downloadClientIds.map((downloadClientId, index) => ({ downloadClientId, priority: index + 1 }))
    })
  });
  if (!response.ok) {
    throw new Error(await readErrorText(response, "Routing could not be saved."));
  }
}

function defaultClientPort(protocol: string) {
  return {
    qbittorrent: "8080",
    sabnzbd: "8080",
    transmission: "9091",
    deluge: "8112",
    nzbget: "6789",
    utorrent: "8080"
  }[protocol] ?? "8080";
}
