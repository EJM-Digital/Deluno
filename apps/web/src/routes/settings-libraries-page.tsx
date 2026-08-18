/**
 * Libraries — list → drawer.
 *
 *   PageToolbar (Library setup tabs · New library)
 *   ListCard  (name · folder · default plan · automation · status · on · ›)
 *   Drawer    (Basics · Automation · Remove)
 *
 * Contracts: POST /api/libraries, PUT /api/libraries/{id} (name/folders),
 * PUT /api/libraries/{id}/media-plan, PUT /api/libraries/{id}/quality-profile,
 * PUT /api/libraries/{id}/automation, DELETE /api/libraries/{id}.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { Plus } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { emptyPlatformSettingsSnapshot, fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type PolicySetItem, type QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { MEDIA_PLAN_STARTERS, type MediaPlanStarter } from "../lib/media-plan-starters";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";

const STARTER_VALUE_PREFIX = "starter:";

interface LoaderData {
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  qualityProfiles: QualityProfileItem[];
  policySets: PolicySetItem[];
}

interface LibraryForm {
  name: string;
  mediaType: "movies" | "tv";
  rootPath: string;
  downloadsPath: string;
  /** "" = none · policySetId · "starter:<id>" */
  planChoice: string;
  qualityProfileId: string;
  autoSearchEnabled: boolean;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsLibrariesLoader(): Promise<LoaderData> {
  const [libraries, settings, qualityProfiles, policySets] = await Promise.all([
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => emptyPlatformSettingsSnapshot),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles"),
    fetchJson<PolicySetItem[]>("/api/policy-sets")
  ]);
  return { libraries, settings, qualityProfiles, policySets };
}

