/**
 * Libraries — list → drawer.
 *
 *   PageToolbar (Media Management tabs · New library)
 *   ListCard  (name · folder · library profile · automation · status · on · ›)
 *   Drawer    (identity · destination · remove)
 *
 * Contracts: POST /api/libraries, PUT /api/libraries/{id} (name/folders),
 * PUT /api/libraries/{id}/automation, PUT /api/libraries/{id}/subtitles,
 * DELETE /api/libraries/{id}.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useLocation, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { Plus } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { MenuSelect } from "../components/ui/menu-select";
import { Select } from "../components/ui/select";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Switch } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { emptyPlatformSettingsSnapshot, fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem, type SubtitleLanguageOption, readErrorText } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { formatSearchInterval } from "../lib/search-interval-format";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { ExistingMediaImportDialog } from "../components/app/existing-media-import-dialog";
import { downloadsPathProblem } from "../lib/library-paths";
import { FolderChainSection } from "./connections/folder-chain-section";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

interface LoaderData {
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  qualityProfiles: QualityProfileItem[];
}

interface LibraryForm {
  name: string;
  mediaType: "movies" | "tv";
  rootPath: string;
  /** Where finished downloads for this library arrive. Empty means "use the download client's own folder". */
  downloadsPath: string;
  /** The quality profile that judges this library's titles. Empty only while creating, when the server picks the standard one. */
  qualityProfileId: string;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsLibrariesLoader(): Promise<LoaderData> {
  const [libraries, settings, qualityProfiles] = await Promise.all([
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => emptyPlatformSettingsSnapshot),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles")
  ]);
  // The subtitle language list is not fetched here any more: this screen no
  // longer asks about subtitles, and a request nobody reads is a request.
  return { libraries, settings, qualityProfiles };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["LibraryChanged", "SettingsChanged", "QualityProfileChanged"] };

