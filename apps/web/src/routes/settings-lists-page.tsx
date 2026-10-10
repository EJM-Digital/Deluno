/**
 * Import Lists — list → drawer.
 *
 *   PageToolbar (New list)
 *   ListCard  (name · list · adds to · last sync · status · on · ›)
 *   Drawer    (Basics · Filters [Fine-tune] · Behaviour · Preview & sync · Remove)
 *
 * Contracts: GET/POST /api/intake-sources, PUT/DELETE /api/intake-sources/{id},
 * POST …/sync, …/preview, …/approve-preview, …/exclude-preview,
 * DELETE …/exclusions/{exclusionId}.
 */
import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { toneSortValue } from "../lib/status-tones";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { Eye, LoaderCircle, Plus, RefreshCcw } from "lucide-react";
import { TagPicker } from "../components/app/tag-picker";
import { Button } from "../components/ui/button";
import { Checkbox } from "../components/ui/checkbox";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import {
  fetchJson,
  type IntakeListApprovalResult,
  type IntakeListPreviewItem,
  type IntakeListPreviewResult,
  type IntakeSourceItem,
  type LibraryItem,
  type MediaExclusionItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem,
  type TraktConnectionStatus
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";
import { formatDateTime, formatShortDate, type DisplayPreferences, useDisplayPreferences } from "../lib/display-preferences";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

const PROVIDERS = [
  { label: "Custom list URL", value: "url-list" },
  { label: "Trakt", value: "trakt" },
  { label: "IMDb", value: "imdb" },
  { label: "TMDb", value: "tmdb" },
  { label: "TMDb person", value: "tmdb-person" },
  { label: "TMDb Popular", value: "tmdb-popular" },
  { label: "TMDb Discover/Keywords", value: "tmdb-discover" },
  { label: "TMDb Collection", value: "tmdb-collection" },
  { label: "TMDb Company Credits", value: "tmdb-company-credits" },
  { label: "Letterboxd", value: "letterboxd" },
  { label: "RSS feed", value: "rss" },
  { label: "StevenLu", value: "stevenlu" },
  { label: "Trakt Trending (connected)", value: "trakt-trending" },
  { label: "Trakt Popular (connected)", value: "trakt-popular" },
  { label: "Trakt Anticipated (connected)", value: "trakt-anticipated" },
  { label: "Trakt Top Watched (connected)", value: "trakt-watched" },
  { label: "Trakt Recommended (connected)", value: "trakt-recommended" }
];
const TMDB_PERSON_CREDIT_TYPES = [
  { value: "cast", label: "Cast", description: "Films and shows where this person acts." },
  { value: "director", label: "Director", description: "Films and shows this person directs." },
  { value: "producer", label: "Producer", description: "Films and shows this person produces." },
  { value: "sound", label: "Sound", description: "Films and shows this person is credited on for sound." },
  { value: "writing", label: "Writing", description: "Films and shows this person writes." }
] as const;
type TmdbPersonCreditType = typeof TMDB_PERSON_CREDIT_TYPES[number]["value"];
const SYNC_OPTIONS = [
  { label: "Every 6 hours", value: "6" },
  { label: "Every 12 hours", value: "12" },
  { label: "Daily", value: "24" },
  { label: "Every 3 days", value: "72" },
  { label: "Weekly", value: "168" },
  { label: "Fortnightly", value: "336" },
  { label: "Monthly", value: "720" }
];

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
  intakeSources: IntakeSourceItem[];
  exclusions: MediaExclusionItem[];
  traktStatus: TraktConnectionStatus;
}

interface ListForm {
  name: string;
  provider: string;
  feedUrl: string;
  mediaType: "movies" | "tv";
  libraryId: string;
  qualityProfileId: string;
  requiredGenres: string;
  minimumRating: string;
  minimumYear: string;
  maximumAgeDays: string;
  allowedCertifications: string;
  audience: string;
  syncIntervalHours: string;
  searchOnAdd: boolean;
  isEnabled: boolean;
  personCreditTypes: TmdbPersonCreditType[];
  /** Minimum release stage (movies only) a list-added title must reach before Deluno searches for it (#849). */
  minimumAvailability: string;
  /** How much of a newly added title to actually watch for (#848): All/Future/Missing/Existing/Recent for TV, Movie/None for movies. */
  monitorMode: string;
  /** TV only (#850): Standard/Daily/Anime, mirroring the same choice on a direct series add. */
  seriesType: string;
  /** TV only (#850): forces a season subfolder on or off, or "" to follow the global Media Management format. */
  seasonFolder: "" | "true" | "false";
  /** #1015: tags every title this list adds gets, comma separated. */
  tags: string;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsListsLoader(): Promise<LoaderData> {
  const overview = await settingsOverviewLoader();
  const [exclusions, traktStatus] = await Promise.all([
    fetchJson<MediaExclusionItem[]>("/api/exclusions"),
    fetchJson<TraktConnectionStatus>("/api/trakt/status")
  ]);
  return { ...overview, exclusions, traktStatus };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["IntakeSourceChanged", "LibraryChanged", "SettingsChanged"] };

export function SettingsListsPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { intakeSources, libraries, exclusions, traktStatus, settings } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const { preferences } = useDisplayPreferences();

  /* ------------------------------------------------------ Trakt (#846) */
  const [traktBusy, setTraktBusy] = useState(false);
  const [traktCredentials, setTraktCredentials] = useState({ clientId: "", clientSecret: "" });
  const [traktDevice, setTraktDevice] = useState<{ deviceCode: string; userCode: string; verificationUrl: string } | null>(null);

