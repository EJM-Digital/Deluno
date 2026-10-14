/**
 * Automation & Recovery — list → drawer plus one page-level form.
 *
 *   PageToolbar (Resume/Pause automation)
 *   SummaryStrip (automation · searching · due · source checks · sent)
 *   ListCard  libraries (schedule · next search · last result · status · on · ›)
 *             → drawer: Schedule · Budget · Search window · Run now / Skip
 *   ListCard  failed-download handling (page form, saved by PageFooter)
 *   ListCard  recent cycles
 *
 * Contracts: PUT /api/settings/automation, PATCH /api/settings,
 * PUT /api/libraries/{id}/automation, POST …/search-now, POST …/skip-cycle.
 */
import { formatBytes } from "../lib/utils";
import { toneSortValue } from "../lib/status-tones";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useLoaderData, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import { Loader2, Pause, Play, SkipForward, Zap } from "lucide-react";
import { Button } from "../components/ui/button";
import { Textarea } from "../components/ui/textarea";
import { Chip, type ChipProps } from "../components/ui/chip";
import { Drawer, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { BlockedReleasesConsole } from "../components/app/blocked-releases-console";
import type { ImportFailureRule } from "../lib/failure-reasons";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SummaryStrip } from "../components/ui/summary-strip";
import { ListGroupHeader, MediaTypeFilter, useMediaTypeSplit } from "../components/ui/media-type-split";
import { PresetField } from "../components/ui/preset-field";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { automationNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { ConfigAreas } from "../lib/config-areas";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { RealtimeGroups, useLiveEvent, useSignalREvent } from "../lib/use-signalr";
import {
  fetchJson, fetchPageItems,
  type LibraryAutomationStateItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityModelSnapshot,
  type SearchCycleRunItem,
  type BlockedRelease,
  type RecycleBinSettings,
  type ContentBlocklistSettings,
  emptyContentBlocklistSettings
} from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { formatSearchInterval } from "../lib/search-interval-format";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

// #984: minutes are the unit on the wire so the interval can go below an
// hour (Huntarr-style hunting cadence). The presets stay in plain words;
// only "Custom interval" asks for a raw number, and that number is minutes.
const INTERVAL_OPTIONS = [
  { value: "15", label: "Every 15 minutes" },
  { value: "30", label: "Every 30 minutes" },
  { value: "60", label: "Every hour" },
  { value: "180", label: "Every 3 hours" },
  { value: "360", label: "Every 6 hours" },
  { value: "720", label: "Every 12 hours" },
  { value: "1440", label: "Daily" }
];
const RETRY_OPTIONS = [
  { value: "1", label: "1 hour" },
  { value: "3", label: "3 hours" },
  { value: "6", label: "6 hours" },
  { value: "12", label: "12 hours" },
  { value: "24", label: "Daily" }
];
const BATCH_OPTIONS = [
  { value: "5", label: "5 titles" },
  { value: "10", label: "10 titles" },
  { value: "25", label: "25 titles" },
  { value: "50", label: "50 titles" }
];
const HOUR_OPTIONS = Array.from({ length: 24 }, (_, hour) => ({ value: String(hour), label: `${String(hour).padStart(2, "0")}:00` }));

interface LoaderData {
  automationStates: LibraryAutomationStateItem[];
  libraries: LibraryItem[];
  qualityModel: QualityModelSnapshot;
  settings: PlatformSettingsSnapshot;
  searchCycles: SearchCycleRunItem[];
  blockedReleases: BlockedRelease[];
  failureRules: ImportFailureRule[];
  recycleBinSettings: RecycleBinSettings;
  contentBlocklist: ContentBlocklistSettings;
}

export async function searchCyclesLoader(): Promise<LoaderData> {
  const [automationStates, libraries, qualityModel, settings, searchCycles, blockedReleases, failureRules, recycleBinSettings, contentBlocklist] = await Promise.all([
    fetchPageItems<LibraryAutomationStateItem>("/api/library-automation?pageSize=50"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityModelSnapshot>("/api/quality-model"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchPageItems<SearchCycleRunItem>("/api/search-cycles?pageSize=50"),
    fetchJson<BlockedRelease[]>("/api/blocked-releases").catch(() => []),
    fetchJson<ImportFailureRule[]>("/api/failure-rules").catch(() => []),
    fetchJson<RecycleBinSettings>("/api/recycle-bin/settings"),
    // #816: its own small endpoint, not part of the flat settings object.
    fetchJson<ContentBlocklistSettings>("/api/download-health/content-blocklist").catch(() => emptyContentBlocklistSettings)
  ]);
  return { automationStates, libraries, qualityModel, settings, searchCycles, blockedReleases, failureRules, recycleBinSettings, contentBlocklist };
}

interface AutomationForm {
  autoSearchEnabled: boolean;
  missingSearchEnabled: boolean;
  upgradeSearchEnabled: boolean;
  searchIntervalMinutes: string;
  retryDelayHours: string;
  maxItemsPerRun: string;
  /** How many missing-item candidates a cycle works through, independent of maxUpgradeItemsPerRun (#830). */
  maxMissingItemsPerRun: string;
  /** How many upgrade candidates a cycle works through, independent of maxMissingItemsPerRun (#830). */
  maxUpgradeItemsPerRun: string;
  searchWindowStartHour: string;
  searchWindowEndHour: string;
  /** One flag per day (index 0 = Sunday ... 6 = Saturday), matching .NET DayOfWeek and JS Date.getDay() (#832). */
  searchWindowDays: boolean[];
  randomizeSearchOrder: boolean;
}

/** Sunday-first, matching .NET DayOfWeek / JS Date.getDay() so the UI bit order matches the stored mask exactly (#832). */
const WEEKDAYS = [
  { bit: 0, label: "Sun" },
  { bit: 1, label: "Mon" },
  { bit: 2, label: "Tue" },
  { bit: 3, label: "Wed" },
  { bit: 4, label: "Thu" },
  { bit: 5, label: "Fri" },
  { bit: 6, label: "Sat" }
];
const ALL_DAYS_MASK = 0b111_1111;
const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

function daysMaskToFlags(mask: number | undefined): boolean[] {
  const value = !mask || mask <= 0 || mask > ALL_DAYS_MASK ? ALL_DAYS_MASK : mask;
  return WEEKDAYS.map(({ bit }) => ((value >> bit) & 1) === 1);
}
function daysFlagsToMask(flags: boolean[]): number {
  const mask = flags.reduce((total, on, index) => (on ? total | (1 << index) : total), 0);
  return mask === 0 ? ALL_DAYS_MASK : mask;
}

/**
 * What Deluno does with the download client's copy once a title is safely in
 * the library (#288). It lives on this screen because it answers the same
 * question the cleanup settings below it do — what happens on its own, without
 * being asked — and it used to live on the library instead, which split "after
 * a download finishes" and "when a download goes wrong" across two screens.
 */
interface SharingForm {
  mode: string;
  forHours: string;
  untilRatio: string;
  stuckAction: string;
  stuckAfterDays: string;
  // #815: which tracker-privacy downloads this policy covers.
  privacyScope: string;
  /** "reclaim" or "stop" — what happens once the target above is reached (#821). */
  completionAction: string;
}

interface CleanupForm {
  strikeThreshold: string;
  blockRelease: boolean;
  queueReplacement: boolean;
  removeClientEntry: boolean;
  purgePayload: boolean;
  // #659: when a download counts as stuck, before any of the above ever
  // runs. Each rule is its own switch; "no progress" is on out of the box,
  // the other two are sharper floors a person opts into once they know
  // their own swarms.
  noProgressEnabled: boolean;
  noProgressMinutes: string;
  minSpeedEnabled: boolean;
  minSpeedKbps: string;
  minSpeedMinutes: string;
  minPeersEnabled: boolean;
  minPeers: string;
  minPeersMinutes: string;
  // #829: a single hourly search-call ceiling across every indexer
  // combined, alongside a library's own "titles per run" limit. Off by
  // default (#936).
  maxSearchesPerHourEnabled: boolean;
  maxSearchesPerHour: string;
  // #1002: hold the automatic search cycles while many downloads are
  // waiting. Off by default (#936); searches you start are never held.
  pauseSearchWhenQueueLongEnabled: boolean;
  pauseSearchWhenQueueLongThreshold: string;
  // #818: zero seeders, or the tracker itself saying unregistered, past a
  // grace period since the download was added. Off by default (#936).
  deadTorrentEnabled: boolean;
  deadTorrentGraceMinutes: string;
  // Checkpoint A (#917): opens a finished download and checks its resolution,
  // runtime and playability against what its name and the catalogue claim,
  // before a processor or the library ever sees it. Off by default (#936).
  downloadIntegrityCheckEnabled: boolean;
  downloadIntegrityMinimumRuntimePercent: string;
  // #879: acted on through #816's file-content check, not the release name.
  dangerousFileEnabled: boolean;
}

interface ContentBlocklistForm {
  enabled: boolean;
  mode: "blacklist" | "whitelist";
  useBuiltInPatterns: boolean;
  customPatterns: string;
  remoteUrl: string;
  syncToQbittorrentEnabled: boolean;
}

function contentBlocklistFrom(settings: ContentBlocklistSettings): ContentBlocklistForm {
  return {
    enabled: settings.enabled,
    mode: settings.mode === "whitelist" ? "whitelist" : "blacklist",
    useBuiltInPatterns: settings.useBuiltInPatterns,
    customPatterns: settings.customPatterns.join("\n"),
    remoteUrl: settings.remoteUrl ?? "",
    syncToQbittorrentEnabled: settings.syncToQbittorrentEnabled
  };
}

/** What else on the page reads again when it changes: libraries, settings, the quality model and the download-health rules. */
const SEARCH_PAGE_REFRESH: LiveRefreshSpec = {
  events: ["LibraryChanged", "SettingsChanged", "QualityProfileChanged"],
  areas: [ConfigAreas.downloadHealth, ConfigAreas.qualityModel]
};

export function SearchCyclesPage() {
  const { automationStates: loadedAutomationStates, libraries, qualityModel: loadedQualityModel, settings, searchCycles: loadedSearchCycles, blockedReleases, failureRules, recycleBinSettings, contentBlocklist: loadedContentBlocklist } = useLoaderData() as LoaderData;
  const location = useLocation();
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const contentBlocklistMutation = useApiMutation<ContentBlocklistSettings, ContentBlocklistSettings>("/api/download-health/content-blocklist", "PUT");

  // The schedule follows the server (#1179): when a library's automation moves, a search cycle
  // starts, advances or finishes, only the two lists that show it are read again, and nothing
  // asks on a timer. Everything else on the page is read again when its own data changes.
  const [liveSchedule, setLiveSchedule] = useState<{ source: unknown; automationStates: LibraryAutomationStateItem[]; searchCycles: SearchCycleRunItem[] } | null>(null);
  const loadedSchedule = useMemo(() => ({ automationStates: loadedAutomationStates, searchCycles: loadedSearchCycles }), [loadedAutomationStates, loadedSearchCycles]);
  // A newer answer from the loader replaces whatever was pushed in before it.
  const automationStates = liveSchedule && liveSchedule.source === loadedSchedule ? liveSchedule.automationStates : loadedAutomationStates;
  const searchCycles = liveSchedule && liveSchedule.source === loadedSchedule ? liveSchedule.searchCycles : loadedSearchCycles;
  const reloadSchedule = useCoalescedRevalidate(() => {
    void Promise.all([
      fetchPageItems<LibraryAutomationStateItem>("/api/library-automation?pageSize=50"),
      fetchPageItems<SearchCycleRunItem>("/api/search-cycles?pageSize=50")
    ]).then(([nextStates, nextCycles]) => setLiveSchedule({ source: loadedSchedule, automationStates: nextStates, searchCycles: nextCycles })).catch(() => undefined);
  }, 400);
  useSignalREvent("AutomationStateChanged", RealtimeGroups.Dashboard, reloadSchedule);
  useSignalREvent("SearchRunCompleted", RealtimeGroups.Dashboard, reloadSchedule);
  useLiveEvent("SearchCycleProgress", reloadSchedule);
  useLiveRefresh(SEARCH_PAGE_REFRESH);

  const view = location.pathname.endsWith("/missing") ? "missing" : location.pathname.endsWith("/upgrades") ? "upgrades" : location.pathname.endsWith("/failed-downloads") ? "failed" : location.pathname.endsWith("/blocklist") ? "blocklist" : "overview";
  const scheduleLibraries = useMemo(
    () => view === "missing" ? libraries.filter((library) => library.missingSearchEnabled) : view === "upgrades" ? libraries.filter((library) => library.upgradeSearchEnabled) : libraries,
    [libraries, view]
  );
  const split = useMediaTypeSplit(scheduleLibraries, (library) => library.mediaType);
  const stateByLibrary = useMemo(() => new Map(automationStates.map((state) => [state.libraryId, state])), [automationStates]);
  const running = automationStates.filter((state) => state.status === "running").length;
  const due = automationStates.filter((state) => state.status !== "paused" && (!state.nextSearchUtc || new Date(state.nextSearchUtc) <= new Date())).length;
  const cycleCost = useMemo(
    () =>
      searchCycles.reduce(
        (summary, cycle) => {
          const notes = parseNotes(cycle.notesJson);
          summary.apiCalls += notes.apiCallCount;
          summary.queuedBytes += notes.queuedReleaseBytes;
          return summary;
        },
        { apiCalls: 0, queuedBytes: 0 }
      ),
    [searchCycles]
  );
  const cyclesShown = useMemo(() => searchCycles.slice(0, 12), [searchCycles]);

  /* ------------------------------------------------------ global + rows */
  const [busy, setBusy] = useState<string | null>(null);

  async function run(key: string, action: () => Promise<unknown>, success?: string) {
    setBusy(key);
    try {
      await action();
      if (success) toast.success(success);
      revalidator.revalidate();
      return true;
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Action failed.");
      return false;
    } finally {
      setBusy(null);
    }
  }

  async function toggleGlobal() {
    const enabling = !settings.autoStartJobs;
    await run("global", async () => {
      const response = await authedFetch("/api/settings/automation", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ isEnabled: enabling }) });
      if (!response.ok) throw new Error("Could not update automation.");
    }, enabling ? "Automation resumed" : "Automation paused — external downloads are unchanged");
  }

  async function putAutomation(library: LibraryItem, form: AutomationForm) {
    const response = await authedFetch(`/api/libraries/${library.id}/automation`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        autoSearchEnabled: form.autoSearchEnabled,
        missingSearchEnabled: form.missingSearchEnabled,
        upgradeSearchEnabled: form.upgradeSearchEnabled,
        searchIntervalMinutes: Number(form.searchIntervalMinutes || 360),
        retryDelayHours: Number(form.retryDelayHours || 6),
        maxItemsPerRun: Number(form.maxItemsPerRun || 10),
        maxMissingItemsPerRun: Number(form.maxMissingItemsPerRun || form.maxItemsPerRun || 10),
        maxUpgradeItemsPerRun: Number(form.maxUpgradeItemsPerRun || form.maxItemsPerRun || 10),
        searchWindowStartHour: form.searchWindowStartHour === "" ? null : Number(form.searchWindowStartHour),
        searchWindowEndHour: form.searchWindowEndHour === "" ? null : Number(form.searchWindowEndHour),
        searchWindowDays: daysFlagsToMask(form.searchWindowDays),
        randomizeSearchOrder: form.randomizeSearchOrder
      })
    });
    if (!response.ok) throw new Error("Automation could not be saved.");
  }

  async function toggleLibrary(library: LibraryItem, enabled: boolean) {
    await run(`toggle:${library.id}`, () => putAutomation(library, { ...automationFrom(library), autoSearchEnabled: enabled }));
    if (drawerId === library.id && !drawerDirty) {
      const next = { ...form, autoSearchEnabled: enabled };
      setForm(next);
      setInitialForm(next);
    }
  }

  /* ---------------------------------------------------------- drawer */
  const [drawerId, setDrawerId] = useState<string | null>(null);
  const [form, setForm] = useState<AutomationForm>(() => emptyAutomation());
  const [initialForm, setInitialForm] = useState<AutomationForm>(() => emptyAutomation());
  const [drawerState, setDrawerState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [drawerMessage, setDrawerMessage] = useState<string | null>(null);

  const editing = drawerId ? libraries.find((library) => library.id === drawerId) ?? null : null;
  const drawerDirty = drawerId !== null && JSON.stringify(form) !== JSON.stringify(initialForm);
  const drawerFooterState: DrawerSaveState = resolveDrawerSaveState(drawerState, drawerDirty);
  useEffect(() => {
    if (shouldClearSaveState(drawerState, drawerDirty)) setDrawerState(undefined);
  }, [drawerDirty, drawerState]);

  function openLibrary(library: LibraryItem) {
    const next = automationFrom(library);
    setDrawerId(library.id);
    setForm(next);
    setInitialForm(next);
    setDrawerState(undefined);
  }

  useEffect(() => {
    if (drawerId !== null) return;
    const libraryId = new URLSearchParams(location.search).get("libraryId");
    if (!libraryId) return;
    const library = libraries.find((item) => item.id === libraryId);
    if (!library) return;
    openLibrary(library);
    navigate(location.pathname, { replace: true });
  }, [drawerId, libraries, location.pathname, location.search, navigate]);

  async function submitDrawer(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editing || busy) return;
    setBusy("save");
    setDrawerState("saving");
    try {
      await putAutomation(editing, form);
      setInitialForm(form);
      setDrawerState("saved");
      setDrawerMessage("Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setDrawerState("error");
      setDrawerMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(null);
    }
  }

  /* ------------------------------------------------- failed downloads */
  // The baseline is state, not a memo over `settings`: this page revalidates every
  // 10s, so deriving it would leave the form "dirty" for the whole round trip after
  // a save — long enough for the effect below to wipe "Saved just now" off the footer.
  const [savedCleanup, setSavedCleanup] = useState<CleanupForm>(() => cleanupFrom(settings));
  const [cleanup, setCleanup] = useState<CleanupForm>(savedCleanup);
  const [savedSharing, setSavedSharing] = useState<SharingForm>(() => sharingFrom(settings));
  const [sharing, setSharing] = useState<SharingForm>(savedSharing);
  const [savedContentBlocklist, setSavedContentBlocklist] = useState<ContentBlocklistForm>(() => contentBlocklistFrom(loadedContentBlocklist));
  const [contentBlocklist, setContentBlocklist] = useState<ContentBlocklistForm>(savedContentBlocklist);
  const [savedQualityModel, setSavedQualityModel] = useState(loadedQualityModel);
  const [qualityModel, setQualityModel] = useState(loadedQualityModel);
  const [cleanupState, setCleanupState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [cleanupMessage, setCleanupMessage] = useState<string | null>(null);

  const cleanupDirty = !same(cleanup, savedCleanup) || !same(sharing, savedSharing) || !same(contentBlocklist, savedContentBlocklist);
  const qualityDirty = !same(qualityModel, savedQualityModel);
  const automationDirty = cleanupDirty || qualityDirty;
  const settingsCleanup = useMemo(() => cleanupFrom(settings), [settings]);
  const settingsSharing = useMemo(() => sharingFrom(settings), [settings]);
  const settingsContentBlocklist = useMemo(() => contentBlocklistFrom(loadedContentBlocklist), [loadedContentBlocklist]);
  useEffect(() => {
    // Adopt server state only when the user has nothing unsaved in this form.
    if (cleanupDirty || same(savedCleanup, settingsCleanup)) return;
    setSavedCleanup(settingsCleanup);
    setCleanup(settingsCleanup);
    setSavedSharing(settingsSharing);
    setSharing(settingsSharing);
    setSavedContentBlocklist(settingsContentBlocklist);
    setContentBlocklist(settingsContentBlocklist);
  }, [cleanupDirty, savedCleanup, settingsCleanup, settingsSharing, settingsContentBlocklist]);

  const cleanupFooter: DrawerSaveState = resolveDrawerSaveState(cleanupState, automationDirty);
  useUnsavedChanges(automationDirty || drawerDirty);
  useEffect(() => {
    if (shouldClearSaveState(cleanupState, automationDirty)) setCleanupState(undefined);
  }, [automationDirty, cleanupState]);

  async function submitCleanup(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (cleanupState === "saving") return;
    setCleanupState("saving");
    try {
      const [, nextQualityModel] = await Promise.all([
        settingsMutation.mutate({
            downloadHealthStrikeThreshold: Math.max(1, Math.min(100, Number(cleanup.strikeThreshold || 3))),
            cleanupBlockReleaseAfterThreshold: cleanup.blockRelease,
            cleanupQueueReplacementAfterThreshold: cleanup.queueReplacement,
            cleanupRemoveClientEntryAfterThreshold: cleanup.removeClientEntry,
            cleanupPurgePayloadAfterThreshold: cleanup.purgePayload,
            downloadHealthNoProgressEnabled: cleanup.noProgressEnabled,
            downloadHealthNoProgressMinutes: Math.max(1, Math.min(1440, Number(cleanup.noProgressMinutes || 30))),
            downloadHealthMinSpeedEnabled: cleanup.minSpeedEnabled,
            downloadHealthMinSpeedKbps: Math.max(1, Number(cleanup.minSpeedKbps || 50)),
            downloadHealthMinSpeedMinutes: Math.max(1, Math.min(1440, Number(cleanup.minSpeedMinutes || 30))),
            downloadHealthMinPeersEnabled: cleanup.minPeersEnabled,
            downloadHealthMinPeers: Math.max(0, Number(cleanup.minPeers || 1)),
            downloadHealthMinPeersMinutes: Math.max(1, Math.min(1440, Number(cleanup.minPeersMinutes || 30))),
            maxSearchesPerHourEnabled: cleanup.maxSearchesPerHourEnabled,
            maxSearchesPerHour: Math.max(1, Math.min(100_000, Number(cleanup.maxSearchesPerHour || 200))),
            pauseSearchWhenQueueLongEnabled: cleanup.pauseSearchWhenQueueLongEnabled,
            pauseSearchWhenQueueLongThreshold: Math.max(1, Math.min(10_000, Number(cleanup.pauseSearchWhenQueueLongThreshold || 20))),
            downloadHealthDeadTorrentEnabled: cleanup.deadTorrentEnabled,
            downloadHealthDeadTorrentGraceMinutes: Math.max(0, Math.min(1440, Number(cleanup.deadTorrentGraceMinutes || 60))),
            downloadIntegrityCheckEnabled: cleanup.downloadIntegrityCheckEnabled,
            downloadIntegrityMinimumRuntimePercent: Math.max(1, Math.min(100, Number(cleanup.downloadIntegrityMinimumRuntimePercent || 80))),
            downloadHealthDangerousFileEnabled: cleanup.dangerousFileEnabled,
            sharingMode: sharing.mode,
            // A blank target is sent as null, which the API stores as
            // "deliberately not part of this rule" rather than "never set".
            sharingForHours: sharing.forHours.trim() === "" ? null : Math.max(1, Number(sharing.forHours)),
            sharingUntilRatio: sharing.untilRatio.trim() === "" ? null : Math.max(0, Number(sharing.untilRatio)),
            sharingStuckAction: sharing.stuckAction,
            sharingStuckAfterDays: Math.max(1, Number(sharing.stuckAfterDays || 14)),
            sharingPrivacyScope: sharing.privacyScope,
            sharingCompletionAction: sharing.completionAction
        }),
        fetchJson<QualityModelSnapshot>("/api/quality-model", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ tiers: loadedQualityModel.tiers, upgradeStop: qualityModel.upgradeStop })
        }),
        contentBlocklistMutation.mutate({
          enabled: contentBlocklist.enabled,
          mode: contentBlocklist.mode,
          useBuiltInPatterns: contentBlocklist.useBuiltInPatterns,
          customPatterns: contentBlocklist.customPatterns.split("\n").map((line) => line.trim()).filter(Boolean),
          remoteUrl: contentBlocklist.remoteUrl.trim() === "" ? null : contentBlocklist.remoteUrl.trim(),
          remotePatterns: loadedContentBlocklist.remotePatterns,
          lastRefreshedUtc: loadedContentBlocklist.lastRefreshedUtc,
          syncToQbittorrentEnabled: contentBlocklist.syncToQbittorrentEnabled,
          updatedUtc: loadedContentBlocklist.updatedUtc
        })
      ]);
      setSavedContentBlocklist(contentBlocklist);
      // Move the baseline first: the revalidation below is slower than the render.
      setSavedCleanup(cleanup);
      setSavedSharing(sharing);
      setSavedQualityModel(nextQualityModel);
      setQualityModel(nextQualityModel);
      setCleanupState("saved");
      setCleanupMessage("Saved just now");
    } catch (error) {
      setCleanupState("error");
      setCleanupMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  /* ---------------------------------------------------------- render */
  const paused = !settings.autoStartJobs;

  return (
    <form onSubmit={submitCleanup} className="flex flex-col gap-(--page-gap)" noValidate>
      <PageToolbar
        tabs={automationNavItems}
        actions={
          <Button type="button" variant={paused ? "default" : "outline"} onClick={() => void toggleGlobal()} disabled={busy === "global"}>
            {busy === "global" ? <Loader2 className="h-4 w-4 animate-spin" /> : paused ? <Play className="h-4 w-4" /> : <Pause className="h-4 w-4" />}
            {paused ? "Resume automation" : "Pause automation"}
          </Button>
        }
      />


      {view === "overview" ? (
        <SummaryStrip
          cells={[
            { label: "Automation", value: paused ? "Paused" : "Running", help: paused ? "Queued work is held safely" : "Deluno searches on schedule", tone: paused ? "warning" : undefined },
            { label: "Searching now", value: String(running), help: running ? "libraries mid-cycle" : "nothing running" },
            { label: "Due", value: String(due), help: due ? "libraries ready for a cycle" : "all caught up" },
            { label: "Source checks", value: cycleCost.apiCalls.toLocaleString(), help: "in the last 50 cycles" },
            { label: "Sent to downloads", value: formatBytes(cycleCost.queuedBytes), help: "in the last 50 cycles" }
          ]}
        />
      ) : null}

      {view === "upgrades" ? (
        <ListCard title="Upgrade searches" count="When Deluno should keep looking for a better release">
          <div className="p-(--card-pad-x)">
            <SwitchRow
              label="Stop searching once the cutoff is met"
              description="Deluno keeps the title monitored, but stops looking for a better release after it reaches the quality level you chose."
              checked={qualityModel.upgradeStop.stopWhenCutoffMet}
              onCheckedChange={(checked) => setQualityModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, stopWhenCutoffMet: checked } }))}
            />
          </div>
        </ListCard>
      ) : null}

      {view !== "failed" ? <ListCard title={view === "missing" ? "Missing search schedules" : view === "upgrades" ? "Upgrade search schedules" : "Library schedules"} actions={<MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />} count={scheduleLibraries.length ? `${scheduleLibraries.length} ${scheduleLibraries.length === 1 ? "library" : "libraries"}` : undefined}>
        {scheduleLibraries.length === 0 ? (
          <ListEmpty title={libraries.length === 0 ? "No libraries yet" : view === "missing" ? "No libraries search for missing titles" : "No libraries search for upgrades"} description={libraries.length === 0 ? "Create a library first, then Deluno can search it on a schedule." : "Open a library's automation settings to turn this search on."} />
        ) : (
          <ListTable listId="search-cycles-libraries" columns={[{ label: "Library" }, { label: "Searches for" }, { label: "Schedule" }, { label: "Next / last" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]}>
            {split.groups.flatMap((group) => [
              split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
              ...group.items.map((library) => {
              const state = stateByLibrary.get(library.id);
              const chip = automationChip(library, state, paused);
              const kinds = [library.missingSearchEnabled ? "Missing" : null, library.upgradeSearchEnabled ? "Upgrades" : null].filter(Boolean).join(" · ") || "Nothing selected";
              const intervalLabel = formatSearchInterval(library.searchIntervalMinutes);
              const schedule = [
                library.missingSearchEnabled ? `Missing: ${intervalLabel}` : null,
                library.upgradeSearchEnabled ? `Upgrades: ${intervalLabel}` : null
              ].filter(Boolean).join(" · ") || "Off";
              const nextSearch = nextSearchLabel(view, library, state);
              return (
                <ListRow key={library.id} onClick={() => openLibrary(library)} selected={drawerId === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={kinds} secondary={`Up to ${library.maxMissingItemsPerRun ?? library.maxItemsPerRun} missing · ${library.maxUpgradeItemsPerRun ?? library.maxItemsPerRun} upgrades per run`} />
                  <ListCell primary={schedule} secondary={library.searchWindowStartHour !== null && library.searchWindowEndHour !== null ? `Only ${pad(library.searchWindowStartHour)}:00–${pad(library.searchWindowEndHour)}:00` : "Any time of day"} />
                  <ListCell numeric primary={nextSearch ?? <span className="text-muted-foreground">—</span>} secondary={state?.lastError ? state.lastError : state?.lastCompletedUtc ? `Last ran ${agoLabel(state.lastCompletedUtc)}` : "Not run yet"} />
                  <ListCell mobile>
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                  </ListCell>
                  <ListCell mobile>
                    <Switch size="sm" aria-label={`${library.autoSearchEnabled ? "Pause" : "Resume"} automation for ${library.name}`} checked={library.autoSearchEnabled} disabled={busy === `toggle:${library.id}`} onCheckedChange={(checked) => void toggleLibrary(library, checked)} />
                  </ListCell>
                </ListRow>
              );
            })
            ])}
          </ListTable>
        )}
      </ListCard> : null}

      {view === "overview" ? (
        <ListCard
          title="When a download finishes"
          count={sharing.mode === "share-then-tidy" ? "Shares, then tidies up" : sharing.mode === "tidy-now" ? "Tidies up straight away" : "Left alone"}
        >
          <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
            {/* The whole beginner decision is this one control. The dials below
                only exist once they can apply, so nobody configures a ratio for
                a mode that never waits. */}
            <Field
              label="What Deluno does with the original"
              help="Your library keeps its own copy either way. This is about the download client's copy, which may still be shared with other people."
            >
              <SegmentedControl
                aria-label="What Deluno does with the original"
                value={sharing.mode}
                onValueChange={(mode) => setSharing((current) => ({ ...current, mode }))}
                options={[
                  { value: "share-then-tidy", label: "Share, then tidy up" },
                  { value: "tidy-now", label: "Tidy up now" },
                  { value: "leave-alone", label: "Leave it alone" }
                ]}
              />
            </Field>

            {sharing.mode !== "leave-alone" ? (
              <Field
                label="Tracker privacy this covers"
                help="A private-tracker account can be permanently banned for early removal or a hit-and-run. Scope this policy away from private trackers to leave those alone entirely, or away from public ones."
              >
                <SegmentedControl
                  aria-label="Tracker privacy this covers"
                  value={sharing.privacyScope}
                  onValueChange={(privacyScope) => setSharing((current) => ({ ...current, privacyScope }))}
                  options={[
                    { value: "both", label: "Public and private" },
                    { value: "public", label: "Public only" },
                    { value: "private", label: "Private only" }
                  ]}
                />
              </Field>
            ) : null}

            {sharing.mode === "share-then-tidy" ? (
              <>
                <div className="grid gap-(--grid-gap) sm:grid-cols-2">
                  <Field label="Keep sharing for" help="Leave blank to ignore time and use the ratio alone.">
                    <PresetField
                      inputType="number"
                      value={sharing.forHours}
                      onChange={(value) => setSharing((current) => ({ ...current, forHours: value }))}
                      options={[
                        { value: "24", label: "1 day" },
                        { value: "72", label: "3 days" },
                        { value: "336", label: "14 days" }
                      ]}
                      customLabel="Custom"
                      customPlaceholder="Hours"
                    />
                  </Field>
                  <Field label="And until ratio" help="Leave blank to ignore ratio and use the time alone. Set both and Deluno waits for both.">
                    <PresetField
                      inputType="number"
                      value={sharing.untilRatio}
                      onChange={(value) => setSharing((current) => ({ ...current, untilRatio: value }))}
                      options={[
                        { value: "", label: "Not used" },
                        { value: "1", label: "1.0" },
                        { value: "2", label: "2.0" }
                      ]}
                      customLabel="Custom"
                      customPlaceholder="e.g. 1.5"
                    />
                  </Field>
                </div>

                <div className="grid gap-(--grid-gap) sm:grid-cols-2">
                  <Field label="If it can never get there" help="A download with nobody to share to will never reach a ratio.">
                    <SegmentedControl
                      aria-label="If it can never get there"
                      value={sharing.stuckAction}
                      onValueChange={(stuckAction) => setSharing((current) => ({ ...current, stuckAction }))}
                      options={[
                        { value: "give-up", label: "Give up" },
                        { value: "keep-waiting", label: "Keep waiting" },
                        { value: "ask", label: "Ask me" }
                      ]}
                    />
                  </Field>
                  {sharing.stuckAction === "give-up" ? (
                    <Field label="Give up after" help="Deluno tidies up and tells you it could not reach the target.">
                      <PresetField
                        inputType="number"
                        value={sharing.stuckAfterDays}
                        onChange={(value) => setSharing((current) => ({ ...current, stuckAfterDays: value }))}
                        options={[
                          { value: "7", label: "7 days" },
                          { value: "14", label: "14 days" },
                          { value: "30", label: "30 days" }
                        ]}
                        customLabel="Custom"
                        customPlaceholder="Days"
                      />
                    </Field>
                  ) : null}
                </div>

                <Field
                  label="Once the target above is reached"
                  help="Reclaiming removes the download from the client entirely. Stopping just ends seeding — the client keeps the entry and the files, and Deluno does not touch your library either way."
                >
                  <SegmentedControl
                    aria-label="Once the target above is reached"
                    value={sharing.completionAction}
                    onValueChange={(completionAction) => setSharing((current) => ({ ...current, completionAction }))}
                    options={[
                      { value: "reclaim", label: "Reclaim the space" },
                      { value: "stop", label: "Stop seeding without deleting" }
                    ]}
                  />
                </Field>
              </>
            ) : null}

            {sharing.mode === "tidy-now" ? (
              <p className="text-(length:--type-body-sm) text-muted-foreground">
                Deluno reclaims the space as soon as the import is verified. Some sites expect you to keep sharing and may penalise an account that does not.
              </p>
            ) : null}

            {sharing.mode === "leave-alone" ? (
              <p className="text-(length:--type-body-sm) text-muted-foreground">
                Deluno never removes anything from your download client. You decide what to keep and when to delete it.
              </p>
            ) : null}
          </div>
        </ListCard>
      ) : null}

      {view === "blocklist" ? (
        <BlockedReleasesConsole
          releases={blockedReleases}
          rules={failureRules}
          settings={settings}
          recycleBin={recycleBinSettings}
          onChanged={() => revalidator.revalidate()}
        />
      ) : null}

      {view === "failed" ? <ListCard title="Download health" count="What counts as stuck, before it is struck">
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            What counts as stuck, and what Deluno does about it, is set rule by rule on the Download Health tab: each rule has its own strike count and its own action, and all of them are checked on the one "Check every" interval there. A tracker the client itself says is unregistered feeds the same Dead torrent rule there, and is otherwise handled separately (Downloading: torrents) — removed and replaced as soon as it is seen, once removal is turned on.
          </p>
          <div>
            <Button asChild variant="outline" size="sm">
              <Link to="/search-cycles/download-health">Open Download Health</Link>
            </Button>
          </div>

          <div className="grid gap-(--grid-gap) border-t border-hairline pt-(--grid-gap) sm:grid-cols-[minmax(0,1fr)_14rem] sm:items-start">
            <SwitchRow
              label="Hourly search cap"
              description="Stop after this many total searches per hour, across every indexer combined — not a per-indexer limit. Off until a ceiling makes sense for your own indexers."
              checked={cleanup.maxSearchesPerHourEnabled}
              onCheckedChange={(checked) => setCleanup((current) => ({ ...current, maxSearchesPerHourEnabled: checked }))}
            />
            <Field label="Searches per hour" className="max-w-48 sm:pl-(--grid-gap)">
              <PresetField
                inputType="number"
                value={cleanup.maxSearchesPerHour}
                onChange={(value) => setCleanup((current) => ({ ...current, maxSearchesPerHour: value }))}
                disabled={!cleanup.maxSearchesPerHourEnabled}
                options={[
                  { value: "100", label: "100" },
                  { value: "200", label: "200" },
                  { value: "400", label: "400" }
                ]}
                customLabel="Custom"
                customPlaceholder="1–100000"
              />
            </Field>
          </div>

          <div className="grid gap-(--grid-gap) border-t border-hairline pt-(--grid-gap) sm:grid-cols-[minmax(0,1fr)_14rem] sm:items-start">
            <SwitchRow
              label="Pause automatic searching while downloads pile up"
              description="Skip the routine film and TV searches while more than this many of your downloads are still waiting, and start again by yourself when the queue shrinks. Deluno says so in Activity and on Health. Searches you start yourself always run."
              checked={cleanup.pauseSearchWhenQueueLongEnabled}
              onCheckedChange={(checked) => setCleanup((current) => ({ ...current, pauseSearchWhenQueueLongEnabled: checked }))}
            />
            <Field label="Pause above this many waiting" className="max-w-48 sm:pl-(--grid-gap)">
              <PresetField
                inputType="number"
                value={cleanup.pauseSearchWhenQueueLongThreshold}
                onChange={(value) => setCleanup((current) => ({ ...current, pauseSearchWhenQueueLongThreshold: value }))}
                disabled={!cleanup.pauseSearchWhenQueueLongEnabled}
                options={[
                  { value: "10", label: "10" },
                  { value: "20", label: "20" },
                  { value: "50", label: "50" }
                ]}
                customLabel="Custom"
                customPlaceholder="1–10000"
              />
            </Field>
          </div>

          <div className="grid gap-(--grid-gap) border-t border-hairline pt-(--grid-gap) sm:grid-cols-[minmax(0,1fr)_14rem] sm:items-start">
            <SwitchRow
              label="Download integrity check"
              description="Before a finished download reaches a processor or the library, open the file and check its resolution, runtime and playability against what its name and Deluno's own records claim. A file that fails is removed from its client, deleted, blocklisted and searched for again."
              checked={cleanup.downloadIntegrityCheckEnabled}
              onCheckedChange={(checked) => setCleanup((current) => ({ ...current, downloadIntegrityCheckEnabled: checked }))}
            />
            <Field label="Minimum runtime" help="A file running under this share of the known length is treated as a sample or a cut-off copy." className="max-w-48 sm:pl-(--grid-gap)">
              <PresetField
                inputType="number"
                value={cleanup.downloadIntegrityMinimumRuntimePercent}
                onChange={(value) => setCleanup((current) => ({ ...current, downloadIntegrityMinimumRuntimePercent: value }))}
                disabled={!cleanup.downloadIntegrityCheckEnabled}
                options={[
                  { value: "70", label: "70%" },
                  { value: "80", label: "80%" },
                  { value: "90", label: "90%" }
                ]}
                customLabel="Custom"
                customPlaceholder="1–100"
              />
            </Field>
          </div>
        </div>
      </ListCard> : null}

      {view === "failed" ? <ListCard title="Content blocklist" count="Checks the files inside a download, not just its name">
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <SwitchRow
            label="Check files inside downloads"
            description="Match every file actually inside a download against the patterns below, instead of only its release name."
            checked={contentBlocklist.enabled}
            onCheckedChange={(checked) => setContentBlocklist((current) => ({ ...current, enabled: checked }))}
          />
          <div className="grid gap-(--grid-gap) sm:grid-cols-2">
            <Field label="Mode" help="Blacklist blocks a matching file. Whitelist blocks everything except a matching file.">
              <SegmentedControl
                value={contentBlocklist.mode}
                onValueChange={(value) => setContentBlocklist((current) => ({ ...current, mode: value }))}
                options={[
                  { value: "blacklist", label: "Blacklist" },
                  { value: "whitelist", label: "Whitelist" }
                ]}
              />
            </Field>
            <SwitchRow
              label="Include Deluno's built-in patterns"
              description="Executable, script and shortcut extensions Deluno ships with, on top of anything entered below."
              checked={contentBlocklist.useBuiltInPatterns}
              onCheckedChange={(checked) => setContentBlocklist((current) => ({ ...current, useBuiltInPatterns: checked }))}
            />
          </div>
          <Field label="Custom patterns" help="One glob pattern per line, e.g. *.exe or autorun.inf.">
            <Textarea
              rows={4}
              value={contentBlocklist.customPatterns}
              onChange={(event) => setContentBlocklist((current) => ({ ...current, customPatterns: event.target.value }))}
              placeholder={"*.exe\n*.scr"}
            />
          </Field>
          <Field label="Custom remote list URL" help="Optional. Refetched periodically and combined with the patterns above.">
            <input
              type="url"
              className="h-9 w-full rounded-md border border-hairline bg-transparent px-(--space-sm) text-(length:--type-body-sm)"
              value={contentBlocklist.remoteUrl}
              onChange={(event) => setContentBlocklist((current) => ({ ...current, remoteUrl: event.target.value }))}
              placeholder="https://example.com/blocklist.txt"
            />
          </Field>
          <SwitchRow
            label="Push into qBittorrent's Excluded file names"
            description="Every hour, sets qBittorrent's own 'Excluded file names' preference to this list, only when it changed. Deluno never turns that qBittorrent setting on by itself — qBittorrent still needs its own 'Excluded file names' toggle on to actually skip a matching file."
            checked={contentBlocklist.syncToQbittorrentEnabled}
            onCheckedChange={(checked) => setContentBlocklist((current) => ({ ...current, syncToQbittorrentEnabled: checked }))}
          />
        </div>
      </ListCard> : null}

      {view === "failed" ? <ListCard title="When strikes run out" titleCase={false}>
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            Strike counts and per-kind conditions belong to each rule under Download Health, and the check interval is set there too. What happens once a rule's strikes run out, for anything still using this platform-wide default, is set below.
          </p>
          <div className="grid gap-(--grid-gap) sm:grid-cols-2">
            <SwitchRow label="Block this release" description="Refuse it on the blocklist, so searches pick another release. It can be lifted there." checked={cleanup.blockRelease} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, blockRelease: checked }))} />
            <SwitchRow label="Search for a replacement" description="Queue one bounded replacement search using the normal budget." checked={cleanup.queueReplacement} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, queueReplacement: checked }))} className="sm:border-l sm:border-hairline sm:pl-(--grid-gap)" />
            <SwitchRow label="Remove the client entry" description="Remove the failed item from its download client when the client supports it." checked={cleanup.removeClientEntry} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, removeClientEntry: checked }))} />
            <SwitchRow label="Purge residual files" description="Delete the failed payload, only inside paths Deluno can prove it owns." checked={cleanup.purgePayload} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, purgePayload: checked }))} className="sm:border-l sm:border-hairline sm:pl-(--grid-gap)" />
          </div>
        </div>
      </ListCard> : null}

      {view === "overview" ? <ListCard title="Recent cycles" count={cyclesShown.length ? runsLabel(cyclesShown.length, searchCycles.length) : undefined}>
        {searchCycles.length === 0 ? (
          <ListEmpty title="No search cycles yet" description="Once a library runs a missing or upgrade search, each cycle and what it queued shows up here." />
        ) : (
          <ListTable
            listId="search-cycles-recent"
            items={searchCycles}
            columns={[
              { id: "library", label: "Library", sort: (cycle) => cycle.libraryName.toLowerCase() },
              { id: "trigger", label: "Trigger", sort: (cycle) => triggerLabel(cycle.triggerKind) },
              { id: "result", label: "Result", sort: (cycle) => cycle.queuedCount },
              { id: "ran", label: "Ran", sort: (cycle) => new Date(cycle.startedUtc) },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (cycle) => toneSortValue(cycle.status === "completed" ? "ok" : cycle.status === "failed" ? "bad" : "info", cycle.status) }
            ]}
            chevron={false}
          >
            {(rows) => rows.slice(0, cyclesShown.length).map((cycle) => (
              <ListRow key={cycle.id}>
                <ListNameCell name={cycle.libraryName} sub={cycle.mediaType === "tv" ? "TV shows" : "Movies"} />
                <ListCell primary={triggerLabel(cycle.triggerKind)} />
                <ListCell numeric primary={`${cycle.queuedCount} queued`} secondary={`${cycle.plannedCount} planned · ${cycle.skippedCount} skipped`} />
                <ListCell numeric primary={agoLabel(cycle.startedUtc)} secondary={cycle.completedUtc ? `Took ${durationLabel(cycle.startedUtc, cycle.completedUtc)}` : "Still running"} />
                <ListCell mobile>
                  <Chip tone={cycle.status === "completed" ? "ok" : cycle.status === "failed" ? "bad" : "info"}>{cycle.status}</Chip>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard> : null}

      {/* Automation carries a saveable setting of its own now — what happens
          when a download finishes — so it needs the footer too. */}
      {view === "overview" || view === "failed" || view === "upgrades" ? <PageFooter state={cleanupFooter} message={cleanupMessage} saveLabel="Save automation settings" /> : null}

      <Drawer
        open={drawerId !== null}
        onOpenChange={(open) => {
          if (!open) setDrawerId(null);
        }}
        title={editing?.name ?? "Automation"}
        description={`Search schedule · ${editing?.mediaType === "tv" ? "TV shows" : "Movies"}`}
        onSubmit={submitDrawer}
        footer={<DrawerFooter state={drawerFooterState} message={drawerMessage} saveLabel="Save schedule" onCancel={() => setDrawerId(null)} disabled={busy !== null} />}
      >
        <DrawerSection title="What to search for">
          <div className="grid gap-(--grid-gap) sm:grid-cols-3">
            <SwitchRow label="Search automatically" description="Turn off to keep this library manual — you can still run a search from here." checked={form.autoSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, autoSearchEnabled: checked }))} />
            <SwitchRow label="Missing titles" description="Look for files this library does not have yet." checked={form.missingSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, missingSearchEnabled: checked }))} className="sm:border-l sm:border-hairline sm:pl-(--grid-gap)" />
            <SwitchRow label="Upgrades" description="Look for better releases for files already imported, until the profile cutoff." checked={form.upgradeSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeSearchEnabled: checked }))} className="sm:border-l sm:border-hairline sm:pl-(--grid-gap)" />
          </div>
        </DrawerSection>

        <DrawerSection title="Schedule">
          <FieldRow>
            <Field label="Search every" help="How often a cycle starts for this library.">
              <PresetField inputType="number" value={form.searchIntervalMinutes} onChange={(value) => setForm((current) => ({ ...current, searchIntervalMinutes: value }))} options={INTERVAL_OPTIONS} customLabel="Custom interval" customPlaceholder="Minutes" />
            </Field>
            <Field label="Retry after" help="Wait this long before retrying a title that found nothing.">
              <PresetField inputType="number" value={form.retryDelayHours} onChange={(value) => setForm((current) => ({ ...current, retryDelayHours: value }))} options={RETRY_OPTIONS} customLabel="Custom delay" customPlaceholder="Hours" />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Missing titles per run" help="Keeps a single cycle from flooding your indexers looking for missing titles.">
              <PresetField inputType="number" value={form.maxMissingItemsPerRun} onChange={(value) => setForm((current) => ({ ...current, maxMissingItemsPerRun: value }))} options={BATCH_OPTIONS} customLabel="Custom batch" customPlaceholder="Titles" />
            </Field>
            <Field label="Upgrade candidates per run" help="Set on its own, apart from missing titles, so tuning one never changes the other.">
              <PresetField inputType="number" value={form.maxUpgradeItemsPerRun} onChange={(value) => setForm((current) => ({ ...current, maxUpgradeItemsPerRun: value }))} options={BATCH_OPTIONS} customLabel="Custom batch" customPlaceholder="Titles" />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Only search after" optional help="Leave both empty to search at any hour.">
              <Select value={form.searchWindowStartHour} onChange={(event) => setForm((current) => ({ ...current, searchWindowStartHour: event.target.value }))} placeholder="Any time" options={HOUR_OPTIONS} />
            </Field>
            <Field label="And before" optional>
              <Select value={form.searchWindowEndHour} onChange={(event) => setForm((current) => ({ ...current, searchWindowEndHour: event.target.value }))} placeholder="Any time" options={HOUR_OPTIONS} />
            </Field>
          </FieldRow>
          <Field label="Only search on" help="Turn off a day to pause searches entirely on it, whatever the hour window above allows.">
            <div className="flex flex-wrap gap-1.5" role="group" aria-label="Days the search window applies on">
              {WEEKDAYS.map(({ bit, label }) => {
                const on = form.searchWindowDays[bit];
                return (
                  <Button
                    key={bit}
                    type="button"
                    variant={on ? "default" : "outline"}
                    size="sm"
                    aria-pressed={on}
                    aria-label={`${on ? "Searches on" : "Searches off"} ${DAY_NAMES[bit]}`}
                    onClick={() => setForm((current) => {
                      const next = [...current.searchWindowDays];
                      next[bit] = !next[bit];
                      return { ...current, searchWindowDays: next };
                    })}
                  >
                    {label}
                  </Button>
                );
              })}
            </div>
          </Field>
          <SwitchRow
            label="Search order"
            description={form.randomizeSearchOrder ? "Random — a new title never waits behind a long backlog." : "Oldest added first."}
            checked={form.randomizeSearchOrder}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, randomizeSearchOrder: checked }))}
          />
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Run now" aside={stateByLibrary.get(editing.id)?.nextSearchUtc ? `next ${untilLabel(stateByLibrary.get(editing.id)!.nextSearchUtc!)}` : undefined}>
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="sm" disabled={busy !== null || drawerDirty} onClick={() => void run(`now:${editing.id}`, async () => {
                const response = await authedFetch(`/api/libraries/${editing.id}/search-now`, { method: "POST" });
                if (!response.ok) throw new Error("Search could not be queued.");
              }, `Search queued for ${editing.name}`)}>
                {busy === `now:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Zap className="h-3.5 w-3.5" />}
                Search now
              </Button>
              <Button type="button" variant="outline" size="sm" disabled={busy !== null || drawerDirty} onClick={() => void run(`skip:${editing.id}`, async () => {
                const response = await authedFetch(`/api/libraries/${editing.id}/skip-cycle`, { method: "POST" });
                if (!response.ok) throw new Error("Cycle could not be skipped.");
              }, `Next cycle skipped for ${editing.name}`)}>
                {busy === `skip:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <SkipForward className="h-3.5 w-3.5" />}
                Skip next cycle
              </Button>
              {drawerDirty ? <span className="self-center text-(length:--type-caption) text-muted-foreground">Save your changes first.</span> : null}
            </div>
            {stateByLibrary.get(editing.id)?.lastError ? <p className="text-(length:--type-caption) text-destructive">{stateByLibrary.get(editing.id)!.lastError}</p> : null}
          </DrawerSection>
        ) : null}
      </Drawer>
    </form>
  );
}