export function SettingsLibrariesPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { libraries, settings, qualityProfiles } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const location = useLocation();
  const navigate = useNavigate();

  /* ------------------------------------------------------------ list */
  const [togglingId, setTogglingId] = useState<string | null>(null);
  const setupTabs = useMemo(
    () =>
      librarySetupNavItems.map((tab) =>
        tab.to === "/settings/libraries" ? { ...tab, status: libraries.length > 0 ? ("complete" as const) : ("pending" as const) } : tab
      ),
    [libraries.length]
  );
  const sortedLibraries = useMemo(() => [...libraries].sort((a, b) => a.name.localeCompare(b.name)), [libraries]);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<LibraryForm>(() => emptyForm("movies", settings));
  const [initialForm, setInitialForm] = useState<LibraryForm>(() => emptyForm("movies", settings));
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<{ name?: string; rootPath?: string; downloadsPath?: string }>({});
  const [folderCheckKey, setFolderCheckKey] = useState(0);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);
  const [existingImportLibraryId, setExistingImportLibraryId] = useState<string | null>(null);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? libraries.find((library) => library.id === mode.id) ?? null : null;
  const existingImportLibrary = existingImportLibraryId ? libraries.find((library) => library.id === existingImportLibraryId) ?? null : null;
  const dirty = useMemo(() => isOpen && !sameForm(form, initialForm), [isOpen, form, initialForm]);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  useEffect(() => {
    const libraryId = new URLSearchParams(location.search).get("libraryId");
    if (!libraryId || mode.kind !== "closed") return;
    const library = libraries.find((item) => item.id === libraryId);
    if (!library) return;
    const next = formFromLibrary(library);
    setMode({ kind: "edit", id: library.id });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
    setErrors({});
    navigate("/settings/libraries", { replace: true });
  }, [libraries, location.search, mode.kind, navigate]);

  function openCreate() {
    const next = emptyForm("movies", settings);
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
    setErrors({});
  }

  function openEdit(library: LibraryItem) {
    const next = formFromLibrary(library);
    setMode({ kind: "edit", id: library.id });
    setForm(next);
    setInitialForm(next);
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
      qualityProfileId: "",
      name: current.name,
      rootPath: current.rootPath.trim() && current.rootPath !== defaultRoot(current.mediaType, settings) ? current.rootPath : defaultRoot(mediaType, settings)
    }));
  }

  /* ---------------------------------------------------------- saving */
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    const creating = mode.kind === "create";
    const nextErrors: typeof errors = {};
    if (!form.name.trim()) nextErrors.name = "Give this library a name.";
    if (!form.rootPath.trim()) nextErrors.rootPath = "Choose a folder for this library.";
    const downloadsProblem = downloadsPathProblem(form.downloadsPath);
    if (downloadsProblem) nextErrors.downloadsPath = downloadsProblem;
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) return;

    setBusy(true);
    setSaveState("saving");
    const submitted = form;
    try {
      let library: LibraryItem;
      if (creating) {
        const response = await authedFetch("/api/libraries", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            name: form.name.trim(),
            mediaType: form.mediaType,
            purpose: "Main library",
            rootPath: form.rootPath.trim(),
            downloadsPath: form.downloadsPath.trim() || null,
            qualityProfileId: form.qualityProfileId || null,
            // New libraries stay idle until their search behaviour is attached
            // explicitly from Automation & Recovery.
            autoSearchEnabled: false,
            missingSearchEnabled: false,
            upgradeSearchEnabled: false,
            searchIntervalMinutes: 360,
            retryDelayHours: 6,
            maxItemsPerRun: 10
          })
        });
        if (!response.ok) throw new Error(await readErrorText(response, "Library could not be created."));
        library = (await response.json()) as LibraryItem;
      } else {
        const id = mode.id;
        const before = initialForm;
        if (form.name !== before.name || form.rootPath !== before.rootPath || form.downloadsPath !== before.downloadsPath) {
          // The downloads folder is always sent: the server writes exactly what
          // it is given, so leaving it out would clear it.
          await putJson(
            `/api/libraries/${id}`,
            { name: form.name.trim(), rootPath: form.rootPath.trim(), downloadsPath: form.downloadsPath.trim() || null },
            "Library details could not be saved."
          );
        }
        // Another profile re-judges every title that follows this library, so a raised
        // cutoff turns the files below it into upgrades straight away (#1169).
        if (form.qualityProfileId && form.qualityProfileId !== before.qualityProfileId) {
          await putJson(`/api/libraries/${id}/quality-profile`, { qualityProfileId: form.qualityProfileId }, "The quality profile could not be changed.");
        }
        // Subtitles are not written from here any more. They are one screen for
        // every library — Subtitles → Languages — and a second write path for
        // the same setting is the shape this codebase keeps paying for, whether
        // or not anything on this form can currently reach it.
        library = editing!;
      }

      // Re-read so server-side normalisation shows in the drawer.
      const fresh = (await fetchJson<LibraryItem[]>("/api/libraries")).find((item) => item.id === library.id) ?? library;
      const settled = formFromLibrary(fresh);
      // A field the person kept typing in while this save ran keeps what they typed; it stays a change to save.
      setForm((current) => settleFormAfterSave(current, submitted, settled));
      setInitialForm(settled);
      if (creating) {
        toast.success(`${settled.name} library created`);
        closeDrawer();
      } else {
        setSaveState("saved");
        setSaveMessage("Saved just now");
        // The folders just changed, so the folder check is asked again.
        setFolderCheckKey((value) => value + 1);
      }
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
      await putJson(`/api/libraries/${library.id}/automation`, automationPayload(library, { ...library, autoSearchEnabled: enabled }), `Could not ${enabled ? "resume" : "pause"} searching for ${library.name}.`);
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
    <div className="grid gap-(--page-gap)">
      <PageToolbar tabs={setupTabs} actions={<PageToolbarAction onClick={openCreate}>New library</PageToolbarAction>} />


      <ListCard
        title={libraries.length ? `${libraries.length} ${libraries.length === 1 ? "library" : "libraries"}` : "0 libraries"}
        titleCase={false}
      >
        {libraries.length === 0 ? (
          <ListEmpty
            title="No libraries yet"
            description="A library tells Deluno whether it manages movies or TV and where the finished files live. Add its Library Profile from the Library Profiles page."
            actions={
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus className="h-3.5 w-3.5" />
                New library
              </Button>
            }
          />
        ) : (
          <ListTable
            listId="libraries"
            items={sortedLibraries}
            columns={[
              { id: "name", label: "Name", sort: (library) => library.name.toLowerCase() },
              { id: "folder", label: "Folder", sort: (library) => library.rootPath.toLowerCase() },
              { id: "profile", label: "Library Profile", sort: (library) => (library.defaultPolicySetName ?? qualityProfiles.find((item) => item.id === library.qualityProfileId)?.name ?? "").toLowerCase() },
              { id: "schedule", label: "Search schedule", sort: (library) => (library.missingSearchEnabled || library.upgradeSearchEnabled ? library.searchIntervalMinutes : null) },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (library) => (!(library.missingSearchEnabled || library.upgradeSearchEnabled) ? 0 : library.automationStatus === "running" ? 2 : library.autoSearchEnabled ? 3 : 1) },
              { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (library) => library.autoSearchEnabled }
            ]}
          >
            {(rows) => rows.map((library) => {
              const profile = qualityProfiles.find((item) => item.id === library.qualityProfileId);
              const running = library.automationStatus === "running";
              const searchKinds = [library.missingSearchEnabled ? "Missing" : null, library.upgradeSearchEnabled ? "Upgrades" : null].filter(Boolean).join(" · ");
              const hasSearchSelection = Boolean(searchKinds);
              const scheduled = library.autoSearchEnabled && hasSearchSelection;
              const tone = !scheduled ? "idle" : running ? "info" : "idle";
              const status = !hasSearchSelection ? "Not configured" : running ? "Searching" : library.autoSearchEnabled ? "Scheduled" : "Paused";
              return (
                <ListRow key={library.id} onClick={() => openEdit(library)} selected={mode.kind === "edit" && mode.id === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell mono primary={library.rootPath} secondary={library.downloadsPath ? <>Advanced source: <span className="font-mono">{library.downloadsPath}</span></> : "Uses the download client's reported folder"} />
                  <ListCell
                    primary={library.defaultPolicySetName ?? (profile ? <span>Direct quality profile: {profile.name}</span> : <span className="text-muted-foreground">No profile assigned</span>)}
                    secondary={library.defaultPolicySetName ? profile ? `${profile.name} · stops at ${profile.cutoffQuality}` : "Quality set by the Library Profile" : profile ? `Stops at ${profile.cutoffQuality}` : "Add a Library Profile before automated searching"}
                  />
                  <ListCell
                    primary={hasSearchSelection ? <span className="text-foreground">{searchKinds}</span> : <span className="text-muted-foreground">No searches selected</span>}
                    secondary={
                      hasSearchSelection ? (
                        <>
                          {library.autoSearchEnabled ? formatSearchInterval(library.searchIntervalMinutes) : "Paused"} · <Link to={`/search-cycles?libraryId=${encodeURIComponent(library.id)}`} onClick={(event) => event.stopPropagation()} className="text-info hover:underline">Manage</Link>
                        </>
                      ) : (
                        <Link to={`/search-cycles?libraryId=${encodeURIComponent(library.id)}`} onClick={(event) => event.stopPropagation()} className="text-info hover:underline">Configure searches</Link>
                      )
                    }
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
        title={<span className="min-w-0 truncate">{drawerTitle}</span>}
        description={drawerDescription}
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={mode.kind === "create" ? "Create library" : "Save library"}
            onCancel={requestClose}
            saveEnabled={mode.kind === "create" ? true : undefined}
            disabled={busy}
          />
        }
      >
        <div className="grid gap-0 py-1">
          <section className="grid gap-(--grid-gap) border-b border-hairline py-6 first:pt-4 sm:grid-cols-[minmax(180px,0.7fr)_minmax(0,1.3fr)]">
            <div className="grid content-start gap-1">
              <h3 className="text-(length:--type-body-sm) font-semibold text-foreground">What is this library?</h3>
              <p className="text-(length:--type-caption) leading-relaxed text-muted-foreground">Give this library a name and tell Deluno whether it contains movies or TV shows.</p>
            </div>
            <FieldRow>
              <Field label="Library name" error={errors.name}>
                <Input
                  value={form.name}
                  onChange={(event) => {
                    setErrors((current) => ({ ...current, name: undefined }));
                    setForm((current) => ({ ...current, name: event.target.value }));
                  }}
                  placeholder="Enter a library name"
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
          </section>

          <section className="border-b border-hairline py-6">
            <div className="grid max-w-2xl gap-1">
              <h3 className="text-(length:--type-body-sm) font-semibold text-foreground">Where do the files go?</h3>
              <p className="text-(length:--type-caption) leading-relaxed text-muted-foreground">Choose where Deluno puts the clean, imported files people see in this library.</p>
            </div>
            <div className="mt-4 grid gap-(--grid-gap)">
              <Field label="Library folder" help="This is where imported files end up." error={errors.rootPath}>
                <PathInput
                  value={form.rootPath}
                  onChange={(rootPath) => {
                    setErrors((current) => ({ ...current, rootPath: undefined }));
                    setForm((current) => ({ ...current, rootPath }));
                  }}
                  browseTitle={`Choose ${typeLabel.toLowerCase()} library folder`}
                  showAdvanced={false}
                  stacked
                />
              </Field>
              <Field
                label="Downloads folder"
                help="Where finished downloads for this library arrive; your download clients and any processor must use this folder. Leave it empty to use the folder each download client reports."
                error={errors.downloadsPath}
              >
                <PathInput
                  value={form.downloadsPath}
                  onChange={(downloadsPath) => {
                    setErrors((current) => ({ ...current, downloadsPath: undefined }));
                    setForm((current) => ({ ...current, downloadsPath }));
                  }}
                  browseTitle={`Choose ${typeLabel.toLowerCase()} downloads folder`}
                  showAdvanced={false}
                  stacked
                />
              </Field>
            </div>
          </section>

          <section className="grid gap-(--grid-gap) border-b border-hairline py-6 sm:grid-cols-[minmax(180px,0.7fr)_minmax(0,1.3fr)]">
            <div className="grid content-start gap-1">
              <h3 className="text-(length:--type-body-sm) font-semibold text-foreground">What quality do you want?</h3>
              <p className="text-(length:--type-caption) leading-relaxed text-muted-foreground">The quality profile decides which releases are good enough and when a file is worth upgrading.</p>
            </div>
            <FieldRow>
              <Field
                label="Quality profile"
                help={mode.kind === "edit" ? "Changing it rechecks every title here that follows the library's profile. A title with a profile of its own keeps it." : "You can change this later."}
              >
                <Select
                  aria-label="Quality profile"
                  value={form.qualityProfileId}
                  onChange={(event) => setForm((current) => ({ ...current, qualityProfileId: event.target.value }))}
                  options={[
                    ...(mode.kind === "create" ? [{ value: "", label: "Standard (recommended)" }] : form.qualityProfileId ? [] : [{ value: "", label: "Choose a profile" }]),
                    ...qualityProfiles
                      .filter((profile) => profile.mediaType === form.mediaType)
                      .map((profile) => ({ value: profile.id, label: profile.name }))
                  ]}
                />
              </Field>
            </FieldRow>
          </section>

          {/*
            "Which subtitles?" used to be here.

            It is one screen for every library now — Subtitles → Languages —
            because the settings are per library and the comparison somebody
            actually wants to make is *across* libraries: "English on everything,
            Japanese on anime" was something you could only work out by opening
            two forms and remembering the first. Leaving a copy here as well
            would be the same setting in two places, which is the defect this
            codebase keeps paying for.
          */}
          {mode.kind === "edit" && editing ? (
            <section className="border-b border-hairline py-6">
              <FolderChainSection libraryId={editing.id} refreshKey={folderCheckKey} />
            </section>
          ) : null}
          {mode.kind === "edit" && editing ? (
            <section className="grid gap-3 border-b border-hairline py-6">
              <div>
                <h3 className="text-(length:--type-body-sm) font-semibold text-foreground">Already have files here?</h3>
                <p className="mt-1 max-w-2xl text-(length:--type-caption) leading-relaxed text-muted-foreground">Review what Deluno finds in this folder, then select only the movies or TV shows you want to add. Deluno will not guess or import anything from this screen without your selection.</p>
              </div>
              <div>
                <Button type="button" variant="outline" size="sm" onClick={() => setExistingImportLibraryId(editing.id)} disabled={dirty || busy}>
                  Review existing files
                </Button>
                {dirty ? <p className="mt-2 text-(length:--type-caption) text-warning">Save the library folder before reviewing files.</p> : null}
              </div>
            </section>
          ) : null}

          {mode.kind === "edit" ? (
            <section className="py-6">
              <DrawerDanger
                title="Remove this library"
                description="Files stay on disk. Titles tracked under it are no longer managed by Deluno."
                action={
                  <Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>
                    Remove…
                  </Button>
                }
              />
            </section>
          ) : null}
        </div>
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
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this library haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />

      <ExistingMediaImportDialog
        open={existingImportLibrary !== null}
        library={existingImportLibrary}
        onOpenChange={(open) => {
          if (!open) setExistingImportLibraryId(null);
        }}
        onImported={() => revalidator.revalidate()}
      />
    </div>
  );
}

/* ---------------------------------------------------------------- utils */

function defaultRoot(mediaType: "movies" | "tv", settings: PlatformSettingsSnapshot) {
  return (mediaType === "movies" ? settings.movieRootPath : settings.seriesRootPath) ?? "";
}

function emptyForm(mediaType: "movies" | "tv", settings: PlatformSettingsSnapshot): LibraryForm {
  return {
    name: "",
    mediaType,
    rootPath: defaultRoot(mediaType, settings),
    downloadsPath: "",
    qualityProfileId: "",
  };
}

function formFromLibrary(library: LibraryItem): LibraryForm {
  return {
    name: library.name,
    mediaType: library.mediaType === "tv" ? "tv" : "movies",
    rootPath: library.rootPath,
    downloadsPath: library.downloadsPath ?? "",
    qualityProfileId: library.qualityProfileId ?? "",
  };
}

function sameForm(a: LibraryForm, b: LibraryForm) {
  return (
    a.name === b.name &&
    a.mediaType === b.mediaType &&
    a.rootPath === b.rootPath &&
    a.downloadsPath === b.downloadsPath &&
    a.qualityProfileId === b.qualityProfileId
  );
}

/**
 * Order counts, so this is a sequence comparison and not a set one. The mode is
 * only part of the answer when there is more than one language to order — with
 * one, the two modes do the same thing, and treating a stale mode as a change
 * would mark the drawer dirty for a setting nobody can see.
 */

function automationPayload(library: LibraryItem, settings: Pick<LibraryItem, "autoSearchEnabled" | "missingSearchEnabled" | "upgradeSearchEnabled">) {
  return {
    autoSearchEnabled: settings.autoSearchEnabled,
    missingSearchEnabled: settings.missingSearchEnabled,
    upgradeSearchEnabled: settings.upgradeSearchEnabled,
    searchIntervalMinutes: library.searchIntervalMinutes,
    retryDelayHours: library.retryDelayHours,
    maxItemsPerRun: library.maxItemsPerRun,
    searchWindowStartHour: library.searchWindowStartHour,
    searchWindowEndHour: library.searchWindowEndHour
  };
}

async function putJson(url: string, body: unknown, failure: string) {
  const response = await authedFetch(url, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  if (!response.ok) throw new Error(await readErrorText(response, failure));
  return response;
}