  async function saveTraktCredentials() {
    if (!traktCredentials.clientId.trim() || !traktCredentials.clientSecret.trim()) {
      toast.error("Enter both the Client ID and Client Secret from your Trakt app.");
      return;
    }
    setTraktBusy(true);
    try {
      const response = await authedFetch("/api/trakt/credentials", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(traktCredentials)
      });
      if (!response.ok) throw new Error("Could not save the Trakt app credentials.");
      toast.success("Trakt app credentials saved");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not save the Trakt app credentials.");
    } finally {
      setTraktBusy(false);
    }
  }

  async function startTraktConnect() {
    setTraktBusy(true);
    try {
      const response = await authedFetch("/api/trakt/connect", { method: "POST" });
      if (!response.ok) throw new Error("Could not start connecting Trakt.");
      const data = await response.json();
      setTraktDevice({ deviceCode: data.deviceCode, userCode: data.userCode, verificationUrl: data.verificationUrl });
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not start connecting Trakt.");
    } finally {
      setTraktBusy(false);
    }
  }

  async function checkTraktConnect() {
    if (!traktDevice) return;
    setTraktBusy(true);
    try {
      const response = await authedFetch("/api/trakt/connect/poll", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceCode: traktDevice.deviceCode })
      });
      if (!response.ok) throw new Error("Could not check Trakt.");
      const data = await response.json();
      if (data.status === "connected") {
        setTraktDevice(null);
        toast.success("Trakt connected");
        revalidator.revalidate();
      } else if (data.status === "expired" || data.status === "denied") {
        setTraktDevice(null);
        toast.error(data.status === "expired" ? "That code expired — start again." : "Trakt declined the request — start again.");
      } else {
        toast.error(`Not approved yet — open ${traktDevice.verificationUrl} and enter ${traktDevice.userCode}.`);
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not check Trakt.");
    } finally {
      setTraktBusy(false);
    }
  }

  async function disconnectTrakt() {
    setTraktBusy(true);
    try {
      const response = await authedFetch("/api/trakt/disconnect", { method: "POST" });
      if (!response.ok) throw new Error("Could not disconnect Trakt.");
      toast.success("Trakt disconnected");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not disconnect Trakt.");
    } finally {
      setTraktBusy(false);
    }
  }

  const [togglingId, setTogglingId] = useState<string | null>(null);
  const [restoringExclusionId, setRestoringExclusionId] = useState<string | null>(null);
  const sorted = useMemo(() => [...intakeSources].sort((a, b) => a.name.localeCompare(b.name)), [intakeSources]);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<ListForm>(() => emptyForm(libraries));
  const [initialForm, setInitialForm] = useState<ListForm>(() => emptyForm(libraries));
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<{ name?: string; feedUrl?: string; creditTypes?: string }>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [preview, setPreview] = useState<IntakeListPreviewResult | null>(null);
  const [previewNote, setPreviewNote] = useState<string | null>(null);
  const [selectedKeys, setSelectedKeys] = useState<string[]>([]);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? intakeSources.find((item) => item.id === mode.id) ?? null : null;
  const dirty = useMemo(() => isOpen && !sameForm(form, initialForm), [isOpen, form, initialForm]);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const matchingLibraries = useMemo(() => libraries.filter((library) => library.mediaType === form.mediaType), [libraries, form.mediaType]);

  function openCreate() {
    const next = emptyForm(libraries);
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    resetChrome();
  }
  function openEdit(item: IntakeSourceItem) {
    const next = formFrom(item);
    setMode({ kind: "edit", id: item.id });
    setForm(next);
    setInitialForm(next);
    resetChrome();
    setFiltersOpen(hasFilters(next));
  }
  function resetChrome() {
    setFiltersOpen(false);
    setSaveState(undefined);
    setErrors({});
    setPreview(null);
    setPreviewNote(null);
    setSelectedKeys([]);
  }
  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  /* ---------------------------------------------------------- saving */
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    const nextErrors: typeof errors = {};
    if (!form.feedUrl.trim() && form.provider !== "tmdb-popular") nextErrors.feedUrl = "Paste the list URL or identifier.";
    if (form.provider === "tmdb-person" && form.personCreditTypes.length === 0) {
      nextErrors.creditTypes = "Choose at least one credit type to follow.";
    }
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) return;

    setBusy("save");
    setSaveState("saving");
    try {
      const payload = toPayload(form);
      let saved: IntakeSourceItem;
      if (mode.kind === "create") {
        const response = await authedFetch("/api/intake-sources", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
        if (!response.ok) throw new Error(await readIntakeSourceError(response, "Import list could not be added."));
        saved = (await response.json()) as IntakeSourceItem;
        setMode({ kind: "edit", id: saved.id });
        setSaveMessage("List added");
      } else {
        const response = await authedFetch(`/api/intake-sources/${mode.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
        if (!response.ok) throw new Error(await readIntakeSourceError(response, "Import list could not be saved."));
        saved = (await response.json().catch(() => null)) ?? { ...(editing as IntakeSourceItem), ...payload };
        setSaveMessage("Saved just now");
      }
      const settled = formFrom(saved);
      setForm((current) => settleFormAfterSave(current, form, settled));
      setInitialForm(settled);
      setSaveState("saved");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(null);
    }
  }

  async function run(key: string, action: () => Promise<unknown>, success?: string) {
    setBusy(key);
    try {
      await action();
      if (success) toast.success(success);
      return true;
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Action failed.");
      return false;
    } finally {
      setBusy(null);
    }
  }

  async function handleRemove() {
    if (mode.kind !== "edit") return;
    const id = mode.id;
    const ok = await run("remove", async () => {
      const response = await authedFetch(`/api/intake-sources/${id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Import list could not be removed.");
    }, `${editing?.name ?? "List"} removed`);
    if (!ok) return;
    setConfirmRemove(false);
    setInitialForm(form);
    closeDrawer();
    revalidator.revalidate();
  }

  async function toggleEnabled(item: IntakeSourceItem, isEnabled: boolean) {
    setTogglingId(item.id);
    try {
      const response = await authedFetch(`/api/intake-sources/${item.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(toPayload({ ...formFrom(item), isEnabled })) });
      if (!response.ok) throw new Error(await readIntakeSourceError(response, `Could not ${isEnabled ? "enable" : "pause"} ${item.name}.`));
      if (mode.kind === "edit" && mode.id === item.id && !dirty) {
        const next = { ...form, isEnabled };
        setForm(next);
        setInitialForm(next);
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Import list could not be updated.");
    } finally {
      setTogglingId(null);
    }
  }

  async function syncNow() {
    if (mode.kind !== "edit") return;
    const id = mode.id;
    const ok = await run("sync", async () => {
      const response = await authedFetch(`/api/intake-sources/${id}/sync`, { method: "POST" });
      if (!response.ok) throw new Error("Sync could not be queued.");
    }, "Sync queued — results appear under Last sync");
    if (ok) revalidator.revalidate();
  }

  async function loadPreview() {
    if (mode.kind !== "edit") return;
    const id = mode.id;
    setPreviewNote(null);
    await run("preview", async () => {
      const response = await authedFetch(`/api/intake-sources/${id}/preview`, { method: "POST" });
      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as { message?: string } | null;
        throw new Error(body?.message ?? "Preview could not be loaded.");
      }
      const result = (await response.json()) as IntakeListPreviewResult;
      setPreview(result);
      setSelectedKeys(result.items.filter((item) => item.action === "would add").map(previewEntryKey));
      setPreviewNote("Preview ready. Nothing was added or searched.");
    });
  }

  async function approvePreview(searchAfterAdd: boolean) {
    if (mode.kind !== "edit" || !preview) return;
    const id = mode.id;
    const keys = new Set(selectedKeys);
    const entries = preview.items.filter((item) => keys.has(previewEntryKey(item)) && item.action === "would add").map((item) => ({ title: item.title, year: item.year, imdbId: item.imdbId }));
    if (!entries.length) {
      setPreviewNote("Choose at least one eligible entry first.");
      return;
    }
    await run(`approve:${searchAfterAdd ? "search" : "add"}`, async () => {
      const response = await authedFetch(`/api/intake-sources/${id}/approve-preview`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ entries, searchAfterAdd }) });
      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as { message?: string } | null;
        throw new Error(body?.message ?? "Selected entries could not be added.");
      }
      const result = (await response.json()) as IntakeListApprovalResult;
      setPreviewNote(`${result.addedCount} title${result.addedCount === 1 ? "" : "s"} added from ${result.selectedCount} approved preview entr${result.selectedCount === 1 ? "y" : "ies"}.${result.searchRequested ? " Deluno will search them using normal automation rules." : ""}`);
      setPreview(null);
      revalidator.revalidate();
    });
  }

  async function excludeEntry(entry: IntakeListPreviewItem, durationDays: number | null) {
    if (mode.kind !== "edit" || !preview) return;
    const id = mode.id;
    await run(`exclude:${previewEntryKey(entry)}`, async () => {
      const response = await authedFetch(`/api/intake-sources/${id}/exclude-preview`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title: entry.title, year: entry.year, imdbId: entry.imdbId, durationDays, reason: "Excluded during import list preview" }) });
      if (!response.ok) throw new Error("This entry could not be excluded.");
      const exclusion = (await response.json()) as { id: string };
      setPreview((current) =>
        current
          ? { ...current, items: current.items.map((item) => (previewEntryKey(item) === previewEntryKey(entry) ? { ...item, action: "excluded", reason: durationDays ? `Ignored for ${durationDays} days.` : "Excluded from this list.", exclusionId: exclusion.id } : item)) }
          : current
      );
      setSelectedKeys((current) => current.filter((key) => key !== previewEntryKey(entry)));
      setPreviewNote(durationDays ? `${entry.title} will be ignored for ${durationDays} days.` : `${entry.title} will not be added from this list again.`);
    });
  }

  async function restoreEntry(entry: IntakeListPreviewItem) {
    if (mode.kind !== "edit" || !entry.exclusionId) return;
    const id = mode.id;
    await run(`restore:${entry.exclusionId}`, async () => {
      const response = await authedFetch(`/api/intake-sources/${id}/exclusions/${entry.exclusionId}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("This entry could not be restored.");
      setPreview((current) =>
        current
          ? { ...current, items: current.items.map((item) => (previewEntryKey(item) === previewEntryKey(entry) ? { ...item, action: "would add", reason: "Eligible again. Choose it when you are ready to add it.", exclusionId: null } : item)) }
          : current
      );
      setPreviewNote(`${entry.title} is eligible for this list again.`);
    });
  }

  async function restoreExclusion(exclusion: MediaExclusionItem) {
    setRestoringExclusionId(exclusion.id);
    try {
      const response = await authedFetch(`/api/exclusions/${exclusion.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("This exclusion could not be restored.");
      toast.success(`${exclusion.title} can be added again.`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "This exclusion could not be restored.");
    } finally {
      setRestoringExclusionId(null);
    }
  }

  /* ---------------------------------------------------------- render */
  const providerLabel = (value: string) => PROVIDERS.find((provider) => provider.value === value)?.label ?? value;

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        actions={
          <PageToolbarAction onClick={openCreate}>New list</PageToolbarAction>
        }
      />

      <ListCard
        title="Trakt"
        count={traktStatus.connected ? (traktStatus.needsReconnect ? "Needs reconnect" : "Connected") : "Not connected"}
      >
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <p className="text-(length:--type-caption) text-muted-foreground">
            Connect a Trakt account to reach private lists and Trakt&rsquo;s own Trending, Popular, Anticipated, Top Watched and Recommended lists — none of which have a public RSS feed.
          </p>
          {!traktStatus.clientConfigured ? (
            <FieldRow>
              <Field label="Client ID">
                <Input value={traktCredentials.clientId} onChange={(event) => setTraktCredentials((current) => ({ ...current, clientId: event.target.value }))} autoComplete="off" spellCheck={false} />
              </Field>
              <Field label="Client Secret">
                <Input value={traktCredentials.clientSecret} onChange={(event) => setTraktCredentials((current) => ({ ...current, clientSecret: event.target.value }))} type="password" autoComplete="off" spellCheck={false} />
              </Field>
            </FieldRow>
          ) : null}
          <div className="flex flex-wrap items-center gap-2">
            {!traktStatus.clientConfigured ? (
              <Button type="button" size="sm" disabled={traktBusy} onClick={() => void saveTraktCredentials()}>
                Save Trakt app credentials
              </Button>
            ) : traktDevice ? (
              <>
                <span className="text-(length:--type-caption)">
                  Open <a className="underline" href={traktDevice.verificationUrl} target="_blank" rel="noreferrer">{traktDevice.verificationUrl}</a> and enter code <strong className="font-mono">{traktDevice.userCode}</strong>
                </span>
                <Button type="button" size="sm" variant="outline" disabled={traktBusy} onClick={() => void checkTraktConnect()}>
                  I&rsquo;ve approved it — check now
                </Button>
              </>
            ) : traktStatus.connected ? (
              <>
                {traktStatus.needsReconnect ? (
                  <Button type="button" size="sm" disabled={traktBusy} onClick={() => void startTraktConnect()}>
                    Reconnect Trakt
                  </Button>
                ) : null}
                <Button type="button" size="sm" variant="outline" disabled={traktBusy} onClick={() => void disconnectTrakt()}>
                  Disconnect
                </Button>
              </>
            ) : (
              <Button type="button" size="sm" disabled={traktBusy} onClick={() => void startTraktConnect()}>
                Connect Trakt
              </Button>
            )}
          </div>
          {traktStatus.needsReconnect && !traktDevice ? (
            <p className="text-(length:--type-caption) text-destructive">Trakt stopped renewing its own connection. Reconnect to keep Trakt lists working.</p>
          ) : null}
        </div>
      </ListCard>

      <ListCard title={intakeSources.length ? `${intakeSources.length} ${intakeSources.length === 1 ? "list" : "lists"} · ${intakeSources.filter((item) => item.isEnabled).length} enabled` : "0 lists"} titleCase={false}>
        {intakeSources.length === 0 ? (
          <ListEmpty
            title="No import lists yet"
            description="Follow a watchlist, a curated list or an RSS feed. Deluno checks it on a schedule, adds only matching titles, and can start a search when they arrive."
            actions={
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus className="h-3.5 w-3.5" />
                New list
              </Button>
            }
          />
        ) : (
          <ListTable
            listId="import-lists"
            items={sorted}
            columns={[
              { id: "name", label: "Name", sort: (item) => item.name.toLowerCase() },
              { id: "list", label: "List", sort: (item) => item.feedUrl.toLowerCase() },
              { id: "addsTo", label: "Adds to", sort: (item) => (item.libraryName ?? "").toLowerCase() },
              { id: "lastSync", label: "Last sync", sort: (item) => (item.lastSyncUtc ? new Date(item.lastSyncUtc) : null) },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (item) => { const chip = syncChip(item); return toneSortValue(chip.tone, chip.label); } },
              { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (item) => item.isEnabled }
            ]}
          >
            {(rows) => rows.map((item) => {
              const chip = syncChip(item);
              return (
                <ListRow key={item.id} onClick={() => openEdit(item)} selected={mode.kind === "edit" && mode.id === item.id}>
                  <ListNameCell name={item.name} sub={`${providerLabel(item.provider)} · ${item.mediaType === "tv" ? "TV" : "Movies"}`} />
                  <ListCell mono primary={item.feedUrl} secondary={item.searchOnAdd ? "Adds and searches" : "Adds only"} />
                  <ListCell primary={item.libraryName ?? <span className="text-muted-foreground">No library</span>} secondary={item.libraryName ? `Every ${item.syncIntervalHours} h` : "Choose a library before syncing"} />
                  <ListCell numeric primary={item.lastSyncUtc ? relative(item.lastSyncUtc, preferences) : <span className="text-muted-foreground">Never</span>} secondary={item.lastSyncSummary ?? (item.lastSyncStatus === "never" ? "Not synced yet" : item.lastSyncStatus)} />
                  <ListCell mobile>
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                  </ListCell>
                  <ListCell mobile>
                    <Switch size="sm" aria-label={`${item.isEnabled ? "Pause" : "Enable"} ${item.name}`} checked={item.isEnabled} disabled={togglingId === item.id} onCheckedChange={(checked) => void toggleEnabled(item, checked)} />
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <OffListTagCard
        key={`${settings.tagTitlesOffEveryList}:${settings.offListTag}`}
        enabled={settings.tagTitlesOffEveryList ?? false}
        tag={settings.offListTag ?? ""}
        onSaved={() => revalidator.revalidate()}
      />

      <ListCard
        title="Never add decisions"
        count={exclusions.length ? `${exclusions.length} active ${exclusions.length === 1 ? "decision" : "decisions"} · all automated sources` : undefined}
      >
        {exclusions.length === 0 ? (
          <ListEmpty
            title="No exclusions"
            description="When you exclude an entry during preview, it appears here so you can review or allow it again later."
          />
        ) : (
          <ListTable
            listId="list-exclusions"
            items={[...exclusions].sort((left, right) => new Date(right.createdUtc).getTime() - new Date(left.createdUtc).getTime())}
            columns={[
              { id: "entry", label: "Entry", sort: (exclusion) => exclusion.title.toLowerCase() },
              { id: "source", label: "Source", sort: (exclusion) => (exclusion.sourceName || (intakeSources.find((source) => source.id === exclusion.sourceId)?.name ?? exclusion.sourceKind)).toLowerCase() },
              { id: "decision", label: "Decision", sort: (exclusion) => (exclusion.expiresUtc ? new Date(exclusion.expiresUtc) : null) },
              { id: "created", label: "Created", sort: (exclusion) => new Date(exclusion.createdUtc) },
              { id: "action", label: "Action", width: "120px", mobile: true }
            ]}
          >
            {(rows) => rows
              .map((exclusion) => (
                <ListRow key={exclusion.id}>
                  <ListNameCell name={exclusion.title} sub={exclusion.year ? String(exclusion.year) : exclusion.imdbId ?? "No year or IMDb id"} />
                  <ListCell primary={exclusion.sourceName || (intakeSources.find((source) => source.id === exclusion.sourceId)?.name ?? exclusion.sourceKind)} secondary={exclusion.sourceKind === "collection" ? "Collection" : exclusion.sourceKind === "retention" ? "Retention rule" : "Import list"} />
                  <ListCell
                    primary={exclusion.expiresUtc ? `Ignored until ${formatShortDate(exclusion.expiresUtc, { ...preferences, showRelativeDates: false })}` : "Excluded permanently"}
                    secondary={exclusion.reason || "Excluded from import list by user"}
                  />
                  <ListCell numeric primary={formatDateTime(exclusion.createdUtc, preferences)} />
                  <ListCell mobile align="end">
                    <Button type="button" size="sm" variant="outline" disabled={restoringExclusionId === exclusion.id} onClick={() => void restoreExclusion(exclusion)}>
                      {restoringExclusionId === exclusion.id ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                      Allow again
                    </Button>
                  </ListCell>
                </ListRow>
              ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={isOpen}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={mode.kind === "create" ? "New import list" : editing?.name ?? form.name}
        description={mode.kind === "create" ? "A watchlist, curated list or feed Deluno should follow." : `${providerLabel(form.provider)} · ${form.mediaType === "tv" ? "TV" : "Movies"} · ${editing?.libraryName ? `adds to ${editing.libraryName}` : "no library yet"}`}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Add list" : "Save list"} onCancel={requestClose} saveEnabled={mode.kind === "create" ? true : undefined} disabled={busy !== null} />}
      >
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Provider">
              <Select value={form.provider} onChange={(event) => setForm((current) => ({ ...current, provider: event.target.value }))} options={PROVIDERS} />
            </Field>
          </FieldRow>
          <Field label={form.provider === "tmdb-person" ? "Person" : "List URL"} help={listAddressHelp(form.provider)} error={errors.feedUrl}>
            <Input value={form.feedUrl} onChange={(event) => { setErrors((current) => ({ ...current, feedUrl: undefined })); setForm((current) => ({ ...current, feedUrl: event.target.value })); }} placeholder={form.provider === "tmdb-person" ? "https://www.themoviedb.org/person/…" : "https://…"} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
          </Field>
          <FieldRow>
            <Field label="Media type">
              <SegmentedControl<"movies" | "tv">
                value={form.mediaType}
                onValueChange={(mediaType) => setForm((current) => ({
                  ...current,
                  mediaType,
                  libraryId: libraries.find((library) => library.mediaType === mediaType)?.id ?? "",
                  monitorMode: mediaType === "tv" ? "all" : "movie"
                }))}
                options={[
                  { value: "movies", label: "Movies" },
                  { value: "tv", label: "TV shows" }
                ]}
              />
            </Field>
            <Field label="Adds to" help={matchingLibraries.length ? "Titles from this list are added to this library." : `No ${form.mediaType === "tv" ? "TV" : "movie"} libraries yet — sync adds nothing until one is chosen.`}>
              <Select value={form.libraryId} onChange={(event) => setForm((current) => ({ ...current, libraryId: event.target.value }))} placeholder="No library" options={matchingLibraries.map((library) => ({ value: library.id, label: library.name }))} />
            </Field>
          </FieldRow>
        </DrawerSection>

        {form.provider === "tmdb-person" ? (
          <DrawerSection title="Credits" aside={form.personCreditTypes.length ? `${form.personCreditTypes.length} selected` : "choose at least one"}>
            <p className="text-(length:--type-caption) text-muted-foreground">Follow titles where this person has one or more of these credits.</p>
            <div className="grid gap-2">
              {TMDB_PERSON_CREDIT_TYPES.map((credit) => {
                const checked = form.personCreditTypes.includes(credit.value);
                const id = `tmdb-person-${credit.value}`;
                return (
                  <label key={credit.value} htmlFor={id} className={cn("flex cursor-pointer items-start gap-3 rounded-[10px] border px-(--field-pad-x) py-2.5 transition", checked ? "border-primary/30 bg-primary/6" : "border-hairline bg-card")}>
                    <Checkbox
                      id={id}
                      checked={checked}
                      onCheckedChange={(nextChecked) => {
                        setForm((current) => ({
                          ...current,
                          personCreditTypes: nextChecked
                            ? [...new Set([...current.personCreditTypes, credit.value])]
                            : current.personCreditTypes.filter((value) => value !== credit.value)
                        }));
                        setErrors((current) => ({ ...current, creditTypes: undefined }));
                      }}
                    />
                    <span className="grid gap-0.5">
                      <span className="text-(length:--type-body-sm) font-medium text-foreground">{credit.label}</span>
                      <span className="text-(length:--type-caption) text-muted-foreground">{credit.description}</span>
                    </span>
                  </label>
                );
              })}
            </div>
            {errors.creditTypes ? <p className="text-(length:--type-caption) text-destructive">{errors.creditTypes}</p> : null}
          </DrawerSection>
        ) : null}

        <DrawerSection title="Filters" aside={hasFilters(form) ? "some filters set" : "none — follow the whole list"}>
          <Disclosure title="Fine-tune" summary="Genres, rating, year, age, certification, audience" open={filtersOpen} onOpenChange={setFiltersOpen}>
            <FieldRow>
              <Field label="Required genres" optional help="Comma-separated; at least one must match.">
                <Input value={form.requiredGenres} onChange={(event) => setForm((current) => ({ ...current, requiredGenres: event.target.value }))} placeholder="Action, Sci-Fi" />
              </Field>
              <Field label="Allowed certifications" optional help="e.g. PG-13, TV-14, TV-MA">
                <Input value={form.allowedCertifications} onChange={(event) => setForm((current) => ({ ...current, allowedCertifications: event.target.value }))} placeholder="PG, PG-13" />
              </Field>
            </FieldRow>
            <FieldRow>
              <Field label="Minimum rating" optional>
                <Input inputMode="decimal" value={form.minimumRating} onChange={(event) => setForm((current) => ({ ...current, minimumRating: event.target.value }))} placeholder="0–10" />
              </Field>
              <Field label="Minimum year" optional>
                <Input inputMode="numeric" value={form.minimumYear} onChange={(event) => setForm((current) => ({ ...current, minimumYear: event.target.value }))} placeholder="e.g. 2015" />
              </Field>
            </FieldRow>
            <FieldRow>
              <Field label="Maximum age" optional help="Days since release.">
                <Input inputMode="numeric" value={form.maximumAgeDays} onChange={(event) => setForm((current) => ({ ...current, maximumAgeDays: event.target.value }))} placeholder="e.g. 365" />
              </Field>
              <Field label="Audience">
                <Select value={form.audience} onChange={(event) => setForm((current) => ({ ...current, audience: event.target.value }))} options={[{ value: "any", label: "Any" }, { value: "kids", label: "Kids" }, { value: "adult", label: "Adult" }]} />
              </Field>
            </FieldRow>
          </Disclosure>
        </DrawerSection>

        <DrawerSection title="Behaviour">
          <Field label="Check the list" help="How often Deluno re-reads the list for new titles.">
            <PresetField inputType="number" value={form.syncIntervalHours} onChange={(value) => setForm((current) => ({ ...current, syncIntervalHours: value }))} options={SYNC_OPTIONS} customLabel="Custom interval" customPlaceholder="Hours" />
          </Field>
          <FieldRow>
            <SwitchRow label="Search when a title is added" description="Turn off to add titles without downloading anything yet." checked={form.searchOnAdd} onCheckedChange={(checked) => setForm((current) => ({ ...current, searchOnAdd: checked }))} />
            <SwitchRow label="Enabled" description="Checked on the schedule above. Syncing never removes titles already in your library." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} className="sm:border-l sm:border-hairline sm:pl-(--grid-gap)" />
          </FieldRow>
          {form.mediaType === "movies" ? (
            <FieldRow>
              <Field label="Don't search until" help="Holds a list-added movie back from searching until it reaches this release stage.">
                <Select
                  value={form.minimumAvailability}
                  onChange={(event) => setForm((current) => ({ ...current, minimumAvailability: event.target.value }))}
                  options={[
                    { value: "announced", label: "Announced — search right away" },
                    { value: "inCinemas", label: "In cinemas" },
                    { value: "released", label: "Released (digital/physical)" }
                  ]}
                />
              </Field>
              <Field label="Monitor" help="Whether an added movie is watched for at all.">
                <Select
                  value={form.monitorMode}
                  onChange={(event) => setForm((current) => ({ ...current, monitorMode: event.target.value }))}
                  options={[
                    { value: "movie", label: "Monitored" },
                    { value: "none", label: "Not monitored — add without watching for it" }
                  ]}
                />
              </Field>
            </FieldRow>
          ) : (
            <>
              <Field label="Monitor" help="How much of a newly added show to watch for.">
                <Select
                  value={form.monitorMode}
                  onChange={(event) => setForm((current) => ({ ...current, monitorMode: event.target.value }))}
                  options={[
                    { value: "all", label: "All episodes" },
                    { value: "future", label: "Future episodes only" },
                    { value: "missing", label: "Missing episodes" },
                    { value: "existing", label: "Existing episodes only" },
                    { value: "recent", label: "Most recent season only" }
                  ]}
                />
              </Field>
              <FieldRow>
                <Field label="Series type" help="Mirrors the same choice on a direct series add.">
                  <Select
                    value={form.seriesType}
                    onChange={(event) => setForm((current) => ({ ...current, seriesType: event.target.value }))}
                    options={[
                      { value: "standard", label: "Standard" },
                      { value: "daily", label: "Daily" },
                      { value: "anime", label: "Anime" }
                    ]}
                  />
                </Field>
                <Field label="Season folder" optional help="Leave on Default to follow Media Management's season-folder setting.">
                  <Select
                    value={form.seasonFolder}
                    onChange={(event) => setForm((current) => ({ ...current, seasonFolder: event.target.value as ListForm["seasonFolder"] }))}
                    options={[
                      { value: "", label: "Default" },
                      { value: "true", label: "On" },
                      { value: "false", label: "Off" }
                    ]}
                  />
                </Field>
              </FieldRow>
            </>
          )}
          <Field label="Tags for titles this list adds" optional help="Each new title this list adds gets these tags straight away, so a destination rule, a download client or a notification can act on it. Titles already in your library are left alone.">
            <TagPicker label="Tags for titles this list adds" value={form.tags} onChange={(tags) => setForm((current) => ({ ...current, tags }))} warnUnused={false} />
          </Field>
        </DrawerSection>

        {editing ? (
        <DrawerSection title="Preview & sync" aside={editing.lastSyncUtc ? `last sync ${relative(editing.lastSyncUtc, preferences)} · ${editing.lastSyncStatus}` : "never synced"}>
            {editing.lastSyncSummary ? <p className="text-(length:--type-caption) text-muted-foreground">{editing.lastSyncSummary}</p> : null}
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="sm" title="Preview without adding titles" onClick={() => void loadPreview()} disabled={busy !== null || dirty}>
                {busy === "preview" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Eye className="h-3.5 w-3.5" />}
                Preview list
              </Button>
              <Button type="button" variant="outline" size="sm" onClick={() => void syncNow()} disabled={busy !== null || dirty}>
                {busy === "sync" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <RefreshCcw className="h-3.5 w-3.5" />}
                Sync now
              </Button>
              {dirty ? <span className="self-center text-(length:--type-caption) text-muted-foreground">Save your changes first.</span> : null}
            </div>
            {previewNote ? <p role="status" className="text-(length:--type-caption) text-foreground">{previewNote}</p> : null}
            {preview ? (
              <ImportListPreview
                preview={preview}
                selectedKeys={selectedKeys}
                busy={busy?.startsWith("approve:") ?? false}
                onSelectionChange={(key, selected) => setSelectedKeys((current) => (selected ? [...new Set([...current, key])] : current.filter((item) => item !== key)))}
                onApprove={(searchAfterAdd) => void approvePreview(searchAfterAdd)}
                onExclude={(entry, durationDays) => void excludeEntry(entry, durationDays)}
                onRestore={(entry) => void restoreEntry(entry)}
              />
            ) : null}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger title="Remove this list" description="Titles it already added stay in your library." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy !== null}>Remove…</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Remove “${editing?.name ?? form.name}”?`}
        description="Deluno stops following this list. Titles it already added stay in your library."
        confirmLabel="Remove list"
        busy={busy === "remove"}
        onConfirm={() => void handleRemove()}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this list haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

/* ------------------------------------------------------------- preview */

function ImportListPreview({
  preview,
  selectedKeys,
  busy,
  onSelectionChange,
  onApprove,
  onExclude,
  onRestore
}: {
  preview: IntakeListPreviewResult;
  selectedKeys: string[];
  busy: boolean;
  onSelectionChange: (key: string, selected: boolean) => void;
  onApprove: (searchAfterAdd: boolean) => void;
  onExclude: (entry: IntakeListPreviewItem, durationDays: number | null) => void;
  onRestore: (entry: IntakeListPreviewItem) => void;
}) {
  const wouldAdd = preview.items.filter((item) => item.action === "would add").length;
  const existing = preview.items.filter((item) => item.action === "already in library").length;
  return (
    <div className="grid gap-(--grid-gap) rounded-[10px] border border-hairline bg-surface-2/50 p-(--field-pad-x)">
      <div>
        <p className="text-(length:--type-body-sm) font-medium text-foreground">Read-only preview</p>
        <p className="text-(length:--type-caption) text-muted-foreground">
          {preview.fetchedCount} found · {wouldAdd} would add · {existing} already in library{preview.targetLibraryName ? ` · adds to ${preview.targetLibraryName}` : " · no library chosen"}
        </p>
        {preview.warnings.map((warning) => (
          <p key={warning} className="mt-1 text-(length:--type-caption) text-warning">{warning}</p>
        ))}
      </div>
      <div className="grid max-h-72 gap-2 overflow-y-auto">
        {preview.items.map((entry, index) => {
          const key = previewEntryKey(entry);
          const selectable = entry.action === "would add";
          const id = `preview-${index}`;
          return (
            <div key={`${entry.title}-${entry.year ?? "unknown"}-${index}`} className={cn("rounded-[10px] border px-(--field-pad-x) py-2", selectable && selectedKeys.includes(key) ? "border-primary/30 bg-primary/6" : "border-hairline bg-card")}>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <label htmlFor={selectable ? id : undefined} className="flex items-center gap-2 text-(length:--type-body-sm) font-medium text-foreground">
                  {selectable ? <Checkbox id={id} checked={selectedKeys.includes(key)} onCheckedChange={(checked) => onSelectionChange(key, checked)} /> : null}
                  {entry.title}
                  {entry.year ? ` (${entry.year})` : ""}
                </label>
                <Chip tone={entry.action === "would add" ? "ok" : entry.action === "excluded" ? "idle" : "info"}>{entry.action}</Chip>
              </div>
              <p className="mt-1 text-(length:--type-caption) text-muted-foreground">{entry.reason} · {entry.matchConfidence} confidence</p>
              {selectable ? (
                <div className="mt-1.5 flex flex-wrap gap-1.5">
                  <Button type="button" size="sm" variant="ghost" className="h-7 px-2" onClick={() => onExclude(entry, 7)}>Ignore 7 days</Button>
                  <Button type="button" size="sm" variant="ghost" className="h-7 px-2" onClick={() => onExclude(entry, null)}>Exclude</Button>
                </div>
              ) : entry.action === "excluded" && entry.exclusionId ? (
                <Button type="button" size="sm" variant="ghost" className="mt-1.5 h-7 px-2" onClick={() => onRestore(entry)}>Allow again</Button>
              ) : null}
            </div>
          );
        })}
      </div>
      {wouldAdd ? (
        <div className="flex flex-wrap gap-2">
          <Button type="button" size="sm" variant="outline" disabled={busy || selectedKeys.length === 0} onClick={() => onApprove(false)}>
            {busy ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
            Add selected
          </Button>
          <Button type="button" size="sm" disabled={busy || selectedKeys.length === 0} onClick={() => onApprove(true)}>
            {busy ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
            Add selected and search
          </Button>
        </div>
      ) : null}
    </div>
  );
}

/* --------------------------------------------------------------- utils */

function emptyForm(libraries: LibraryItem[]): ListForm {
  return {
    name: "",
    provider: "url-list",
    feedUrl: "",
    mediaType: "movies",
    libraryId: libraries.find((library) => library.mediaType === "movies")?.id ?? "",
    qualityProfileId: "",
    requiredGenres: "",
    minimumRating: "",
    minimumYear: "",
    maximumAgeDays: "",
    allowedCertifications: "",
    audience: "any",
    syncIntervalHours: "24",
    searchOnAdd: true,
    isEnabled: true,
    personCreditTypes: ["cast"],
    minimumAvailability: "announced",
    monitorMode: "movie",
    seriesType: "standard",
    seasonFolder: "",
    tags: ""
  };
}

function formFrom(item: IntakeSourceItem): ListForm {
  return {
    name: item.name,
    provider: item.provider,
    feedUrl: item.feedUrl,
    mediaType: item.mediaType === "tv" ? "tv" : "movies",
    libraryId: item.libraryId ?? "",
    qualityProfileId: item.qualityProfileId ?? "",
    requiredGenres: item.requiredGenres ?? "",
    minimumRating: item.minimumRating?.toString() ?? "",
    minimumYear: item.minimumYear?.toString() ?? "",
    maximumAgeDays: item.maximumAgeDays?.toString() ?? "",
    allowedCertifications: item.allowedCertifications ?? "",
    audience: item.audience ?? "any",
    syncIntervalHours: String(item.syncIntervalHours ?? 24),
    searchOnAdd: item.searchOnAdd,
    isEnabled: item.isEnabled,
    personCreditTypes: item.provider === "tmdb-person" ? parseTmdbPersonAddress(item.feedUrl).creditTypes : ["cast"],
    minimumAvailability: item.minimumAvailability ?? "announced",
    monitorMode: item.monitorMode ?? (item.mediaType === "tv" ? "all" : "movie"),
    seriesType: item.seriesType ?? "standard",
    seasonFolder: item.seasonFolder === true ? "true" : item.seasonFolder === false ? "false" : "",
    tags: item.tags ?? ""
  };
}

function toPayload(form: ListForm) {
  return {
    name: form.name.trim(),
    provider: form.provider,
    feedUrl: form.provider === "tmdb-person" ? buildTmdbPersonAddress(form.feedUrl, form.personCreditTypes) : form.feedUrl.trim(),
    mediaType: form.mediaType,
    libraryId: form.libraryId || null,
    qualityProfileId: form.qualityProfileId || null,
    requiredGenres: form.requiredGenres,
    minimumRating: form.minimumRating.trim() ? Number(form.minimumRating) : null,
    minimumYear: form.minimumYear.trim() ? Number(form.minimumYear) : null,
    maximumAgeDays: form.maximumAgeDays.trim() ? Number(form.maximumAgeDays) : null,
    allowedCertifications: form.allowedCertifications,
    audience: form.audience,
    syncIntervalHours: form.syncIntervalHours.trim() ? Number(form.syncIntervalHours) : 24,
    searchOnAdd: form.searchOnAdd,
    isEnabled: form.isEnabled,
    minimumAvailability: form.mediaType === "movies" ? form.minimumAvailability : "announced",
    monitorMode: form.monitorMode,
    seriesType: form.mediaType === "tv" ? form.seriesType : null,
    seasonFolder: form.mediaType === "tv" && form.seasonFolder !== "" ? form.seasonFolder === "true" : null,
    tags: form.tags.trim()
  };
}

function sameForm(a: ListForm, b: ListForm) {
  return (Object.keys(a) as (keyof ListForm)[]).every((key) =>
    key === "personCreditTypes"
      ? a.personCreditTypes.join(",") === b.personCreditTypes.join(",")
      : a[key] === b[key]);
}

function hasFilters(form: ListForm) {
  return Boolean(form.requiredGenres.trim() || form.minimumRating.trim() || form.minimumYear.trim() || form.maximumAgeDays.trim() || form.allowedCertifications.trim() || (form.audience && form.audience !== "any"));
}

function syncChip(item: IntakeSourceItem): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (!item.isEnabled) return { tone: "idle", label: "Off" };
  if (!item.libraryId) return { tone: "warn", label: "No library" };
  switch (item.lastSyncStatus) {
    case "success":
      return { tone: "ok", label: "Synced" };
    case "partial":
      return { tone: "warn", label: "Partial" };
    case "error":
      return { tone: "bad", label: "Failed" };
    default:
      return { tone: "idle", label: "Not synced" };
  }
}

function relative(iso: string, preferences: DisplayPreferences) {
  if (!preferences.showRelativeDates) return formatDateTime(iso, preferences);
  const minutes = Math.round(Math.abs(Date.now() - new Date(iso).getTime()) / 60000);
  return minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`;
}

function previewEntryKey(entry: { title: string; year: number | null; imdbId: string | null }) {
  return `${entry.imdbId ?? "title"}:${entry.title.toLocaleLowerCase()}:${entry.year ?? ""}`;
}

function listAddressHelp(provider: string) {
  switch (provider) {
    case "trakt":
      return "Paste a Trakt list or watchlist URL. A Trakt username also follows that person's watchlist.";
    case "imdb":
      return "Paste an IMDb list URL, its ls… identifier, or an IMDb CSV export URL.";
    case "tmdb":
      return "Paste a TMDb list URL or list ID.";
    case "tmdb-person":
      return "Paste a TMDb person URL or numeric person ID. Choose the credit types Deluno should follow below.";
    case "tmdb-popular":
      return "No address needed — reads TMDb's current Popular list for the media type below.";
    case "tmdb-discover":
      return "Enter one or more TMDb keyword IDs, comma-separated.";
    case "tmdb-collection":
      return "Paste a TMDb collection URL or numeric collection ID. Every movie in the collection is added.";
    case "tmdb-company-credits":
      return "Paste a TMDb company URL or numeric company ID.";
    case "mdblist":
      return "For a public MDbList list, choose Custom list URL and paste https://mdblist.com/lists/owner/list-name.";
    case "letterboxd":
      return "Paste a public Letterboxd list URL or its RSS feed.";
    case "rss":
      return "Paste a public RSS or Atom feed URL.";
    case "stevenlu":
      return "Paste a StevenLu-format JSON feed URL — a plain list of { title, imdb_id } entries, movies only.";
    default:
      return "Paste a public list URL. Deluno recognises compatible list sites automatically.";
  }
}

function parseTmdbPersonAddress(feedUrl: string): { personId: string; creditTypes: TmdbPersonCreditType[] } {
  const fallback = { personId: "", creditTypes: ["cast"] as TmdbPersonCreditType[] };
  const value = feedUrl.trim();
  if (!value) return fallback;

  let url: URL;
  try {
    url = new URL(/^\d+$/.test(value) ? `https://www.themoviedb.org/person/${value}` : value);
  } catch {
    return fallback;
  }

  const segments = url.pathname.split("/").filter(Boolean);
  if (segments.length < 2 || segments[0].toLowerCase() !== "person" || !/^\d+$/.test(segments[1])) return fallback;
  const values = (url.searchParams.get("credits") ?? "cast")
    .split(",")
    .map((value) => value.trim().toLowerCase())
    .filter((value): value is TmdbPersonCreditType => TMDB_PERSON_CREDIT_TYPES.some((credit) => credit.value === value));
  return { personId: segments[1], creditTypes: values.length ? [...new Set(values)] : ["cast"] };
}

function buildTmdbPersonAddress(feedUrl: string, creditTypes: TmdbPersonCreditType[]) {
  const parsed = parseTmdbPersonAddress(feedUrl);
  const selected = creditTypes.length ? creditTypes : ["cast"] as TmdbPersonCreditType[];
  if (!parsed.personId) return feedUrl.trim();
  return `https://www.themoviedb.org/person/${parsed.personId}?credits=${selected.join(",")}`;
}

/**
 * #1046: Sonarr's "keep and tag" for list sync. Off unless someone turns it on,
 * existing installs included; when on, a title that has left every import list
 * it came from is given the tag chosen here and Activity says so.
 */
export function OffListTagCard({ enabled, tag, onSaved }: { enabled: boolean; tag: string; onSaved: () => void }) {
  const [on, setOn] = useState(enabled);
  const [name, setName] = useState(tag);
  const [busy, setBusy] = useState(false);
  const changed = on !== enabled || name.trim() !== tag.trim();
  const missingTag = on && !name.trim();

  async function save() {
    if (missingTag || busy) return;
    setBusy(true);
    try {
      const response = await authedFetch("/api/settings", {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tagTitlesOffEveryList: on, offListTag: name.trim() })
      });
      if (!response.ok) throw new Error("This setting could not be saved.");
      toast.success("Saved");
      onSaved();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "This setting could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <ListCard title="Titles that leave every list" count={enabled ? "On" : "Off"}>
      <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
        <p className="text-(length:--type-caption) text-muted-foreground">
          Deluno never removes a title because it dropped off a list. Turn this on to tag it instead, so you can find it later or let a tag rule decide what happens to it. A title still on another list is left alone.
        </p>
        <SwitchRow label="Tag titles that are no longer on any list" description="Off by default. Checked after each successful list sync." checked={on} onCheckedChange={setOn} />
        <Field label="Tag to give them" optional={!on} help={missingTag ? "Choose a tag before turning this on." : "Created for you if it does not exist yet."}>
          <TagPicker label="Tag for titles that left every list" value={name} onChange={setName} single warnUnused={false} placeholder="For example: off-list" />
        </Field>
        {changed ? (
          <div>
            <Button type="button" size="sm" onClick={() => void save()} disabled={busy || missingTag}>
              {busy ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
              Save list tag settings
            </Button>
          </div>
        ) : null}
      </div>
    </ListCard>
  );
}

async function readIntakeSourceError(response: Response, fallback: string) {
  const body = (await response.json().catch(() => null)) as { errors?: Record<string, string[] | undefined>; detail?: string; title?: string } | null;
  const validationMessage = body?.errors ? Object.values(body.errors).flat().find((value): value is string => Boolean(value?.trim())) : null;
  return validationMessage ?? body?.detail ?? body?.title ?? fallback;
}