/* ---------------------------------------------------------------- bits */

/** Shallow value equality for the small flat forms on this page. */
function same<T>(a: T, b: T) {
  return JSON.stringify(a) === JSON.stringify(b);
}

/** The card lists the latest 12, so say so rather than counting what is off-screen. */
function runsLabel(shown: number, total: number) {
  if (total > shown) return `Latest ${shown} of ${total} runs`;
  return total === 1 ? "1 run" : `${total} runs`;
}

function emptyAutomation(): AutomationForm {
  return { autoSearchEnabled: true, missingSearchEnabled: true, upgradeSearchEnabled: true, searchIntervalMinutes: "360", retryDelayHours: "6", maxItemsPerRun: "10", maxMissingItemsPerRun: "10", maxUpgradeItemsPerRun: "10", searchWindowStartHour: "", searchWindowEndHour: "", searchWindowDays: daysMaskToFlags(ALL_DAYS_MASK), randomizeSearchOrder: false };
}
function automationFrom(library: LibraryItem): AutomationForm {
  return {
    autoSearchEnabled: library.autoSearchEnabled,
    missingSearchEnabled: library.missingSearchEnabled,
    upgradeSearchEnabled: library.upgradeSearchEnabled,
    searchIntervalMinutes: String(library.searchIntervalMinutes),
    retryDelayHours: String(library.retryDelayHours),
    maxItemsPerRun: String(library.maxItemsPerRun),
    maxMissingItemsPerRun: String(library.maxMissingItemsPerRun ?? library.maxItemsPerRun),
    maxUpgradeItemsPerRun: String(library.maxUpgradeItemsPerRun ?? library.maxItemsPerRun),
    searchWindowStartHour: library.searchWindowStartHour === null ? "" : String(library.searchWindowStartHour),
    searchWindowEndHour: library.searchWindowEndHour === null ? "" : String(library.searchWindowEndHour),
    searchWindowDays: daysMaskToFlags(library.searchWindowDays),
    randomizeSearchOrder: library.randomizeSearchOrder ?? false
  };
}
function sharingFrom(settings: PlatformSettingsSnapshot): SharingForm {
  return {
    mode: settings.sharingMode ?? "share-then-tidy",
    // Empty means that half of the rule is not part of it, which is different
    // from it never having been set.
    forHours: settings.sharingForHours == null ? "" : String(settings.sharingForHours),
    untilRatio: settings.sharingUntilRatio == null ? "" : String(settings.sharingUntilRatio),
    stuckAction: settings.sharingStuckAction ?? "give-up",
    stuckAfterDays: String(settings.sharingStuckAfterDays ?? 14),
    privacyScope: settings.sharingPrivacyScope ?? "both",
    completionAction: settings.sharingCompletionAction ?? "reclaim"
  };
}

