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
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import { Loader2, Pause, Play, SkipForward, Zap } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { Drawer, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
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
import { useVisibleInterval } from "../hooks/use-visible-interval";
import {
  fetchJson, fetchPageItems,
  type LibraryAutomationStateItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityModelSnapshot,
  type SearchCycleRunItem
} from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";

const INTERVAL_OPTIONS = [
  { value: "1", label: "Every hour" },
  { value: "3", label: "Every 3 hours" },
  { value: "6", label: "Every 6 hours" },
  { value: "12", label: "Every 12 hours" },
  { value: "24", label: "Daily" }
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
}

export async function searchCyclesLoader(): Promise<LoaderData> {
  const [automationStates, libraries, qualityModel, settings, searchCycles] = await Promise.all([
    fetchPageItems<LibraryAutomationStateItem>("/api/library-automation?pageSize=50"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityModelSnapshot>("/api/quality-model"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchPageItems<SearchCycleRunItem>("/api/search-cycles?pageSize=50")
  ]);
  return { automationStates, libraries, qualityModel, settings, searchCycles };
}

interface AutomationForm {
  autoSearchEnabled: boolean;
  missingSearchEnabled: boolean;
  upgradeSearchEnabled: boolean;
  searchIntervalHours: string;
  retryDelayHours: string;
  maxItemsPerRun: string;
  searchWindowStartHour: string;
  searchWindowEndHour: string;
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
}

interface CleanupForm {
  strikeThreshold: string;
  blockRelease: boolean;
  queueReplacement: boolean;
  removeClientEntry: boolean;
  purgePayload: boolean;
}

export function SearchCyclesPage() {
  const { automationStates, libraries, qualityModel: loadedQualityModel, settings, searchCycles } = useLoaderData() as LoaderData;
  const location = useLocation();
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");

  useVisibleInterval(() => revalidator.revalidate(), 10_000);

  const view = location.pathname.endsWith("/missing") ? "missing" : location.pathname.endsWith("/upgrades") ? "upgrades" : location.pathname.endsWith("/failed-downloads") ? "failed" : "overview";
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
        searchIntervalHours: Number(form.searchIntervalHours || 12),
        retryDelayHours: Number(form.retryDelayHours || 6),
        maxItemsPerRun: Number(form.maxItemsPerRun || 10),
        searchWindowStartHour: form.searchWindowStartHour === "" ? null : Number(form.searchWindowStartHour),
        searchWindowEndHour: form.searchWindowEndHour === "" ? null : Number(form.searchWindowEndHour)
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
  const drawerFooterState: DrawerSaveState = drawerState === "saving" ? "saving" : drawerDirty ? "dirty" : drawerState ?? "clean";
  useEffect(() => {
    if (drawerDirty && (drawerState === "saved" || drawerState === "error")) setDrawerState(undefined);
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
  const [savedQualityModel, setSavedQualityModel] = useState(loadedQualityModel);
  const [qualityModel, setQualityModel] = useState(loadedQualityModel);
  const [cleanupState, setCleanupState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [cleanupMessage, setCleanupMessage] = useState<string | null>(null);

  const cleanupDirty = !same(cleanup, savedCleanup) || !same(sharing, savedSharing);
  const qualityDirty = !same(qualityModel, savedQualityModel);
  const automationDirty = cleanupDirty || qualityDirty;
  const settingsCleanup = useMemo(() => cleanupFrom(settings), [settings]);
  const settingsSharing = useMemo(() => sharingFrom(settings), [settings]);
  useEffect(() => {
    // Adopt server state only when the user has nothing unsaved in this form.
    if (cleanupDirty || same(savedCleanup, settingsCleanup)) return;
    setSavedCleanup(settingsCleanup);
    setCleanup(settingsCleanup);
    setSavedSharing(settingsSharing);
    setSharing(settingsSharing);
  }, [cleanupDirty, savedCleanup, settingsCleanup, settingsSharing]);

  const cleanupFooter: DrawerSaveState = cleanupState === "saving" ? "saving" : automationDirty ? "dirty" : cleanupState ?? "clean";
  useUnsavedChanges(automationDirty || drawerDirty);
  useEffect(() => {
    if (automationDirty && (cleanupState === "saved" || cleanupState === "error")) setCleanupState(undefined);
  }, [automationDirty, cleanupState]);

  async function submitCleanup(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (cleanupState === "saving") return;
    setCleanupState("saving");
    try {
      const [, nextQualityModel] = await Promise.all([
        settingsMutation.mutate({
            downloadHealthStrikeThreshold: Math.max(1, Math.min(20, Number(cleanup.strikeThreshold || 3))),
            cleanupBlockReleaseAfterThreshold: cleanup.blockRelease,
            cleanupQueueReplacementAfterThreshold: cleanup.queueReplacement,
            cleanupRemoveClientEntryAfterThreshold: cleanup.removeClientEntry,
            cleanupPurgePayloadAfterThreshold: cleanup.purgePayload,
            sharingMode: sharing.mode,
            // A blank target is sent as null, which the API stores as
            // "deliberately not part of this rule" rather than "never set".
            sharingForHours: sharing.forHours.trim() === "" ? null : Math.max(1, Number(sharing.forHours)),
            sharingUntilRatio: sharing.untilRatio.trim() === "" ? null : Math.max(0, Number(sharing.untilRatio)),
            sharingStuckAction: sharing.stuckAction,
            sharingStuckAfterDays: Math.max(1, Number(sharing.stuckAfterDays || 14))
        }),
        fetchJson<QualityModelSnapshot>("/api/quality-model", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ tiers: loadedQualityModel.tiers, upgradeStop: qualityModel.upgradeStop })
        })
      ]);
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
    <form onSubmit={submitCleanup} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar
        tabs={automationNavItems}
        accent="orange"
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
            <Button type="button" variant={paused ? "default" : "outline"} onClick={() => void toggleGlobal()} disabled={busy === "global"}>
            {busy === "global" ? <Loader2 className="h-4 w-4 animate-spin" /> : paused ? <Play className="h-4 w-4" /> : <Pause className="h-4 w-4" />}
              {paused ? "Resume automation" : "Pause automation"}
            </Button>
          </>
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
          <div className="p-[var(--card-pad-x)]">
            <SwitchRow
              label="Stop searching once the cutoff is met"
              description="Deluno keeps the title monitored, but stops looking for a better release after it reaches the quality level you chose."
              checked={qualityModel.upgradeStop.stopWhenCutoffMet}
              onCheckedChange={(checked) => setQualityModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, stopWhenCutoffMet: checked } }))}
            />
          </div>
        </ListCard>
      ) : null}

      {view !== "failed" ? <ListCard title={view === "missing" ? "Missing search schedules" : view === "upgrades" ? "Upgrade search schedules" : "Library schedules"} count={scheduleLibraries.length ? `${scheduleLibraries.length} ${scheduleLibraries.length === 1 ? "library" : "libraries"}` : undefined}>
        {scheduleLibraries.length === 0 ? (
          <ListEmpty title={libraries.length === 0 ? "No libraries yet" : view === "missing" ? "No libraries search for missing titles" : "No libraries search for upgrades"} description={libraries.length === 0 ? "Create a library first, then Deluno can search it on a schedule." : "Open a library's automation settings to turn this search on."} />
        ) : (
          <ListTable columns={[{ label: "Library" }, { label: "Searches for" }, { label: "Schedule" }, { label: "Next / last" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]}>
            {split.groups.flatMap((group) => [
              split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
              ...group.items.map((library) => {
              const state = stateByLibrary.get(library.id);
              const chip = automationChip(library, state, paused);
              const kinds = [library.missingSearchEnabled ? "Missing" : null, library.upgradeSearchEnabled ? "Upgrades" : null].filter(Boolean).join(" · ") || "Nothing selected";
              const schedule = [
                library.missingSearchEnabled ? `Missing every ${library.searchIntervalHours} h` : null,
                library.upgradeSearchEnabled ? `Upgrades every ${library.searchIntervalHours} h` : null
              ].filter(Boolean).join(" · ") || "Off";
              const nextSearch = nextSearchLabel(view, library, state);
              return (
                <ListRow key={library.id} onClick={() => openLibrary(library)} selected={drawerId === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={kinds} secondary={`Up to ${library.maxItemsPerRun} per run`} />
                  <ListCell primary={schedule} secondary={library.searchWindowStartHour !== null && library.searchWindowEndHour !== null ? `Only ${pad(library.searchWindowStartHour)}:00–${pad(library.searchWindowEndHour)}:00` : "Any time of day · each schedule runs independently"} />
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
          <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
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

            {sharing.mode === "share-then-tidy" ? (
              <>
                <div className="grid gap-[var(--grid-gap)] sm:grid-cols-2">
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

                <div className="grid gap-[var(--grid-gap)] sm:grid-cols-2">
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
              </>
            ) : null}

            {sharing.mode === "tidy-now" ? (
              <p className="text-[length:var(--type-body-sm)] text-muted-foreground">
                Deluno reclaims the space as soon as the import is verified. Some sites expect you to keep sharing and may penalise an account that does not.
              </p>
            ) : null}

            {sharing.mode === "leave-alone" ? (
              <p className="text-[length:var(--type-body-sm)] text-muted-foreground">
                Deluno never removes anything from your download client. You decide what to keep and when to delete it.
              </p>
            ) : null}
          </div>
        </ListCard>
      ) : null}

      {view === "failed" ? <ListCard title="Failed downloads" count={`After ${cleanup.strikeThreshold || 3} strikes on the same release`}>
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Act after this many strikes" help="A strike is one failed health check on the same release." className="max-w-[16rem]">
            <PresetField
              inputType="number"
              value={cleanup.strikeThreshold}
              onChange={(value) => setCleanup((current) => ({ ...current, strikeThreshold: value }))}
              options={[
                { value: "2", label: "2 strikes" },
                { value: "3", label: "3 strikes" },
                { value: "5", label: "5 strikes" }
              ]}
              customLabel="Custom"
              customPlaceholder="1–20"
            />
          </Field>
          <div className="grid gap-[var(--grid-gap)] sm:grid-cols-2">
            <SwitchRow label="Block this release" description="Do not select the same failed release again." checked={cleanup.blockRelease} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, blockRelease: checked }))} />
            <SwitchRow label="Search for a replacement" description="Queue one bounded replacement search using the normal budget." checked={cleanup.queueReplacement} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, queueReplacement: checked }))} className="sm:border-l sm:border-hairline sm:pl-[var(--grid-gap)]" />
            <SwitchRow label="Remove the client entry" description="Remove the failed item from its download client when the client supports it." checked={cleanup.removeClientEntry} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, removeClientEntry: checked }))} />
            <SwitchRow label="Purge residual files" description="Delete the failed payload, only inside paths Deluno can prove it owns." checked={cleanup.purgePayload} onCheckedChange={(checked) => setCleanup((current) => ({ ...current, purgePayload: checked }))} className="sm:border-l sm:border-hairline sm:pl-[var(--grid-gap)]" />
          </div>
        </div>
      </ListCard> : null}

      {view === "overview" ? <ListCard title="Recent cycles" count={cyclesShown.length ? runsLabel(cyclesShown.length, searchCycles.length) : undefined}>
        {searchCycles.length === 0 ? (
          <ListEmpty title="No search cycles yet" description="Once a library runs a missing or upgrade search, each cycle and what it queued shows up here." />
        ) : (
          <ListTable columns={[{ label: "Library" }, { label: "Trigger" }, { label: "Result" }, { label: "Ran" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]} chevron={false}>
            {cyclesShown.map((cycle) => (
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
          <div className="grid gap-[var(--grid-gap)] sm:grid-cols-3">
            <SwitchRow label="Search automatically" description="Turn off to keep this library manual — you can still run a search from here." checked={form.autoSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, autoSearchEnabled: checked }))} />
            <SwitchRow label="Missing titles" description="Look for files this library does not have yet." checked={form.missingSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, missingSearchEnabled: checked }))} className="sm:border-l sm:border-hairline sm:pl-[var(--grid-gap)]" />
            <SwitchRow label="Upgrades" description="Look for better releases for files already imported, until the profile cutoff." checked={form.upgradeSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeSearchEnabled: checked }))} className="sm:border-l sm:border-hairline sm:pl-[var(--grid-gap)]" />
          </div>
        </DrawerSection>

        <DrawerSection title="Schedule">
          <FieldRow>
            <Field label="Search every" help="How often a cycle starts for this library.">
              <PresetField inputType="number" value={form.searchIntervalHours} onChange={(value) => setForm((current) => ({ ...current, searchIntervalHours: value }))} options={INTERVAL_OPTIONS} customLabel="Custom interval" customPlaceholder="Hours" />
            </Field>
            <Field label="Retry after" help="Wait this long before retrying a title that found nothing.">
              <PresetField inputType="number" value={form.retryDelayHours} onChange={(value) => setForm((current) => ({ ...current, retryDelayHours: value }))} options={RETRY_OPTIONS} customLabel="Custom delay" customPlaceholder="Hours" />
            </Field>
          </FieldRow>
          <Field label="Titles per run" help="Keeps a single cycle from flooding your indexers.">
            <PresetField inputType="number" value={form.maxItemsPerRun} onChange={(value) => setForm((current) => ({ ...current, maxItemsPerRun: value }))} options={BATCH_OPTIONS} customLabel="Custom batch" customPlaceholder="Titles" />
          </Field>
          <FieldRow>
            <Field label="Only search after" optional help="Leave both empty to search at any hour.">
              <Select value={form.searchWindowStartHour} onChange={(event) => setForm((current) => ({ ...current, searchWindowStartHour: event.target.value }))} placeholder="Any time" options={HOUR_OPTIONS} />
            </Field>
            <Field label="And before" optional>
              <Select value={form.searchWindowEndHour} onChange={(event) => setForm((current) => ({ ...current, searchWindowEndHour: event.target.value }))} placeholder="Any time" options={HOUR_OPTIONS} />
            </Field>
          </FieldRow>
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
              {drawerDirty ? <span className="self-center text-[length:var(--type-caption)] text-muted-foreground">Save your changes first.</span> : null}
            </div>
            {stateByLibrary.get(editing.id)?.lastError ? <p className="text-[length:var(--type-caption)] text-destructive">{stateByLibrary.get(editing.id)!.lastError}</p> : null}
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
  return { autoSearchEnabled: true, missingSearchEnabled: true, upgradeSearchEnabled: true, searchIntervalHours: "12", retryDelayHours: "6", maxItemsPerRun: "10", searchWindowStartHour: "", searchWindowEndHour: "" };
}
function automationFrom(library: LibraryItem): AutomationForm {
  return {
    autoSearchEnabled: library.autoSearchEnabled,
    missingSearchEnabled: library.missingSearchEnabled,
    upgradeSearchEnabled: library.upgradeSearchEnabled,
    searchIntervalHours: String(library.searchIntervalHours),
    retryDelayHours: String(library.retryDelayHours),
    maxItemsPerRun: String(library.maxItemsPerRun),
    searchWindowStartHour: library.searchWindowStartHour === null ? "" : String(library.searchWindowStartHour),
    searchWindowEndHour: library.searchWindowEndHour === null ? "" : String(library.searchWindowEndHour)
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
    stuckAfterDays: String(settings.sharingStuckAfterDays ?? 14)
  };
}

function cleanupFrom(settings: PlatformSettingsSnapshot): CleanupForm {
  return {
    strikeThreshold: String(settings.downloadHealthStrikeThreshold ?? 3),
    blockRelease: settings.cleanupBlockReleaseAfterThreshold,
    queueReplacement: settings.cleanupQueueReplacementAfterThreshold,
    removeClientEntry: settings.cleanupRemoveClientEntryAfterThreshold,
    purgePayload: settings.cleanupPurgePayloadAfterThreshold
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
  if (!library.autoSearchEnabled) return { tone: "muted", label: "Manual" };
  if (globallyPaused) return { tone: "warn", label: "Paused" };
  if (state?.lastError) return { tone: "bad", label: "Last run failed" };
  if (state?.status === "running") return { tone: "info", label: "Searching" };
  if (state?.status === "queued") return { tone: "info", label: "Queued" };
  return { tone: "muted", label: "Scheduled" };
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
function formatBytes(bytes: number) {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
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