export function SettingsLibrariesPage() {
  const { libraries, settings, qualityProfiles, policySets } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();

  /* ------------------------------------------------------------ list */
  const [togglingId, setTogglingId] = useState<string | null>(null);
  const sortedLibraries = useMemo(() => [...libraries].sort((a, b) => a.name.localeCompare(b.name)), [libraries]);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<LibraryForm>(() => emptyForm("movies", settings));
  const [initialForm, setInitialForm] = useState<LibraryForm>(() => emptyForm("movies", settings));
  const [profileOpen, setProfileOpen] = useState(false);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<{ name?: string; rootPath?: string }>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? libraries.find((library) => library.id === mode.id) ?? null : null;
  const dirty = useMemo(() => isOpen && !sameForm(form, initialForm), [isOpen, form, initialForm]);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  const blocker = useUnsavedChanges(dirty);

  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  const profiles = useMemo(() => qualityProfiles.filter((profile) => profile.mediaType === form.mediaType), [form.mediaType, qualityProfiles]);
  const plans = useMemo(() => policySets.filter((plan) => plan.mediaType === form.mediaType && plan.isEnabled), [form.mediaType, policySets]);
  const starters = useMemo(() => MEDIA_PLAN_STARTERS.filter((starter) => starter.values.mediaType === form.mediaType), [form.mediaType]);
  const chosenPlan = plans.find((plan) => plan.id === form.planChoice);
  const chosenStarter = getStarterFromChoice(form.planChoice);

  function openCreate() {
    const next = emptyForm("movies", settings);
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    setProfileOpen(false);
    setSaveState(undefined);
    setErrors({});
  }

  function openEdit(library: LibraryItem) {
    const next = formFromLibrary(library);
    setMode({ kind: "edit", id: library.id });
    setForm(next);
    setInitialForm(next);
    setProfileOpen(!library.defaultPolicySetId && Boolean(library.qualityProfileId));
    setSaveState(undefined);
    setErrors({});
  }

  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }

  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  function chooseType(mediaType: "movies" | "tv") {
    if (mode.kind !== "create" || mediaType === form.mediaType) return;
    setForm((current) => ({
      ...current,
      mediaType,
      name: current.name.trim() && current.name !== defaultName(current.mediaType) ? current.name : defaultName(mediaType),
      rootPath: current.rootPath.trim() && current.rootPath !== defaultRoot(current.mediaType, settings) ? current.rootPath : defaultRoot(mediaType, settings),
      planChoice: "",
      qualityProfileId: ""
    }));
  }

  /* ---------------------------------------------------------- saving */
  async function resolvePlanChoice(value: string): Promise<string | null> {
    if (!value) return null;
    const starter = getStarterFromChoice(value);
    if (!starter) return value;

    const existing = policySets.find(
      (plan) => plan.isEnabled && plan.mediaType === starter.values.mediaType && plan.name.trim().toLowerCase() === starter.values.name.trim().toLowerCase()
    );
    if (existing) return existing.id;

    const qualityProfile = chooseStarterQualityProfile(starter, qualityProfiles);
    const response = await authedFetch("/api/policy-sets", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: starter.values.name,
        mediaType: starter.values.mediaType,
        qualityProfileId: qualityProfile?.id ?? null,
        destinationRuleId: null,
        customFormatIds: "",
        searchIntervalOverrideHours: starter.values.searchIntervalOverrideHours ? Number(starter.values.searchIntervalOverrideHours) : null,
        retryDelayOverrideHours: starter.values.retryDelayOverrideHours ? Number(starter.values.retryDelayOverrideHours) : null,
        upgradeUntilCutoff: starter.values.upgradeUntilCutoff,
        isEnabled: true,
        notes: starter.values.notes
      })
    });
    if (!response.ok) throw new Error("The default media plan could not be created.");
    return ((await response.json()) as PolicySetItem).id;
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    const nextErrors: typeof errors = {};
    if (!form.name.trim()) nextErrors.name = "Give this library a name.";
    if (!form.rootPath.trim()) nextErrors.rootPath = "Choose a folder for this library.";
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) return;

    setBusy(true);
    setSaveState("saving");
    try {
      let library: LibraryItem;
      if (mode.kind === "create") {
        const response = await authedFetch("/api/libraries", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            name: form.name.trim(),
            mediaType: form.mediaType,
            purpose: "Main library",
            rootPath: form.rootPath.trim(),
            downloadsPath: form.downloadsPath.trim() || null,
            qualityProfileId: form.planChoice ? null : form.qualityProfileId || null,
            autoSearchEnabled: form.autoSearchEnabled,
            missingSearchEnabled: true,
            upgradeSearchEnabled: true,
            searchIntervalHours: 12,
            retryDelayHours: 6,
            maxItemsPerRun: 10
          })
        });
        if (!response.ok) throw new Error((await response.text().catch(() => "")) || "Library could not be created.");
        library = (await response.json()) as LibraryItem;
        const planId = await resolvePlanChoice(form.planChoice);
        if (planId) await putJson(`/api/libraries/${library.id}/media-plan`, { policySetId: planId }, "Library was created, but the media plan could not be assigned.");
      } else {
        const id = mode.id;
        const before = initialForm;
        if (form.name !== before.name || form.rootPath !== before.rootPath || form.downloadsPath !== before.downloadsPath) {
          await putJson(`/api/libraries/${id}`, { name: form.name.trim(), rootPath: form.rootPath.trim(), downloadsPath: form.downloadsPath.trim() || null }, "Library details could not be saved.");
        }
        if (form.planChoice !== before.planChoice) {
          const planId = await resolvePlanChoice(form.planChoice);
          await putJson(`/api/libraries/${id}/media-plan`, { policySetId: planId }, "Media plan could not be assigned.");
        }
        if (!form.planChoice && form.qualityProfileId !== before.qualityProfileId) {
          await putJson(`/api/libraries/${id}/quality-profile`, { qualityProfileId: form.qualityProfileId }, "Quality profile could not be assigned.");
        }
        if (form.autoSearchEnabled !== before.autoSearchEnabled && editing) {
          await putJson(`/api/libraries/${id}/automation`, automationPayload(editing, form.autoSearchEnabled), "Automation could not be updated.");
        }
        library = editing!;
      }

      // Re-read so starter choices resolve to the saved plan id and server-side normalisation shows.
      const fresh = (await fetchJson<LibraryItem[]>("/api/libraries")).find((item) => item.id === library.id) ?? library;
      const settled = formFromLibrary(fresh);
      setForm(settled);
      setInitialForm(settled);
      if (mode.kind === "create") setMode({ kind: "edit", id: library.id });
      setSaveState("saved");
      setSaveMessage(mode.kind === "create" ? "Library created" : "Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(false);
    }
  }

  async function handleRemove() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/libraries/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Library could not be removed.");
      toast.success(`${editing?.name ?? "Library"} removed`);
      setConfirmRemove(false);
      setInitialForm(form);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Library could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  async function toggleAutoSearch(library: LibraryItem, enabled: boolean) {
    setTogglingId(library.id);
    try {
      await putJson(`/api/libraries/${library.id}/automation`, automationPayload(library, enabled), `Could not ${enabled ? "resume" : "pause"} searching for ${library.name}.`);
      if (mode.kind === "edit" && mode.id === library.id && !dirty) {
        const next = { ...form, autoSearchEnabled: enabled };
        setForm(next);
        setInitialForm(next);
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Library could not be updated.");
    } finally {
      setTogglingId(null);
    }
  }

  /* ---------------------------------------------------------- render */
  const typeLabel = form.mediaType === "tv" ? "TV" : "Movies";
  const drawerTitle = mode.kind === "create" ? "New library" : editing?.name ?? form.name;
  const drawerDescription =
    mode.kind === "create" ? "Where the files live and how Deluno should look after them." : `Library · ${editing?.mediaType === "tv" ? "TV" : "Movies"} · ${editing?.rootPath ?? ""}`;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={librarySetupNavItems}
        actions={
          <Button type="button" onClick={openCreate}>
            <Plus className="h-4 w-4" />
            New library
          </Button>
        }
      />

      <ListCard title="Libraries" count={`${libraries.length} ${libraries.length === 1 ? "library" : "libraries"}`}>
        {libraries.length === 0 ? (
          <ListEmpty
            title="No libraries yet"
            description="A library tells Deluno whether it manages movies or TV, where the files live, and which media plan to follow. Most people need one Movies library and one TV library."
            actions={
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus className="h-3.5 w-3.5" />
                New library
              </Button>
            }
          />
        ) : (
          <ListTable
            columns={[
              { label: "Name" },
              { label: "Folder" },
              { label: "Default plan" },
              { label: "Searches" },
              { label: "Status", width: LIST_TRACK.status, mobile: true },
              { label: "On", width: LIST_TRACK.toggle, mobile: true }
            ]}
          >
            {sortedLibraries.map((library) => {
              const profile = qualityProfiles.find((item) => item.id === library.qualityProfileId);
              const running = library.automationStatus === "running";
              const tone = !library.autoSearchEnabled ? "muted" : running ? "info" : "ok";
              const status = !library.autoSearchEnabled ? "Manual" : running ? "Searching" : "Automated";
              return (
                <ListRow key={library.id} onClick={() => openEdit(library)} selected={mode.kind === "edit" && mode.id === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell mono primary={library.rootPath} secondary={library.downloadsPath ? library.downloadsPath : "Client's completed folder"} />
                  <ListCell
                    primary={library.defaultPolicySetName ?? (profile ? <span>Direct: {profile.name}</span> : <span className="text-muted-foreground">No plan</span>)}
                    secondary={library.defaultPolicySetName ? profile ? `${profile.name} · stops at ${profile.cutoffQuality}` : "Quality decided by the plan" : profile ? `Stops at ${profile.cutoffQuality}` : "Assign a plan to automate quality"}
                  />
                  <ListCell
                    numeric
                    primary={library.autoSearchEnabled ? `Every ${library.searchIntervalHours} h` : <span className="text-muted-foreground">Off</span>}
                    secondary={library.nextSearchUtc && library.autoSearchEnabled ? `Next ${formatRelative(library.nextSearchUtc)}` : library.lastSearchedUtc ? `Last ${formatRelative(library.lastSearchedUtc)}` : "Not searched yet"}
                  />
                  <ListCell mobile>
                    <Chip tone={tone}>{status}</Chip>
                  </ListCell>
                  <ListCell mobile>
                    <Switch
                      size="sm"
                      aria-label={`${library.autoSearchEnabled ? "Pause" : "Resume"} automatic searches for ${library.name}`}
                      checked={library.autoSearchEnabled}
                      disabled={togglingId === library.id}
                      onCheckedChange={(checked) => void toggleAutoSearch(library, checked)}
                    />
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
        title={drawerTitle}
        description={drawerDescription}
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={mode.kind === "create" ? "Create library" : "Save library"}
            onCancel={requestClose}
            disabled={busy}
          />
        }
      >
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Library name" error={errors.name}>
              <Input
                value={form.name}
                onChange={(event) => {
                  setErrors((current) => ({ ...current, name: undefined }));
                  setForm((current) => ({ ...current, name: event.target.value }));
                }}
                placeholder={defaultName(form.mediaType)}
                autoComplete="off"
              />
            </Field>
            <Field label="Media type">
              <SegmentedControl<"movies" | "tv">
                value={form.mediaType}
                onValueChange={chooseType}
                disabled={mode.kind === "edit"}
                options={[
                  { value: "movies", label: "Movies" },
                  { value: "tv", label: "TV shows" }
                ]}
              />
            </Field>
          </FieldRow>
          <Field label="Library folder" help="Where imported files end up." error={errors.rootPath}>
            <PathInput
              value={form.rootPath}
              onChange={(rootPath) => {
                setErrors((current) => ({ ...current, rootPath: undefined }));
                setForm((current) => ({ ...current, rootPath }));
              }}
              browseTitle={`Choose ${typeLabel.toLowerCase()} library folder`}
            />
          </Field>
          <Field label="Completed downloads folder" optional help="Leave blank to use the completed folder from your download client. Set only for a library-specific or processed-output folder.">
            <PathInput value={form.downloadsPath} onChange={(downloadsPath) => setForm((current) => ({ ...current, downloadsPath }))} browseTitle="Choose completed downloads folder" />
          </Field>
        </DrawerSection>

        <DrawerSection title="Automation">
          <Field
            label="Default media plan"
            help={
              chosenPlan
                ? `${chosenPlan.qualityProfileName ?? "No quality goal"} · upgrades ${chosenPlan.upgradeUntilCutoff ? "on" : "off"}`
                : chosenStarter
                  ? "An editable default — it becomes a saved plan you can tune later."
                  : "The quality, size, release and upgrade rules this library follows."
            }
          >
            <Select value={form.planChoice} onChange={(event) => setForm((current) => ({ ...current, planChoice: event.target.value, qualityProfileId: event.target.value ? "" : current.qualityProfileId }))}>
              <option value="">{mode.kind === "create" ? "Choose later" : "No plan — use the direct quality profile"}</option>
              {plans.length ? (
                <optgroup label="Saved plans">
                  {plans.map((plan) => (
                    <option key={plan.id} value={plan.id}>
                      {plan.name}
                    </option>
                  ))}
                </optgroup>
              ) : null}
              <optgroup label="Editable defaults">
                {starters.map((starter) => (
                  <option key={starter.id} value={starterChoiceValue(starter.id)}>
                    {starter.title.replace(/^Default:\s*/, "")}
                  </option>
                ))}
              </optgroup>
            </Select>
          </Field>
          {chosenPlan ? (
            <p className="-mt-2 text-[length:var(--type-caption)]">
              <Link to="/settings/policy-sets" className="font-medium text-primary hover:underline">
                Open {chosenPlan.name}
              </Link>
            </p>
          ) : null}
          <Disclosure
            title="Direct quality profile instead of a plan"
            summary={form.planChoice ? "Not used while a plan is assigned" : profiles.find((profile) => profile.id === form.qualityProfileId)?.name ?? "Advanced fallback — only when no plan fits"}
            open={profileOpen}
            onOpenChange={setProfileOpen}
          >
            <Field label="Quality profile" help={form.planChoice ? "Clear the media plan above to use a profile directly." : "Deluno uses this profile's tiers and cutoff without any plan-level rules."}>
              <Select
                value={form.qualityProfileId}
                disabled={Boolean(form.planChoice)}
                onChange={(event) => setForm((current) => ({ ...current, qualityProfileId: event.target.value }))}
                placeholder={`Standard ${typeLabel} profile`}
                options={profiles.map((profile) => ({ value: profile.id, label: `${profile.name}${profile.cutoffQuality ? ` · stops at ${profile.cutoffQuality}` : ""}` }))}
              />
            </Field>
          </Disclosure>
          <SwitchRow
            label="Search automatically"
            description="Look for missing files and upgrades on the schedule in Automation & recovery."
            checked={form.autoSearchEnabled}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, autoSearchEnabled: checked }))}
          />
        </DrawerSection>

        {mode.kind === "edit" ? (
          <DrawerSection>
            <DrawerDanger
              title="Remove this library"
              description="Files stay on disk. Titles tracked under it are no longer managed by Deluno."
              action={
                <Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>
                  Remove…
                </Button>
              }
            />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Remove “${editing?.name ?? form.name}”?`}
        description="Deluno stops managing this library. Nothing is deleted from disk, and downloads already in your client are left alone."
        confirmLabel="Remove library"
        busy={busy}
        onConfirm={() => void handleRemove()}
      />

      <ConfirmDialog
        open={confirmDiscard || blocker.state === "blocked"}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
          if (blocker.state === "blocked") blocker.reset();
        }}
        title="Discard unsaved changes?"
        description="Your edits to this library haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          if (blocker.state === "blocked") {
            setMode({ kind: "closed" });
            blocker.proceed();
          } else {
            closeDrawer();
          }
        }}
      />
    </div>
  );
}

/* ---------------------------------------------------------------- utils */

function defaultName(mediaType: "movies" | "tv") {
  return mediaType === "movies" ? "Movies" : "TV Shows";
}

function defaultRoot(mediaType: "movies" | "tv", settings: PlatformSettingsSnapshot) {
  return (mediaType === "movies" ? settings.movieRootPath : settings.seriesRootPath) ?? "";
}

function emptyForm(mediaType: "movies" | "tv", settings: PlatformSettingsSnapshot): LibraryForm {
  return {
    name: defaultName(mediaType),
    mediaType,
    rootPath: defaultRoot(mediaType, settings),
    downloadsPath: settings.downloadsPath ?? "",
    planChoice: "",
    qualityProfileId: "",
    autoSearchEnabled: true
  };
}

function formFromLibrary(library: LibraryItem): LibraryForm {
  return {
    name: library.name,
    mediaType: library.mediaType === "tv" ? "tv" : "movies",
    rootPath: library.rootPath,
    downloadsPath: library.downloadsPath ?? "",
    planChoice: library.defaultPolicySetId ?? "",
    qualityProfileId: library.qualityProfileId ?? "",
    autoSearchEnabled: library.autoSearchEnabled
  };
}

function sameForm(a: LibraryForm, b: LibraryForm) {
  return (
    a.name === b.name &&
    a.mediaType === b.mediaType &&
    a.rootPath === b.rootPath &&
    a.downloadsPath === b.downloadsPath &&
    a.planChoice === b.planChoice &&
    a.qualityProfileId === b.qualityProfileId &&
    a.autoSearchEnabled === b.autoSearchEnabled
  );
}

function automationPayload(library: LibraryItem, autoSearchEnabled: boolean) {
  return {
    autoSearchEnabled,
    missingSearchEnabled: library.missingSearchEnabled,
    upgradeSearchEnabled: library.upgradeSearchEnabled,
    searchIntervalHours: library.searchIntervalHours,
    retryDelayHours: library.retryDelayHours,
    maxItemsPerRun: library.maxItemsPerRun,
    searchWindowStartHour: library.searchWindowStartHour,
    searchWindowEndHour: library.searchWindowEndHour
  };
}

async function putJson(url: string, body: unknown, failure: string) {
  const response = await authedFetch(url, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  if (!response.ok) throw new Error(failure);
  return response;
}

function starterChoiceValue(id: string) {
  return `${STARTER_VALUE_PREFIX}${id}`;
}

function getStarterFromChoice(value: string) {
  if (!value.startsWith(STARTER_VALUE_PREFIX)) return null;
  const id = value.slice(STARTER_VALUE_PREFIX.length);
  return MEDIA_PLAN_STARTERS.find((starter) => starter.id === id) ?? null;
}

function chooseStarterQualityProfile(starter: MediaPlanStarter, profiles: QualityProfileItem[]) {
  const candidates = profiles.filter((profile) => profile.mediaType === starter.values.mediaType);
  if (!candidates.length) return null;
  if (starter.id === "premium-4k") return candidates.find((profile) => matchesProfile(profile, ["4k", "2160"])) ?? candidates[0] ?? null;
  if (starter.values.mediaType === "tv") return candidates.find((profile) => matchesProfile(profile, ["hd tv", "1080"])) ?? candidates[0] ?? null;
  return candidates.find((profile) => matchesProfile(profile, ["standard", "1080"])) ?? candidates[0] ?? null;
}

function matchesProfile(profile: QualityProfileItem, needles: string[]) {
  const haystack = `${profile.name} ${profile.cutoffQuality} ${profile.allowedQualities}`.toLowerCase();
  return needles.some((needle) => haystack.includes(needle));
}

function formatRelative(iso: string) {
  const diff = new Date(iso).getTime() - Date.now();
  const abs = Math.abs(diff);
  const minutes = Math.round(abs / 60000);
  const label = minutes < 60 ? `${Math.max(minutes, 1)} min` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h` : `${Math.round(minutes / 1440)} d`;
  return diff >= 0 ? `in ${label}` : `${label} ago`;
}