function cleanupFrom(settings: PlatformSettingsSnapshot): CleanupForm {
  return {
    strikeThreshold: String(settings.downloadHealthStrikeThreshold ?? 3),
    blockRelease: settings.cleanupBlockReleaseAfterThreshold,
    queueReplacement: settings.cleanupQueueReplacementAfterThreshold,
    removeClientEntry: settings.cleanupRemoveClientEntryAfterThreshold,
    purgePayload: settings.cleanupPurgePayloadAfterThreshold,
    noProgressEnabled: settings.downloadHealthNoProgressEnabled,
    noProgressMinutes: String(settings.downloadHealthNoProgressMinutes ?? 30),
    minSpeedEnabled: settings.downloadHealthMinSpeedEnabled,
    minSpeedKbps: String(settings.downloadHealthMinSpeedKbps ?? 50),
    minSpeedMinutes: String(settings.downloadHealthMinSpeedMinutes ?? 30),
    minPeersEnabled: settings.downloadHealthMinPeersEnabled,
    minPeers: String(settings.downloadHealthMinPeers ?? 1),
    minPeersMinutes: String(settings.downloadHealthMinPeersMinutes ?? 30),
    maxSearchesPerHourEnabled: settings.maxSearchesPerHourEnabled,
    maxSearchesPerHour: String(settings.maxSearchesPerHour ?? 200),
    pauseSearchWhenQueueLongEnabled: settings.pauseSearchWhenQueueLongEnabled ?? false,
    pauseSearchWhenQueueLongThreshold: String(settings.pauseSearchWhenQueueLongThreshold ?? 20),
    deadTorrentEnabled: settings.downloadHealthDeadTorrentEnabled,
    deadTorrentGraceMinutes: String(settings.downloadHealthDeadTorrentGraceMinutes ?? 60),
    downloadIntegrityCheckEnabled: settings.downloadIntegrityCheckEnabled,
    downloadIntegrityMinimumRuntimePercent: String(settings.downloadIntegrityMinimumRuntimePercent ?? 80),
    dangerousFileEnabled: settings.downloadHealthDangerousFileEnabled ?? false
  };
}
function nextSearchLabel(view: string, library: LibraryItem, state: LibraryAutomationStateItem | undefined): string | null {
  if (!state) return null;

  const missing = library.missingSearchEnabled && (state.nextMissingSearchUtc ?? state.nextSearchUtc)
    ? `Missing ${untilLabel(state.nextMissingSearchUtc ?? state.nextSearchUtc!)}`
    : null;
  const upgrades = library.upgradeSearchEnabled && (state.nextUpgradeSearchUtc ?? state.nextSearchUtc)
    ? `Upgrades ${untilLabel(state.nextUpgradeSearchUtc ?? state.nextSearchUtc!)}`
    : null;

  if (view === "missing") return missing;
  if (view === "upgrades") return upgrades;
  return [missing, upgrades].filter(Boolean).join(" · ") || null;
}
function automationChip(library: LibraryItem, state: LibraryAutomationStateItem | undefined, globallyPaused: boolean): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (!library.autoSearchEnabled) return { tone: "idle", label: "Manual" };
  if (globallyPaused) return { tone: "warn", label: "Paused" };
  if (state?.lastError) return { tone: "bad", label: "Last run failed" };
  if (state?.status === "running") return { tone: "info", label: "Searching" };
  if (state?.status === "queued") return { tone: "info", label: "Queued" };
  return { tone: "idle", label: "Scheduled" };
}
function parseNotes(notesJson: string | null): { apiCallCount: number; queuedReleaseBytes: number } {
  if (!notesJson) return { apiCallCount: 0, queuedReleaseBytes: 0 };
  try {
    const parsed = JSON.parse(notesJson) as { apiCallCount?: number; queuedReleaseBytes?: number };
    return { apiCallCount: Number(parsed.apiCallCount ?? 0), queuedReleaseBytes: Number(parsed.queuedReleaseBytes ?? 0) };
  } catch {
    return { apiCallCount: 0, queuedReleaseBytes: 0 };
  }
}
function pad(hour: number) {
  return String(hour).padStart(2, "0");
}
function untilLabel(iso: string) {
  const diff = new Date(iso).getTime() - Date.now();
  if (diff <= 0) return "Due now";
  const minutes = Math.round(diff / 60000);
  return minutes < 60 ? `in ${minutes} min` : minutes < 60 * 48 ? `in ${Math.round(minutes / 60)} h` : `in ${Math.round(minutes / 1440)} d`;
}
function agoLabel(iso: string) {
  const minutes = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`;
}
function durationLabel(startIso: string, endIso: string) {
  const seconds = Math.max(0, Math.round((new Date(endIso).getTime() - new Date(startIso).getTime()) / 1000));
  return seconds < 60 ? `${seconds}s` : `${Math.round(seconds / 60)}m`;
}
function triggerLabel(triggerKind: string) {
  switch (triggerKind) {
    case "manual":
      return "Manual";
    case "scheduled":
      return "Scheduled";
    case "upgrade":
      return "Upgrade sweep";
    default:
      return triggerKind;
  }
}
