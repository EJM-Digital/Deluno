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
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Eye, LoaderCircle, Plus, RefreshCcw } from "lucide-react";
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
  type IntakeListApprovalResult,
  type IntakeListPreviewItem,
  type IntakeListPreviewResult,
  type IntakeSourceItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";

const PROVIDERS = [
  { label: "Custom list URL", value: "url-list" },
  { label: "Trakt", value: "trakt" },
  { label: "IMDb", value: "imdb" },
  { label: "TMDb", value: "tmdb" },
  { label: "Letterboxd", value: "letterboxd" },
  { label: "RSS feed", value: "rss" }
];
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
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsListsLoader(): Promise<LoaderData> {
  const overview = await settingsOverviewLoader();
  return { ...overview, intakeSources: overview.intakeSources };
}

export function SettingsListsPage() {
  const { intakeSources, libraries } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();

  const [togglingId, setTogglingId] = useState<string | null>(null);
  const sorted = useMemo(() => [...intakeSources].sort((a, b) => a.name.localeCompare(b.name)), [intakeSources]);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<ListForm>(() => emptyForm(libraries));
  const [initialForm, setInitialForm] = useState<ListForm>(() => emptyForm(libraries));
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<{ name?: string; feedUrl?: string }>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [preview, setPreview] = useState<IntakeListPreviewResult | null>(null);
  const [previewNote, setPreviewNote] = useState<string | null>(null);
  const [selectedKeys, setSelectedKeys] = useState<string[]>([]);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? intakeSources.find((item) => item.id === mode.id) ?? null : null;
  const dirty = useMemo(() => isOpen && !sameForm(form, initialForm), [isOpen, form, initialForm]);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
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
    if (!form.name.trim()) nextErrors.name = "Give this list a name.";
    if (!form.feedUrl.trim()) nextErrors.feedUrl = "Paste the list URL or identifier.";
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
      setForm(settled);
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
      const response = await authedFetch(`/api/intake-sources/${id}/exclude-preview`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title: entry.title, year: entry.year, imdbId: entry.imdbId, durationDays }) });
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

  /* ---------------------------------------------------------- render */
  const providerLabel = (value: string) => PROVIDERS.find((provider) => provider.value === value)?.label ?? value;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        actions={
          <PageToolbarAction onClick={openCreate}>New list</PageToolbarAction>
        }
      />


      <ListCard title="Import Lists" count={intakeSources.length ? `${intakeSources.length} ${intakeSources.length === 1 ? "list" : "lists"} · ${intakeSources.filter((item) => item.isEnabled).length} enabled · add titles from watchlists and feeds` : undefined}>
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
          <ListTable columns={[{ label: "Name" }, { label: "List" }, { label: "Adds to" }, { label: "Last sync" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]}>
            {sorted.map((item) => {
              const chip = syncChip(item);
              return (
                <ListRow key={item.id} onClick={() => openEdit(item)} selected={mode.kind === "edit" && mode.id === item.id}>
                  <ListNameCell name={item.name} sub={`${providerLabel(item.provider)} · ${item.mediaType === "tv" ? "TV" : "Movies"}`} />
                  <ListCell mono primary={item.feedUrl} secondary={item.searchOnAdd ? "Adds and searches" : "Adds only"} />
                  <ListCell primary={item.libraryName ?? <span className="text-muted-foreground">No library</span>} secondary={item.libraryName ? `Every ${item.syncIntervalHours} h` : "Choose a library before syncing"} />
                  <ListCell numeric primary={item.lastSyncUtc ? relative(item.lastSyncUtc) : <span className="text-muted-foreground">Never</span>} secondary={item.lastSyncSummary ?? (item.lastSyncStatus === "never" ? "Not synced yet" : item.lastSyncStatus)} />
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
            <Field label="Name" error={errors.name}>
              <Input value={form.name} onChange={(event) => { setErrors((current) => ({ ...current, name: undefined })); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Weekend movies" autoComplete="off" />
            </Field>
            <Field label="Provider">
              <Select value={form.provider} onChange={(event) => setForm((current) => ({ ...current, provider: event.target.value }))} options={PROVIDERS} />
            </Field>
          </FieldRow>
          <Field label="List URL" help={listAddressHelp(form.provider)} error={errors.feedUrl}>
            <Input value={form.feedUrl} onChange={(event) => { setErrors((current) => ({ ...current, feedUrl: undefined })); setForm((current) => ({ ...current, feedUrl: event.target.value })); }} placeholder="https://…" className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
          </Field>
          <FieldRow>
            <Field label="Media type">
              <SegmentedControl<"movies" | "tv">
                value={form.mediaType}
                onValueChange={(mediaType) => setForm((current) => ({ ...current, mediaType, libraryId: libraries.find((library) => library.mediaType === mediaType)?.id ?? "" }))}
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
            <SwitchRow label="Enabled" description="Checked on the schedule above. Syncing never removes titles already in your library." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} className="sm:border-l sm:border-hairline sm:pl-[var(--grid-gap)]" />
          </FieldRow>
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Preview & sync" aside={editing.lastSyncUtc ? `last sync ${relative(editing.lastSyncUtc)} · ${editing.lastSyncStatus}` : "never synced"}>
            {editing.lastSyncSummary ? <p className="text-[length:var(--type-caption)] text-muted-foreground">{editing.lastSyncSummary}</p> : null}
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="sm" title="Preview without adding titles" onClick={() => void loadPreview()} disabled={busy !== null || dirty}>
                {busy === "preview" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Eye className="h-3.5 w-3.5" />}
                Preview list
              </Button>
              <Button type="button" variant="outline" size="sm" onClick={() => void syncNow()} disabled={busy !== null || dirty}>
                {busy === "sync" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <RefreshCcw className="h-3.5 w-3.5" />}
                Sync now
              </Button>
              {dirty ? <span className="self-center text-[length:var(--type-caption)] text-muted-foreground">Save your changes first.</span> : null}
            </div>
            {previewNote ? <p role="status" className="text-[length:var(--type-caption)] text-foreground">{previewNote}</p> : null}
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
    <div className="grid gap-[var(--grid-gap)] rounded-[10px] border border-hairline bg-surface-2/50 p-[var(--field-pad-x)]">
      <div>
        <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">Read-only preview</p>
        <p className="text-[length:var(--type-caption)] text-muted-foreground">
          {preview.fetchedCount} found · {wouldAdd} would add · {existing} already in library{preview.targetLibraryName ? ` · adds to ${preview.targetLibraryName}` : " · no library chosen"}
        </p>
        {preview.warnings.map((warning) => (
          <p key={warning} className="mt-1 text-[length:var(--type-caption)] text-warning">{warning}</p>
        ))}
      </div>
      <div className="grid max-h-72 gap-2 overflow-y-auto">
        {preview.items.map((entry, index) => {
          const key = previewEntryKey(entry);
          const selectable = entry.action === "would add";
          const id = `preview-${index}`;
          return (
            <div key={`${entry.title}-${entry.year ?? "unknown"}-${index}`} className={cn("rounded-[10px] border px-[var(--field-pad-x)] py-2", selectable && selectedKeys.includes(key) ? "border-primary/30 bg-primary/[0.06]" : "border-hairline bg-card")}>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <label htmlFor={selectable ? id : undefined} className="flex items-center gap-2 text-[length:var(--type-body-sm)] font-medium text-foreground">
                  {selectable ? <Checkbox id={id} checked={selectedKeys.includes(key)} onCheckedChange={(checked) => onSelectionChange(key, checked)} /> : null}
                  {entry.title}
                  {entry.year ? ` (${entry.year})` : ""}
                </label>
                <Chip tone={entry.action === "would add" ? "ok" : entry.action === "excluded" ? "idle" : "info"}>{entry.action}</Chip>
              </div>
              <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">{entry.reason} · {entry.matchConfidence} confidence</p>
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
    isEnabled: true
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
    isEnabled: item.isEnabled
  };
}

function toPayload(form: ListForm) {
  return {
    name: form.name.trim(),
    provider: form.provider,
    feedUrl: form.feedUrl.trim(),
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
    isEnabled: form.isEnabled
  };
}

function sameForm(a: ListForm, b: ListForm) {
  return (Object.keys(a) as (keyof ListForm)[]).every((key) => a[key] === b[key]);
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

function relative(iso: string) {
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
    case "mdblist":
      return "For a public MDbList list, choose Custom list URL and paste https://mdblist.com/lists/owner/list-name.";
    case "letterboxd":
      return "Paste a public Letterboxd list URL or its RSS feed.";
    case "rss":
      return "Paste a public RSS or Atom feed URL.";
    default:
      return "Paste a public list URL. Deluno recognises compatible list sites automatically.";
  }
}

async function readIntakeSourceError(response: Response, fallback: string) {
  const body = (await response.json().catch(() => null)) as { errors?: Record<string, string[] | undefined>; detail?: string; title?: string } | null;
  const validationMessage = body?.errors ? Object.values(body.errors).flat().find((value): value is string => Boolean(value?.trim())) : null;
  return validationMessage ?? body?.detail ?? body?.title ?? fallback;
}
