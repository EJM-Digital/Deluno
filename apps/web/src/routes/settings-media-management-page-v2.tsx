/**
 * Media Management: Media Naming, Import Policy, and Processing Workflow — one module.
 *
 *   File handling  → page form: Folder naming · PageFooter
 *   Processing     → ListCard of libraries → drawer (workflow per library),
 *                    plus optional completion callbacks as their own list.
 *
 * Contracts: PATCH /api/settings, PUT /api/libraries/{id}/workflow,
 * GET/POST/DELETE /api/integrations/processors/connections (+ /{id}/test).
 */
import { IMPORT_WORKFLOW_HELP, IMPORT_WORKFLOW_LABELS, importWorkflowLabel } from "../lib/import-workflow";
import { toneSortValue } from "../lib/status-tones";
import { SecretUnreadableNotice } from "../components/app/secret-unreadable-notice";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { PageLink } from "../components/app/page-link";
import { Link, useLoaderData, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import { ConfigAreas } from "../lib/config-areas";
import { useFollowLoaded } from "../hooks/use-follow-loaded";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { Loader2, Plus, Trash2, Wifi } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { ConnectionVersionBeside } from "../components/app/connection-version";
import { ConnectionUpdatePanel } from "../components/app/connection-update-panel";
import { useConnectionVersions, versionKey } from "../lib/connection-versions";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Textarea } from "../components/ui/textarea";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { NamingFormatField, NamingPatternEditor, previewNamingFormat, sanitizeIllegalCharacters, type ColonReplacementMode, type NamingFormatKind } from "../components/app/naming-format-field";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { DetectedComponents, type BundledComponentStatus } from "../components/app/bundled-components-detected";
import { toast } from "../components/shell/toaster";
import { settingsOverviewLoader } from "./settings-overview-page";
import { readErrorText, fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type ProcessorConnectionItem, type ProcessorConnectionTestResult, type ProcessorPathMappingItem, type QualityProfileItem } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

/** Mirrors `ProcessorStallLimits`: a day to a week with no progress, two days unless chosen (#511). */
const TIMEOUT_OPTIONS = [
  { value: "1440", label: "24 hours" },
  { value: "2880", label: "48 hours" },
  { value: "4320", label: "3 days" },
  { value: "5760", label: "4 days" },
  { value: "10080", label: "7 days" }
];
const DEFAULT_STALL_MINUTES = 2880;
/** #759: matches ColonReplacementModes on the server. */
const COLON_REPLACEMENT_OPTIONS = [
  { value: "smart", label: "Smart Replace (recommended)" },
  { value: "delete", label: "Delete" },
  { value: "dash", label: "Dash" },
  { value: "space-dash", label: "Space Dash" },
  { value: "space-dash-space", label: "Space Dash Space" }
];
/** #763: matches Deluno.Series.Contracts.MultiEpisodeStyles on the server. */
const MULTI_EPISODE_STYLE_OPTIONS = [
  { value: "extend", label: "Extend" },
  { value: "duplicate", label: "Duplicate" },
  { value: "repeat", label: "Repeat" },
  { value: "scene", label: "Scene" },
  { value: "range", label: "Range (recommended)" },
  { value: "prefixed-range", label: "Prefixed Range" }
];
const FAILURE_OPTIONS = [
  { value: "block", label: "Stop and ask me" },
  { value: "manual-review", label: "Send to manual review" },
  { value: "import-original", label: "Import the original file" }
];
interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
  processorConnections: ProcessorConnectionItem[];
}

export async function settingsMediaManagementLoader(): Promise<LoaderData> {
  const [overview, processorConnections] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections")
  ]);
  return { ...overview, processorConnections };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["SettingsChanged", "LibraryChanged"], areas: [ConfigAreas.processors] };

export function SettingsMediaManagementPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const location = useLocation();
  if (location.pathname.startsWith("/settings/import-policy")) return <ImportPolicyPage />;
  return location.pathname.startsWith("/settings/processing") ? <ProcessingWorkflowPage /> : <FileHandlingPage />;
}

/* ==================================================== file handling */

interface CustomPatternDrawerState {
  kind: NamingFormatKind;
  label: string;
  placeholder: string;
  value: string;
  previousValue: string;
}

function customPatternMeta(kind: NamingFormatKind) {
  if (kind === "movie-folder") return { label: "Movie folders", placeholder: "{Movie Title} ({Release Year})" };
  if (kind === "series-folder") return { label: "TV show folders", placeholder: "{Series Title} ({Series Year})" };
  if (kind === "movie-file") return { label: "Movie files", placeholder: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]" };
  if (kind === "episode-file") return { label: "Episode files", placeholder: "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}" };
  if (kind === "daily-episode-file") return { label: "Daily episode files", placeholder: "{Series Title} - {Air-Date} - {Episode Title}" };
  if (kind === "anime-episode-file") return { label: "Anime episode files", placeholder: "{Series Title} - {absolute:000} - {Episode Title}" };
  return { label: "Custom format", placeholder: "{Title}" };
}

function updateCustomPattern(settings: PlatformSettingsSnapshot, kind: NamingFormatKind, value: string) {
  if (kind === "movie-folder") return { ...settings, movieFolderFormat: value };
  if (kind === "series-folder") return { ...settings, seriesFolderFormat: value };
  if (kind === "movie-file") return { ...settings, movieFileFormat: value };
  if (kind === "episode-file") return { ...settings, episodeFileFormat: value };
  if (kind === "daily-episode-file") return { ...settings, dailyEpisodeFileFormat: value };
  if (kind === "anime-episode-file") return { ...settings, animeEpisodeFileFormat: value };
  return settings;
}

function customPatternPreview(kind: NamingFormatKind, value: string, placeholder: string) {
  const preview = previewNamingFormat(value || placeholder);
  if (kind === "movie-folder") return `Movies\\${preview}`;
  if (kind === "series-folder") return `TV Shows\\${preview}`;
  return preview;
}

function FileHandlingPage() {
  const { settings } = useLoaderData() as LoaderData;
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [saved, setSaved] = useState(settings);
  const [form, setForm] = useState(settings);
  const [customDrawer, setCustomDrawer] = useState<CustomPatternDrawerState | null>(null);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);
  // #762: a season-subfolder layout is opt-in. Blank formats already mean "no
  // subfolder" on the server, so this switch is local UI state that shows or
  // hides the two fields rather than a persisted setting of its own.
  const [seasonSubfoldersEnabled, setSeasonSubfoldersEnabled] = useState(
    () => Boolean(settings.seasonFolderFormat.trim() || settings.specialsFolderFormat.trim())
  );

  const dirty = useMemo(() => JSON.stringify(form) !== JSON.stringify(saved), [form, saved]);
  const state: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  const customDrawerDirty = customDrawer !== null && customDrawer.value !== customDrawer.previousValue;
  useUnsavedChanges(dirty);
  useFollowLoaded(settings, dirty, (next) => { setSaved(next); setForm(next); setSeasonSubfoldersEnabled(Boolean(next.seasonFolderFormat.trim() || next.specialsFolderFormat.trim())); });
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  function handleCustomMode(kind: NamingFormatKind, active: boolean, draftValue = "", previousValue = "") {
    if (!active) return;
    const meta = customPatternMeta(kind);
    setCustomDrawer({ kind, ...meta, value: draftValue, previousValue });
  }

  function closeCustomDrawer(apply: boolean) {
    if (!customDrawer) return;
    setForm((current) => updateCustomPattern(current, customDrawer.kind, apply ? customDrawer.value : customDrawer.previousValue));
    setCustomDrawer(null);
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (state === "saving") return;
    setSaveState("saving");
    try {
      const nextSettings = await settingsMutation.mutate({
        movieFolderFormat: form.movieFolderFormat,
        seriesFolderFormat: form.seriesFolderFormat,
        movieFileFormat: form.movieFileFormat,
        episodeFileFormat: form.episodeFileFormat,
        dailyEpisodeFileFormat: form.dailyEpisodeFileFormat,
        animeEpisodeFileFormat: form.animeEpisodeFileFormat,
        renameOnImport: form.renameOnImport,
        useHardlinks: form.useHardlinks,
        replaceIllegalCharacters: form.replaceIllegalCharacters,
        colonReplacementMode: form.colonReplacementMode,
        multiEpisodeStyle: form.multiEpisodeStyle,
        seasonFolderFormat: seasonSubfoldersEnabled ? form.seasonFolderFormat : "",
        specialsFolderFormat: seasonSubfoldersEnabled ? form.specialsFolderFormat : ""
      });
      setSaved(nextSettings);
      setForm(nextSettings);
      setSaveState("saved");
      setMessage("Saved just now");
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-(--page-gap)" noValidate>
      <PageToolbar tabs={librarySetupNavItems} />

      <ListCard title="Naming" count="The titles people see in your library">
        <div className="grid md:grid-cols-[minmax(0,1.45fr)_minmax(22rem,0.8fr)]">
          <div className="divide-y divide-hairline">
            <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
              <div className="grid content-start gap-1.5">
                <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Movie folders</p>
                <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Used when Deluno creates or renames a movie folder.</p>
              </div>
              <NamingFormatField kind="movie-folder" value={form.movieFolderFormat} onChange={(value) => setForm((current) => ({ ...current, movieFolderFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("movie-folder", active, draftValue, previousValue)} placeholder="{Movie Title} ({Release Year})" showExamples={false} />
            </div>
            <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
              <div className="grid content-start gap-1.5">
                <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Movie files</p>
                {/*
                  A movie had no file-name pattern at all, so every import was
                  named "Title (Year)" and the quality and the guide formats
                  Deluno matched were lost the moment the file landed.
                */}
                <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Used when Deluno renames an imported movie. Quality and custom formats come from the release Deluno chose, not from the old file name.</p>
              </div>
              <NamingFormatField kind="movie-file" value={form.movieFileFormat} onChange={(value) => setForm((current) => ({ ...current, movieFileFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("movie-file", active, draftValue, previousValue)} placeholder="{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]" showExamples={false} />
            </div>
            <div className="grid gap-(--grid-gap) p-(--card-pad-x) md:grid-cols-2">
              <div className="grid content-start gap-(--grid-gap)">
                <div className="grid content-start gap-1.5">
                  <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">TV show folders</p>
                  <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Used when Deluno creates or renames a show folder.</p>
                </div>
                <NamingFormatField kind="series-folder" value={form.seriesFolderFormat} onChange={(value) => setForm((current) => ({ ...current, seriesFolderFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("series-folder", active, draftValue, previousValue)} placeholder="{Series Title} ({Series Year})" showExamples={false} />
              </div>
              <div className="grid content-start gap-(--grid-gap)">
                <div className="grid content-start gap-1.5">
                  <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Episode files</p>
                  <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Used when Deluno renames imported episode files.</p>
                </div>
                <NamingFormatField kind="episode-file" value={form.episodeFileFormat} onChange={(value) => setForm((current) => ({ ...current, episodeFileFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("episode-file", active, draftValue, previousValue)} placeholder="{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}" showExamples={false} />
              </div>
            </div>
            <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
              <Field label="Multi-episode style" help="How a file that covers more than one episode (a double episode, a combined premiere) gets its combined season/episode name.">
                <Select
                  value={form.multiEpisodeStyle}
                  onChange={(event) => setForm((current) => ({ ...current, multiEpisodeStyle: event.target.value }))}
                  options={MULTI_EPISODE_STYLE_OPTIONS}
                />
              </Field>
            </div>
            <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
              <SwitchRow
                label="Split shows into season subfolders"
                description="Adds a Season 01 folder (and a separate one for specials) inside each show's folder instead of one flat show folder."
                checked={seasonSubfoldersEnabled}
                onCheckedChange={(checked) => {
                  setSeasonSubfoldersEnabled(checked);
                  if (!checked) setForm((current) => ({ ...current, seasonFolderFormat: "", specialsFolderFormat: "" }));
                  else
                    setForm((current) => ({
                      ...current,
                      seasonFolderFormat: current.seasonFolderFormat.trim() || "Season {Season:00}",
                      specialsFolderFormat: current.specialsFolderFormat.trim() || "Specials"
                    }));
                }}
              />
              {seasonSubfoldersEnabled ? (
                <div className="grid gap-(--grid-gap) md:grid-cols-2">
                  <Field label="Season folder" help="Tokens: {Season:00} for a zero-padded number, {Season} for a plain one.">
                    <Input value={form.seasonFolderFormat} onChange={(event) => setForm((current) => ({ ...current, seasonFolderFormat: event.target.value }))} placeholder="Season {Season:00}" />
                  </Field>
                  <Field label="Specials folder" help="Used for season 0 instead of the season format above.">
                    <Input value={form.specialsFolderFormat} onChange={(event) => setForm((current) => ({ ...current, specialsFolderFormat: event.target.value }))} placeholder="Specials" />
                  </Field>
                </div>
              ) : null}
            </div>
            <div className="grid gap-(--grid-gap) p-(--card-pad-x) md:grid-cols-2">
              <div className="grid content-start gap-(--grid-gap)">
                <div className="grid content-start gap-1.5">
                  <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Daily episode files</p>
                  <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Used for dated shows (news, talk shows) instead of the episode format above.</p>
                </div>
                <NamingFormatField kind="daily-episode-file" value={form.dailyEpisodeFileFormat} onChange={(value) => setForm((current) => ({ ...current, dailyEpisodeFileFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("daily-episode-file", active, draftValue, previousValue)} placeholder="{Series Title} - {Air-Date} - {Episode Title}" showExamples={false} />
              </div>
              <div className="grid content-start gap-(--grid-gap)">
                <div className="grid content-start gap-1.5">
                  <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Anime episode files</p>
                  <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Used for absolute-numbered shows instead of the episode format above.</p>
                </div>
                <NamingFormatField kind="anime-episode-file" value={form.animeEpisodeFileFormat} onChange={(value) => setForm((current) => ({ ...current, animeEpisodeFileFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("anime-episode-file", active, draftValue, previousValue)} placeholder="{Series Title} - {absolute:000} - {Episode Title}" showExamples={false} />
              </div>
            </div>
            <div className="grid gap-(--grid-gap) p-(--card-pad-x) md:grid-cols-2">
              <SwitchRow
                label="Replace illegal characters"
                description="On, a character a file system can't store (such as : or ?) becomes a dash. Off, Deluno removes it instead."
                checked={form.replaceIllegalCharacters}
                onCheckedChange={(checked) => setForm((current) => ({ ...current, replaceIllegalCharacters: checked }))}
              />
              {form.replaceIllegalCharacters ? (
                <Field label="Colon replacement" help="How a colon in a title is handled specifically. Every other illegal character keeps the dash above.">
                  <Select
                    value={form.colonReplacementMode}
                    onChange={(event) => setForm((current) => ({ ...current, colonReplacementMode: event.target.value }))}
                    options={COLON_REPLACEMENT_OPTIONS}
                  />
                </Field>
              ) : null}
            </div>
          </div>
          <aside className="border-t border-hairline bg-surface-1/20 p-(--card-pad-x) lg:border-l lg:border-t-0">
            <div className="grid gap-(--grid-gap)">
              <div className="grid gap-1">
                <p className="text-(length:--type-body-sm) font-semibold leading-tight text-foreground">Live preview</p>
                <p className="text-(length:--type-caption) leading-snug text-muted-foreground">See how Deluno will name new and imported media.</p>
              </div>
              <div className="grid gap-(--grid-gap)">
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview label="Movie folder" value={`Movies\\${previewNamingFormat(form.movieFolderFormat || "{Movie Title} ({Release Year})")}`} />
                </div>
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview
                    label="TV show folder"
                    value={
                      seasonSubfoldersEnabled
                        ? `TV Shows\\${previewNamingFormat(form.seriesFolderFormat || "{Series Title} ({Series Year})")}\\${previewSeasonFormat(form.seasonFolderFormat || "Season {Season:00}")}`
                        : `TV Shows\\${previewNamingFormat(form.seriesFolderFormat || "{Series Title} ({Series Year})")}`
                    }
                  />
                </div>
                {seasonSubfoldersEnabled ? (
                  <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                    <NamingPreview
                      label="Specials folder"
                      value={`TV Shows\\${previewNamingFormat(form.seriesFolderFormat || "{Series Title} ({Series Year})")}\\${previewSeasonFormat(form.specialsFolderFormat || "Specials", 0)}`}
                    />
                  </div>
                ) : null}
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview label="Movie file" value={previewNamingFormat(form.movieFileFormat || "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]")} />
                </div>
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview label="Episode file" value={previewNamingFormat(form.episodeFileFormat || "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}")} />
                </div>
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview label="Multi-episode file" value={previewMultiEpisodeFormat(form.episodeFileFormat || "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}", form.multiEpisodeStyle)} />
                </div>
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview label="Daily episode file" value={previewNamingFormat(form.dailyEpisodeFileFormat || "{Series Title} - {Air-Date} - {Episode Title}")} />
                </div>
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview label="Anime episode file" value={previewNamingFormat(form.animeEpisodeFileFormat || "{Series Title} - {absolute:000} - {Episode Title}")} />
                </div>
                <div className="grid gap-(--grid-gap) border-b border-hairline pb-(--grid-gap) last:border-b-0 last:pb-0">
                  <NamingPreview label="Illegal characters" value={sanitizeIllegalCharacters("Show? Special Edition", form.replaceIllegalCharacters)} />
                </div>
                <div className="grid gap-(--grid-gap) pb-(--grid-gap) last:pb-0">
                  <NamingPreview label="Colon replacement" value={sanitizeIllegalCharacters("Series: Title", form.replaceIllegalCharacters, form.colonReplacementMode as ColonReplacementMode)} />
                </div>
              </div>
            </div>
          </aside>
        </div>
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save changes" />

      <Drawer
        open={customDrawer !== null}
        onOpenChange={(open) => {
          if (!open) closeCustomDrawer(false);
        }}
        title={customDrawer ? `Custom ${customDrawer.label.toLowerCase()} pattern` : "Custom pattern"}
        description="Build a naming format with Deluno's tokens, then preview the result before applying it."
        footer={customDrawer ? <DrawerFooter state={customDrawerDirty ? "dirty" : "clean"} saveLabel="Apply pattern" saveType="button" onSave={() => closeCustomDrawer(true)} onCancel={() => closeCustomDrawer(false)} /> : null}
      >
        {customDrawer ? (
          <>
            <DrawerSection title="Format" aside={customDrawer.label}>
              <NamingPatternEditor kind={customDrawer.kind} value={customDrawer.value} onChange={(value) => setCustomDrawer((current) => (current ? { ...current, value } : current))} placeholder={customDrawer.placeholder} />
            </DrawerSection>
            <DrawerSection title="Preview">
              <NamingPreview label={customDrawer.label} value={customPatternPreview(customDrawer.kind, customDrawer.value, customDrawer.placeholder)} />
            </DrawerSection>
          </>
        ) : null}
      </Drawer>
    </form>
  );
}

function ImportPolicyPage() {
  const { settings } = useLoaderData() as LoaderData;
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [saved, setSaved] = useState(settings);
  const [form, setForm] = useState(settings);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

  const dirty = useMemo(() => JSON.stringify(form) !== JSON.stringify(saved), [form, saved]);
  const state: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useFollowLoaded(settings, dirty, (next) => { setSaved(next); setForm(next); });
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (state === "saving") return;
    setSaveState("saving");
    const thresholdError = libraryFreeSpaceThresholdError(form);
    if (thresholdError) {
      setSaveState("error");
      setMessage(thresholdError);
      return;
    }
    try {
      const nextSettings = await settingsMutation.mutate({
        renameOnImport: form.renameOnImport,
        renameFoldersOnImport: form.renameFoldersOnImport,
        useHardlinks: form.useHardlinks,
        libraryFreeSpaceWarnPercent: form.libraryFreeSpaceWarnPercent,
        libraryFreeSpacePausePercent: form.libraryFreeSpacePausePercent,
        minimumFreeSpaceMb: form.minimumFreeSpaceMb,
        skipFreeSpaceCheck: form.skipFreeSpaceCheck,
        removeAndDeletePackedArchivesFromClient: form.removeAndDeletePackedArchivesFromClient,
        setPermissionsOnImport: form.setPermissionsOnImport,
        folderChmod: form.folderChmod,
        chownGroup: form.chownGroup,
        leftoverDownloadCleanupEnabled: form.leftoverDownloadCleanupEnabled,
        leftoverDownloadCleanupAgeHours: form.leftoverDownloadCleanupAgeHours,
        leftoverExcludePatterns: form.leftoverExcludePatterns,
        leftoverMinAgeHours: form.leftoverMinAgeHours,
        forceImportEnabled: form.forceImportEnabled,
        forceImportMaxTries: form.forceImportMaxTries,
        forceImportMinimumMegabytes: form.forceImportMinimumMegabytes > 0 ? form.forceImportMinimumMegabytes : 0,
        unmatchedImportPolicy: form.unmatchedImportPolicy,
        unmatchedImportGiveUpDays: form.unmatchedImportGiveUpDays
      });
      setSaved(nextSettings);
      setForm(nextSettings);
      setSaveState("saved");
      setMessage("Saved just now");
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-(--page-gap)" noValidate>
      <PageToolbar tabs={librarySetupNavItems} />

      <ListCard title="After a download finishes" titleCase={false}>
        <ImportPolicyFields
          form={form}
          onFormChange={(update) => setForm((current) => update(current))}
        />
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save import settings" />
    </form>
  );
}

/** Null when the two thresholds agree; otherwise the plain-words reason the drawer's Save should refuse (#909). */
function libraryFreeSpaceThresholdError(form: PlatformSettingsSnapshot): string | null {
  if (form.libraryFreeSpaceWarnPercent < 1 || form.libraryFreeSpaceWarnPercent > 50) {
    return "Choose a warn percentage between 1 and 50.";
  }
  if (form.libraryFreeSpacePausePercent < 1 || form.libraryFreeSpacePausePercent > 50) {
    return "Choose a pause percentage between 1 and 50.";
  }
  if (form.libraryFreeSpacePausePercent > form.libraryFreeSpaceWarnPercent) {
    return "The pause threshold must be at or below the warn threshold.";
  }
  if (form.minimumFreeSpaceMb < 0) {
    return "Choose a minimum free space of 0 MB or more.";
  }
  if (form.unmatchedImportPolicy === "give-up-after-days" && (form.unmatchedImportGiveUpDays < 1 || form.unmatchedImportGiveUpDays > 365)) {
    return "Choose a give-up time between 1 and 365 days.";
  }
  return null;
}

function ImportPolicyFields({
  form,
  onFormChange
}: {
  form: PlatformSettingsSnapshot;
  onFormChange: (update: (current: PlatformSettingsSnapshot) => PlatformSettingsSnapshot) => void;
}) {
  const thresholdError = libraryFreeSpaceThresholdError(form);
  return (
    <>
      <div className="grid divide-y divide-hairline md:grid-cols-3 md:divide-x md:divide-y-0">
      <div className="p-(--card-pad-x)">
        <SwitchRow label="Rename files when imported" description="Apply the naming choices from Media Naming as the file enters the library." checked={form.renameOnImport} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, renameOnImport: checked }))} />
      </div>
      <div className="p-(--card-pad-x)">
        {/* #764: independent of the file switch above -- a movie/series folder
            can be renamed to Deluno's naming choice while the file inside it
            keeps its original name, or the other way around. */}
        <SwitchRow label="Rename folders when imported" description="On, the movie or show folder is renamed to match Media Naming, the same way the file switch renames the file. Off, a folder Deluno was handed (from a download client or processor) keeps the name it arrived with; a brand-new title still gets a named folder either way." checked={form.renameFoldersOnImport} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, renameFoldersOnImport: checked }))} />
      </div>
      <div className="p-(--card-pad-x)">
        <SwitchRow label="Keep seeding without a second copy" description="Use a hardlink when the drives support it, so the download client and library share one set of file data." checked={form.useHardlinks} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, useHardlinks: checked }))} />
      </div>
      </div>
      {/*
        A global empty-folder cleanup switch (CleanupEmptyFolders) used to sit
        here too. Import only ever consulted the per-library
        LibraryItem.RemoveEmptySourceFolders switch (Processing -> a
        library's workflow drawer) -- this one was stored and never read
        (#736). Removed rather than wired up, since a global default would
        still need the per-library switch to win, and that switch already
        exists per library.
      */}
      <p className="border-t border-hairline p-(--card-pad-x) text-(length:--type-caption) text-muted-foreground">
        Whether Deluno removes folders left empty by an import is set per library, in Media Management, Processing Workflow, on that library's workflow.
      </p>
      <div className="border-t border-hairline p-(--card-pad-x)">
        <div className="grid content-start gap-1.5 pb-(--grid-gap)">
          <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Storage safety</p>
          <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Deluno checks every library's free space on the health schedule. Below the warn line it flags a problem but keeps working; at or below the pause line it stops that library's grabs and imports until space is freed, the same way an unreachable folder does.</p>
        </div>
        <FieldRow>
          <Field label="Warn when a library has less than" help="Percent of the drive free. Deluno flags the problem on Health, the Dashboard tile and the bell, but keeps grabbing and importing.">
            <div className="flex items-center gap-2">
              <Input type="number" min={1} max={50} value={form.libraryFreeSpaceWarnPercent} onChange={(event) => onFormChange((current) => ({ ...current, libraryFreeSpaceWarnPercent: Number(event.target.value || 0) }))} className="max-w-28" />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">% free</span>
            </div>
          </Field>
          <Field label="Pause imports when a library has less than" error={thresholdError && form.libraryFreeSpacePausePercent > form.libraryFreeSpaceWarnPercent ? thresholdError : undefined} help={thresholdError && form.libraryFreeSpacePausePercent > form.libraryFreeSpaceWarnPercent ? undefined : "At or below this, Deluno pauses the library's grabs and imports until space is freed."}>
            <div className="flex items-center gap-2">
              <Input type="number" min={1} max={50} value={form.libraryFreeSpacePausePercent} onChange={(event) => onFormChange((current) => ({ ...current, libraryFreeSpacePausePercent: Number(event.target.value || 0) }))} className="max-w-28" />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">% free</span>
            </div>
          </Field>
        </FieldRow>
        <FieldRow>
          <Field label="Minimum free space" help="Deluno blocks an import that would leave the destination volume below this, with a dismissable Activity entry saying why.">
            <div className="flex items-center gap-2">
              <Input type="number" min={0} value={form.minimumFreeSpaceMb} onChange={(event) => onFormChange((current) => ({ ...current, minimumFreeSpaceMb: Number(event.target.value || 0) }))} className="max-w-28" />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">MB</span>
            </div>
          </Field>
        </FieldRow>
        <SwitchRow label="Skip free space check" description="Only for a filesystem that cannot report free space at all. Lower the minimum above instead of turning this on just to allow a tighter margin." checked={form.skipFreeSpaceCheck} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, skipFreeSpaceCheck: checked }))} />
      </div>
      <div className="border-t border-hairline p-(--card-pad-x)">
        <div className="grid content-start gap-1.5 pb-(--grid-gap)">
          <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Leftover downloads</p>
          <p className="text-(length:--type-caption) leading-snug text-muted-foreground">A leftover is a download-folder file nothing is using any more — no client, import or hand-off, and not still the other name of a library file. Off for a new install; the leftovers list in <PageLink page="transfers" /> always offers Keep/Remove by hand either way.</p>
        </div>
        <Field label="Never count these as leftovers" help="Names to skip, one per line or separated by commas. * matches any run of characters and ? one character, so *.nfo skips every .nfo and Thumbs.db skips that file. Checked against each file or folder name at the top of the download folder. Empty by default.">
          <Textarea
            rows={2}
            value={form.leftoverExcludePatterns}
            placeholder="*.nfo, Thumbs.db"
            onChange={(event) => onFormChange((current) => ({ ...current, leftoverExcludePatterns: event.target.value }))}
          />
        </Field>
        <Field label="Count as a leftover after" help="How long something must sit unclaimed in a download folder before it is listed at all. A download that has only just finished can appear a moment before its client lists it, so this is 2 hours unless you change it.">
          <div className="flex items-center gap-2">
            <Input type="number" min={1} max={8760} value={form.leftoverMinAgeHours} onChange={(event) => onFormChange((current) => ({ ...current, leftoverMinAgeHours: Number(event.target.value || 0) }))} className="max-w-28" />
            <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">hours</span>
          </div>
        </Field>
        <SwitchRow label="Delete leftover download files automatically" description="Deluno checks for these on its own schedule and writes every deletion to Activity, saying why it counted as a leftover." checked={form.leftoverDownloadCleanupEnabled} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, leftoverDownloadCleanupEnabled: checked }))} />
        {form.leftoverDownloadCleanupEnabled ? (
          <Field label="After" help="How long a leftover must have sat, untouched, before Deluno deletes it.">
            <div className="flex items-center gap-2">
              <Input type="number" min={1} max={8760} value={form.leftoverDownloadCleanupAgeHours} onChange={(event) => onFormChange((current) => ({ ...current, leftoverDownloadCleanupAgeHours: Number(event.target.value || 0) }))} className="max-w-28" />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">hours</span>
            </div>
          </Field>
        ) : null}
      </div>
      <div className="border-t border-hairline p-(--card-pad-x)">
        <div className="grid content-start gap-1.5 pb-(--grid-gap)">
          <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Force import</p>
          <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Off for a new install. When on, Deluno imports a download anyway after its import has already stopped once for one minor reason, and only this one: Deluno could not read the file to check it (a share that dropped, a lock, or a slow disk), so nothing was found wrong with it. It never goes past a file Deluno read and found wrong, a title it cannot identify, or a file that would replace one you already have. Because Deluno could not read the file, it cannot tell a sample by looking inside, so a file whose name looks like a sample, or that is smaller than the size below, stays stopped. Every forced import is written to Activity.</p>
        </div>
        <SwitchRow label="Import files Deluno could not check" description="Only after the first stop, and only into a gap. If the file turns out to be damaged, remove it from the title's page and search again." checked={form.forceImportEnabled} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, forceImportEnabled: checked }))} />
        {form.forceImportEnabled ? (
          <Field label="Tries per download" help="How many times Deluno will force the import of one download before it leaves it stopped for you.">
            <div className="flex items-center gap-2">
              <Input type="number" min={1} max={25} value={form.forceImportMaxTries} onChange={(event) => onFormChange((current) => ({ ...current, forceImportMaxTries: Number(event.target.value || 0) }))} className="max-w-28" />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">tries</span>
            </div>
          </Field>
        ) : null}
        {form.forceImportEnabled ? (
          <Field label="Smallest file to force" help="Files smaller than this are never force-imported, so samples and trailers stay out. Leave it empty to use 100 MB.">
            <div className="flex items-center gap-2">
              <Input type="number" min={1} max={100000} value={form.forceImportMinimumMegabytes > 0 ? form.forceImportMinimumMegabytes : ""} onChange={(event) => onFormChange((current) => ({ ...current, forceImportMinimumMegabytes: Number(event.target.value || 0) }))} className="max-w-28" />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">MB</span>
            </div>
          </Field>
        ) : null}
      </div>
      <div className="border-t border-hairline p-(--card-pad-x)">
        <div className="grid content-start gap-1.5 pb-(--grid-gap)">
          <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Unmatched imports</p>
          <p className="text-(length:--type-caption) leading-snug text-muted-foreground">What Deluno does with a file it downloaded but could not match to a title or episode — the same choice applies when a processor hands back a file that no longer matches.</p>
        </div>
        <Field label="When a download cannot be matched" help={<>Hold keeps it on <PageLink page="transfers" /> for you to match by hand, for as long as it takes. The other two act on their own.</>}>
          <SegmentedControl<"hold" | "give-up-after-days" | "retry-once-then-bad">
            value={form.unmatchedImportPolicy as "hold" | "give-up-after-days" | "retry-once-then-bad"}
            onValueChange={(unmatchedImportPolicy) => onFormChange((current) => ({ ...current, unmatchedImportPolicy }))}
            options={[
              { value: "hold", label: "Hold for manual import" },
              { value: "give-up-after-days", label: "Give up after N days" },
              { value: "retry-once-then-bad", label: "Retry once, then treat as bad" }
            ]}
            aria-label="When a download cannot be matched"
          />
        </Field>
        {form.unmatchedImportPolicy === "give-up-after-days" ? (
          <Field
            label="Give up after"
            error={thresholdError && (form.unmatchedImportGiveUpDays < 1 || form.unmatchedImportGiveUpDays > 365) ? thresholdError : undefined}
            help={thresholdError && (form.unmatchedImportGiveUpDays < 1 || form.unmatchedImportGiveUpDays > 365) ? undefined : "How many days an unmatched download waits before Deluno removes it, deletes the file, blocklists the release and searches again."}
          >
            <div className="flex items-center gap-2">
              <Input
                type="number"
                min={1}
                max={365}
                value={form.unmatchedImportGiveUpDays}
                onChange={(event) => onFormChange((current) => ({ ...current, unmatchedImportGiveUpDays: Number(event.target.value || 0) }))}
                className="max-w-28"
              />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">days</span>
            </div>
          </Field>
        ) : null}
      </div>
      <div className="border-t border-hairline p-(--card-pad-x)">
        <div className="grid content-start gap-1.5 pb-(--grid-gap)">
          <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Still-packed archives</p>
          <p className="text-(length:--type-caption) leading-snug text-muted-foreground">A download that is still a packed archive nothing can open is always blocklisted (change that per-reason under <PageLink page="failedDownloads" />). Removing and deleting it from the download client is your call.</p>
        </div>
        <SwitchRow label="Remove and delete still-packed archives from the download client" description="Off keeps the packed files in the client, using disk space, the same as today." checked={form.removeAndDeletePackedArchivesFromClient} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, removeAndDeletePackedArchivesFromClient: checked }))} />
      </div>
      <div className="border-t border-hairline p-(--card-pad-x)">
        <div className="grid content-start gap-1.5 pb-(--grid-gap)">
          <p className="text-(length:--type-body-sm) font-medium leading-tight text-foreground">Permissions</p>
          <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Set file mode and group ownership when Deluno imports a file. This only takes effect when Deluno's own process owns the file — a limit of the operating system.</p>
        </div>
        <SwitchRow label="Set permissions on import" description="Apply the chmod and group below to every media file Deluno imports." checked={form.setPermissionsOnImport} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, setPermissionsOnImport: checked }))} />
        {form.setPermissionsOnImport ? (
          <FieldRow>
            <Field label="Chmod" help="Octal file mode, such as 755 or 0644.">
              <Input value={form.folderChmod ?? ""} onChange={(event) => onFormChange((current) => ({ ...current, folderChmod: event.target.value }))} placeholder="755" className="max-w-28" />
            </Field>
            <Field label="Chown group" help="Group name or GID applied to imported files. Leave blank to leave group ownership unchanged.">
              <Input value={form.chownGroup ?? ""} onChange={(event) => onFormChange((current) => ({ ...current, chownGroup: event.target.value }))} placeholder="media" />
            </Field>
          </FieldRow>
        ) : null}
      </div>
    </>
  );
}

/** #762: mirrors ImportPipelineService.BuildSeasonSubfolder's own two tokens. */
function previewSeasonFormat(format: string, season = 1) {
  return format.replaceAll("{Season:00}", season.toString().padStart(2, "0")).replaceAll("{Season}", season.toString());
}

/** #763: mirrors Deluno.Series.Contracts.MultiEpisodeStyles.FormatToken for a 2-episode example. */
function multiEpisodeToken(style: string): string {
  switch (style) {
    case "extend":
      return "S01E01E02";
    case "duplicate":
      return "S01E01S01E02";
    case "repeat":
      return "S01E01-S01E02";
    case "scene":
      return "S01E01-E02";
    case "prefixed-range":
      return "S01E01-02";
    default:
      return "S01E01-E02";
  }
}

/** #763: the same episode-file format the single-episode preview uses, but with a combined token and joined episode title. */
function previewMultiEpisodeFormat(episodeFileFormat: string, style: string) {
  return previewNamingFormat(episodeFileFormat)
    .replace(/S0?1E0?1/i, multiEpisodeToken(style))
    .replace("Good News About Hell", "Good News About Hell + The Grim Barrow");
}

function NamingPreview({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid gap-1.5">
      <span className="text-(length:--type-micro) font-semibold uppercase tracking-[0.08em] text-muted-foreground">{label}</span>
      <code className="wrap-break-word rounded-md border border-hairline bg-surface-1 px-2.5 py-2 text-(length:--type-caption) leading-snug text-foreground">{value}</code>
    </div>
  );
}

/* ============================================== processing workflow */

interface WorkflowForm {
  importWorkflow: string;
  /** The chosen connection's id; the name below is only shown for a link that matches no connection. */
  processorConnectionId: string;
  processorName: string;
  processorOutputPath: string;
  processorTimeoutMinutes: string;
  processorFailureMode: string;
  cleanupMode: string;
  removeEmptySourceFolders: boolean;
}
interface CallbackForm {
  provider: string;
  submissionUrl: string;
  authHeaderName: string;
  healthCheckUrl: string;
  secret: string;
  isEnabled: boolean;
}
type ProcessingDrawer = { kind: "closed" } | { kind: "library"; id: string } | { kind: "callback"; editId?: string };

function ProcessingWorkflowPage() {
  const { libraries, processorConnections } = useLoaderData() as LoaderData;
  const { versions } = useConnectionVersions();
  const location = useLocation();
  const navigate = useNavigate();
  const revalidator = useRevalidator();

  const [drawer, setDrawer] = useState<ProcessingDrawer>({ kind: "closed" });
  const [workflow, setWorkflow] = useState<WorkflowForm>(() => emptyWorkflow());
  const [workflowInitial, setWorkflowInitial] = useState<WorkflowForm>(() => emptyWorkflow());
  const [callback, setCallback] = useState<CallbackForm>(emptyCallback);
  const [callbackInitial, setCallbackInitial] = useState<CallbackForm>(emptyCallback);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState<ProcessorConnectionItem | null>(null);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  // Optional path pairs for one processor connection (#962): "Deluno sees" /
  // "processor sees", the same shape download clients already offer. Loaded
  // per connection rather than for every one up front, since most setups
  // have zero or one processor connected.
  const [mappingConnectionId, setMappingConnectionId] = useState<string | null>(null);
  const [mappings, setMappings] = useState<ProcessorPathMappingItem[]>([]);
  const [newProcessorMapping, setNewProcessorMapping] = useState({ delunoPath: "", processorPath: "" });

  const editingLibrary = drawer.kind === "library" ? libraries.find((library) => library.id === drawer.id) ?? null : null;
  const isRefined = workflow.importWorkflow === "refine-before-import";
  const hasProcessor = Boolean(workflow.processorConnectionId || workflow.processorName);
  const selectedProcessor = findLinkedProcessor(processorConnections, workflow.processorConnectionId, workflow.processorName);
  const processorProduct = selectedProcessor ? processorProductName(selectedProcessor.name) : null;
  const workflowReady = !isRefined || (Boolean(workflow.processorOutputPath.trim()) && (!hasProcessor || Boolean(selectedProcessor?.isEnabled)));
  const dirty =
    drawer.kind === "library"
      ? JSON.stringify(workflow) !== JSON.stringify(workflowInitial)
      : drawer.kind === "callback"
        ? drawer.editId
          ? JSON.stringify(callback) !== JSON.stringify(callbackInitial)
          : Boolean(callback.submissionUrl.trim() || callback.secret.trim())
        : false;
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  function openLibrary(library: LibraryItem) {
    const next = workflowFrom(library);
    setDrawer({ kind: "library", id: library.id });
    setWorkflow(next);
    setWorkflowInitial(next);
    setSaveState(undefined);
  }

  useEffect(() => {
    if (drawer.kind !== "closed") return;
    const libraryId = new URLSearchParams(location.search).get("libraryId");
    if (!libraryId) return;
    const library = libraries.find((item) => item.id === libraryId);
    if (!library) return;
    openLibrary(library);
    navigate("/settings/processing", { replace: true });
  }, [drawer.kind, libraries, location.search, navigate]);
  // A notice about a newer version opens that processor (#1118).
  useEffect(() => {
    if (drawer.kind !== "closed") return;
    const id = new URLSearchParams(location.search).get("connection");
    if (!id) return;
    const connection = processorConnections.find((item) => item.id === id);
    if (!connection) return;
    openEditCallback(connection);
    navigate("/settings/processing", { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [drawer.kind, processorConnections, location.search, navigate]);
  function openEditCallback(connection: ProcessorConnectionItem) {
    const form: CallbackForm = {
      provider: connection.provider,
      submissionUrl: connection.submissionUrl,
      healthCheckUrl: connection.healthCheckUrl ?? "",
      authHeaderName: connection.authHeaderName,
      secret: "",
      isEnabled: connection.isEnabled
    };
    setDrawer({ kind: "callback", editId: connection.id });
    setCallback(form);
    setCallbackInitial(form);
    setSaveState(undefined);
  }
  function openCallback() {
    setDrawer({ kind: "callback" });
    setCallback(emptyCallback());
    setSaveState(undefined);
  }
  /**
   * Opens the processor drawer pointed at Weir's own address on this machine
   * (#640): the installer put it there, or it was already running. Weir still
   * generates its own webhook secret when its account is created, so that
   * field is left for the person to paste in — Deluno and Weir stay decoupled
   * (no API exchange Deluno would have to know Weir-specific internals for).
   */
  function openDetectedCallback(status: BundledComponentStatus) {
    setDrawer({ kind: "callback" });
    setCallback({ ...emptyCallback(), submissionUrl: `http://${status.defaultHost}:${status.defaultPort}/api/v1/intake/webhook/deluno` });
    setSaveState(undefined);
  }
  function closeDrawer() {
    setDrawer({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    if (drawer.kind === "library" && isRefined && !workflowReady) {
      setSaveState("error");
      setMessage(workflow.processorOutputPath.trim() ? "Choose an enabled processor connection, or watch the output folder." : "Choose the folder where the processor will write cleaned files.");
      return;
    }
    setBusy("save");
    setSaveState("saving");
    try {
      if (drawer.kind === "library" && editingLibrary) {
        const response = await authedFetch(`/api/libraries/${editingLibrary.id}/workflow`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            importWorkflow: workflow.importWorkflow,
            processorConnectionId: workflow.processorConnectionId || null,
            processorName: workflow.processorName || null,
            processorOutputPath: workflow.processorOutputPath || null,
            processorTimeoutMinutes: Number(workflow.processorTimeoutMinutes || DEFAULT_STALL_MINUTES),
            processorFailureMode: workflow.processorFailureMode,
            cleanupMode: workflow.cleanupMode,
            removeEmptySourceFolders: workflow.removeEmptySourceFolders
          })
        });
        if (!response.ok) throw new Error(await readErrorText(response, "Import workflow could not be saved."));
        setWorkflowInitial(workflow);
        setMessage("Saved just now");
      } else if (drawer.kind === "callback") {
        const editing = drawer.editId;
        const response = await authedFetch(editing ? `/api/integrations/processors/connections/${editing}` : "/api/integrations/processors/connections", { method: editing ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(callback) });
        if (!response.ok) throw new Error(await readErrorText(response, "Processor connection could not be saved."));
        setCallback(emptyCallback());
        setCallbackInitial(emptyCallback());
        setMessage(editing ? "Processor saved" : "Processor connected");
        closeDrawer();
      }
      setSaveState("saved");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(null);
    }
  }

  async function run(key: string, action: () => Promise<unknown>, success?: string) {
    setBusy(key);
    try {
      await action();
      if (success) toast.success(success);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Action failed.");
    } finally {
      setBusy(null);
    }
  }

  async function testCallback(connection: ProcessorConnectionItem) {
    await run(`test:${connection.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/connections/${connection.id}/test`, { method: "POST" });
      if (!response.ok) throw new Error(await readErrorText(response, "Processor connection test failed."));
      const result = (await response.json()) as ProcessorConnectionTestResult;
      toast.success(`${connection.name}: ${result.message}`);
    });
  }
  async function removeCallback(connection: ProcessorConnectionItem) {
    await run(`remove:${connection.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/connections/${connection.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error(await readErrorText(response, "Processor connection could not be disconnected."));
    }, `${connection.name} disconnected`);
    setConfirmRemove(null);
  }

  async function loadMappings(connectionId: string) {
    try {
      setMappings(await fetchJson<ProcessorPathMappingItem[]>(`/api/integrations/processors/connections/${connectionId}/path-mappings`));
    } catch {
      toast.error("Could not load path mappings.");
    }
  }
  function toggleMappings(connection: ProcessorConnectionItem) {
    if (mappingConnectionId === connection.id) {
      setMappingConnectionId(null);
      return;
    }
    setMappingConnectionId(connection.id);
    setNewProcessorMapping({ delunoPath: "", processorPath: "" });
    void loadMappings(connection.id);
  }
  async function addProcessorMapping() {
    if (!mappingConnectionId || !newProcessorMapping.delunoPath.trim() || !newProcessorMapping.processorPath.trim()) return;
    await run("mapping:add", async () => {
      const response = await authedFetch(`/api/integrations/processors/connections/${mappingConnectionId}/path-mappings`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ delunoPath: newProcessorMapping.delunoPath.trim(), processorPath: newProcessorMapping.processorPath.trim(), isEnabled: true, priority: 10 })
      });
      if (!response.ok) throw new Error(await readErrorText(response, "Path mapping could not be saved."));
      await loadMappings(mappingConnectionId);
      setNewProcessorMapping({ delunoPath: "", processorPath: "" });
    }, "Path mapping saved");
  }
  async function removeProcessorMapping(mappingId: string) {
    if (!mappingConnectionId) return;
    const connectionId = mappingConnectionId;
    await run(`mapping:remove:${mappingId}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/connections/${connectionId}/path-mappings/${mappingId}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error(await readErrorText(response, "Path mapping could not be removed."));
      await loadMappings(connectionId);
    }, "Path mapping removed");
  }

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar tabs={librarySetupNavItems} actions={<PageToolbarAction variant="outline" onClick={openCallback}>Connect processor</PageToolbarAction>} />

      <ListCard title="Finished download workflow" count="Select a library to choose what happens next">
        {libraries.length === 0 ? (
          <ListEmpty title="No libraries yet" description="Create a library first; each one chooses whether completed downloads import straight away or wait for a processor." />
        ) : (
          <ListTable
            listId="media-management-libraries"
            items={libraries}
            columns={[
              { id: "library", label: "Library", sort: (library) => library.name.toLowerCase() },
              { id: "happens", label: "What happens", sort: (library) => (library.importWorkflow ?? "standard") },
              { id: "processed", label: "Processed folder", width: "minmax(0,1.4fr)", sort: (library) => (library.processorOutputPath ?? "").toLowerCase() },
              { id: "afterImport", label: "After import", sort: (library) => library.cleanupMode ?? "keep-source" },
              { id: "failure", label: "If processing fails", sort: (library) => library.processorFailureMode ?? "" },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (library) => { const refined = (library.importWorkflow ?? "standard") === "refine-before-import"; const processor = findLinkedProcessor(processorConnections, library.processorConnectionId, library.processorName); const ready = !refined || (Boolean(library.processorOutputPath) && (!(library.processorConnectionId || library.processorName) || Boolean(processor?.isEnabled))); return ready ? 1 : 0; } }
            ]}
          >
            {(rows) => rows.map((library) => {
              const refined = (library.importWorkflow ?? "standard") === "refine-before-import";
              const processor = findLinkedProcessor(processorConnections, library.processorConnectionId, library.processorName);
              const ready = !refined || (Boolean(library.processorOutputPath) && (!(library.processorConnectionId || library.processorName) || Boolean(processor?.isEnabled)));
              return (
                <ListRow key={library.id} onClick={() => openLibrary(library)} selected={drawer.kind === "library" && drawer.id === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={importWorkflowLabel(library.importWorkflow)} secondary={refined ? library.processorName || "Waits for cleaned output" : "Use the finished download"} />
                  <ListCell mono primary={refined ? library.processorOutputPath || <span className="font-sans text-warning">Not set</span> : <span className="font-sans text-muted-foreground">—</span>} secondary={refined ? `Gives up after ${Math.round((library.processorTimeoutMinutes || DEFAULT_STALL_MINUTES) / 60)} h with no progress` : undefined} />
                  <ListCell primary={(library.cleanupMode ?? "keep-source") === "remove-source-after-import" ? "Remove downloaded file" : "Keep downloaded file"} secondary={library.removeEmptySourceFolders ? "Also remove empty folders" : undefined} />
                  <ListCell primary={refined ? FAILURE_OPTIONS.find((option) => option.value === library.processorFailureMode)?.label ?? library.processorFailureMode : <span className="text-muted-foreground">—</span>} />
                  <ListCell mobile>
                    <Chip tone={ready ? (refined ? "info" : "ok") : "warn"}>{refined ? (ready ? "Ready" : "Needs a folder") : "Ready"}</Chip>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <DetectedComponents
        category="processor"
        onConnected={() => revalidator.revalidate()}
        configuredKinds={processorConnections.some((connection) => connection.submissionUrl.includes(":9347")) ? ["weir"] : []}
        onUse={openDetectedCallback}
      />
      <ListCard title="Processor connections" count="Optional — only when an external processor cleans files for Deluno">
        {processorConnections.length === 0 ? (
          <ListEmpty
            title="No processor connected"
            description="This is optional. Deluno can watch the processed output folder itself. Connect a processor when it needs to receive a job from Deluno and report back when the cleaned file is ready."
            actions={<Button type="button" size="sm" variant="outline" onClick={openCallback}>Connect processor</Button>}
          />
        ) : (
          <ListTable
            listId="processor-connections"
            items={processorConnections}
            columns={[
              { id: "name", label: "Name", sort: (connection) => connection.name.toLowerCase() },
              { id: "type", label: "Type", sort: (connection) => connection.provider },
              { id: "notifies", label: "Notifies", width: "minmax(0,1.4fr)", sort: (connection) => (connection.submissionUrl || "").toLowerCase() },
              { id: "health", label: "Health", width: LIST_TRACK.status, mobile: true, sort: (connection) => toneSortValue(healthTone(connection.healthStatus), connection.healthStatus) },
              { id: "actions", label: "Actions", width: "22rem", srOnly: true }
            ]}
            chevron={false}
          >
            {(rows) => rows.map((connection) => (
              <ListRow key={connection.id}>
                <ListNameCell name={<>{connection.name}<ConnectionVersionBeside info={versions.get(versionKey("processor", connection.id))} /></>} sub={processorSub(connection)} />
                <ListCell primary={connection.provider === "fileflows-webhook" ? "FileFlows webhook" : "Processor webhook"} />
                <ListCell mono primary={connection.submissionUrl || "—"} />
                <ListCell mobile>
                  <Chip tone={healthTone(connection.healthStatus)}>{connection.healthStatus}</Chip>
                </ListCell>
                {/* Its own fixed-width track on wide screens; on a phone it drops to a full-width line under the name, and the buttons wrap instead of overlapping. */}
                <ListCell align="end" className="max-md:col-span-full max-md:block!">
                  <span className="flex flex-wrap justify-end gap-2">
                    <Button type="button" variant="outline" size="sm" className="h-7 px-2" disabled={busy !== null} onClick={() => void testCallback(connection)}>
                      {busy === `test:${connection.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : <Wifi className="h-3 w-3" />}
                      Test
                    </Button>
                    <Button type="button" variant="outline" size="sm" className="h-7 px-2" aria-label={`Edit ${connection.name}`} disabled={busy !== null} onClick={() => openEditCallback(connection)}>
                      Edit
                    </Button>
                    <Button type="button" variant="outline" size="sm" className="h-7 px-2" aria-expanded={mappingConnectionId === connection.id} disabled={busy !== null} onClick={() => toggleMappings(connection)}>
                      Path mappings
                    </Button>
                    <Button type="button" variant="destructive" size="sm" className="h-7 px-2" disabled={busy !== null} onClick={() => setConfirmRemove(connection)}>
                      Remove
                    </Button>
                  </span>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
        {mappingConnectionId ? (
          <div className="mt-3 grid gap-2 rounded-[10px] border border-hairline bg-surface-1/40 px-3 py-3">
            <p className="text-(length:--type-caption) text-muted-foreground">
              Only needed when {processorConnections.find((connection) => connection.id === mappingConnectionId)?.name ?? "this processor"} runs in a container or on another machine and reports a path Deluno cannot use directly — for example the processor reports <code className="font-mono">/data/refined</code> while Deluno reads <code className="font-mono">D:\Refined</code>. With no pairs set here, paths are sent and read back exactly as they are today.
            </p>
            {mappings.length ? (
              <div className="grid gap-2">
                {mappings.map((mapping) => (
                  <div key={mapping.id} className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-(--field-pad-x) font-mono text-(length:--type-caption)">
                    <span className="min-w-0 flex-1 truncate text-foreground" title={mapping.delunoPath}>{mapping.delunoPath}</span>
                    <span aria-hidden className="text-primary">↔</span>
                    <span className="min-w-0 flex-1 truncate text-muted-foreground" title={mapping.processorPath}>{mapping.processorPath}</span>
                    <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove ${mapping.delunoPath} link`} onClick={() => void removeProcessorMapping(mapping.id)} disabled={busy !== null}>
                      <Trash2 className="h-3.5 w-3.5" />
                    </Button>
                  </div>
                ))}
              </div>
            ) : null}
            <FieldRow>
              <Field label="Path as Deluno sees it">
                <Input value={newProcessorMapping.delunoPath} onChange={(event) => setNewProcessorMapping((current) => ({ ...current, delunoPath: event.target.value }))} placeholder="D:\Refined" className="font-mono text-(length:--type-caption)" />
              </Field>
              <Field label="Same path as the processor sees it">
                <Input value={newProcessorMapping.processorPath} onChange={(event) => setNewProcessorMapping((current) => ({ ...current, processorPath: event.target.value }))} placeholder="/data/refined" className="font-mono text-(length:--type-caption)" />
              </Field>
            </FieldRow>
            <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void addProcessorMapping()} disabled={busy !== null || !newProcessorMapping.delunoPath.trim() || !newProcessorMapping.processorPath.trim()}>
              {busy === "mapping:add" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
              Link paths
            </Button>
          </div>
        ) : null}
      </ListCard>

      <Drawer
        open={drawer.kind !== "closed"}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={drawer.kind === "callback" ? (drawer.editId ? "Edit processor connection" : "Connect a processor") : "How to handle finished downloads"}
        description={drawer.kind === "callback" ? "Deluno sends the processor a job, then the processor reports back when the cleaned file is ready." : `${editingLibrary?.name ?? "Library"} · ${editingLibrary?.rootPath ?? ""}`}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={message} saveLabel={drawer.kind === "callback" ? (drawer.editId ? "Save processor" : "Connect processor") : "Save workflow"} onCancel={requestClose} disabled={busy !== null || (drawer.kind === "library" && !workflowReady)} />}
      >
        {drawer.kind === "library" ? (
          <>
            <DrawerSection title="Import timing">
              <Field label="When a download finishes" help={<>{IMPORT_WORKFLOW_LABELS.standard}: {IMPORT_WORKFLOW_HELP.standard} {IMPORT_WORKFLOW_LABELS["refine-before-import"]}: Deluno waits for {processorProduct ?? "your processor"} to clean the download, then imports the cleaned file.</>}>
                <SegmentedControl<"standard" | "refine-before-import">
                  value={workflow.importWorkflow as "standard" | "refine-before-import"}
                  onValueChange={(importWorkflow) => setWorkflow((current) => ({ ...current, importWorkflow }))}
                  options={[{ value: "standard", label: IMPORT_WORKFLOW_LABELS.standard }, { value: "refine-before-import", label: IMPORT_WORKFLOW_LABELS["refine-before-import"] }]}
                  aria-label="Import timing"
                />
              </Field>
              <div className="rounded-[10px] border border-hairline bg-surface-1/40 px-3 py-2.5">
                <p className="text-(length:--type-body-sm) font-medium text-foreground">{isRefined ? "Deluno will wait for the cleaned file" : "Deluno will import the finished download"}</p>
                <p className="mt-1 text-(length:--type-caption) leading-relaxed text-muted-foreground">{isRefined ? "The file is not sent to your library until the output folder contains a matching processed file." : "Deluno places the finished file in this library, applies its naming rules, and then refreshes its details."}</p>
              </div>
            </DrawerSection>
            {isRefined ? (
              <DrawerSection title="External processing">
                <Field label="Processed output folder" error={workflow.processorOutputPath.trim() ? undefined : "Choose the folder where the processor will write cleaned files."} help="A separate folder where your processor writes cleaned files. Deluno waits until the file is readable and stable, matches it to the waiting download, then imports it.">
                  <PathInput value={workflow.processorOutputPath} onChange={(value) => setWorkflow((current) => ({ ...current, processorOutputPath: value }))} browseTitle="Choose processed-output folder" />
                </Field>
                <FieldRow>
                  <Field label="No progress for" help="Deluno asks the processor what it is doing. Queued, scheduled or working is waited for however long it takes; this is how long nothing may change before the rule on the right applies.">
                    <Select value={workflow.processorTimeoutMinutes} onChange={(event) => setWorkflow((current) => ({ ...current, processorTimeoutMinutes: event.target.value }))} options={TIMEOUT_OPTIONS} />
                  </Field>
                  <Field label="If processing stalls or fails" help={<>Import the original file brings the untouched download in. The other two keep it out of the library and ask you in <PageLink page="transfers" />.</>}>
                    <Select value={workflow.processorFailureMode} onChange={(event) => setWorkflow((current) => ({ ...current, processorFailureMode: event.target.value }))} options={FAILURE_OPTIONS} />
                  </Field>
                </FieldRow>
                <Field label="Completion signal" optional help="Leave this on folder watching unless a connected processor sends a completion message back to Deluno. A disabled connection cannot be selected.">
                  <Select
                    value={workflow.processorConnectionId || (workflow.processorName ? UNMATCHED_PROCESSOR : "")}
                    onChange={(event) => setWorkflow((current) => {
                      const value = event.target.value;
                      if (value === UNMATCHED_PROCESSOR) return current;
                      const chosen = processorConnections.find((connection) => connection.id === value);
                      return { ...current, processorConnectionId: chosen?.id ?? "", processorName: chosen?.name ?? "" };
                    })}
                    placeholder="Watch the folder (no processor message)"
                  >
                    {processorConnections.map((connection) => (
                      <option key={connection.id} value={connection.id} disabled={!connection.isEnabled}>
                        {connection.name}
                        {connection.isEnabled ? "" : " (disabled)"}
                      </option>
                    ))}
                    {!workflow.processorConnectionId && workflow.processorName && !selectedProcessor ? <option value={UNMATCHED_PROCESSOR}>{workflow.processorName} (existing)</option> : null}
                  </Select>
                </Field>
                {hasProcessor && !selectedProcessor?.isEnabled ? <p className="text-(length:--type-caption) text-warning">This processor connection is disabled or missing. Enable it, choose folder watching, or select another connection before saving.</p> : null}
                </DrawerSection>
              ) : null}
            <DrawerSection title="Source cleanup">
              <Field label="After import" help={<>This is Deluno's own cleanup step after a successful import. It is separate from {processorProduct ? `${processorProduct}'s own cleanup` : "any external processor's"}. The copy in your library stays.</>}>
                <SegmentedControl<"keep-source" | "remove-source-after-import">
                  value={workflow.cleanupMode as "keep-source" | "remove-source-after-import"}
                  onValueChange={(cleanupMode) => setWorkflow((current) => ({ ...current, cleanupMode }))}
                  options={[{ value: "keep-source", label: "Keep source file" }, { value: "remove-source-after-import", label: "Remove after import" }]}
                  aria-label="Source cleanup"
                />
              </Field>
              <SwitchRow label="Remove empty source folders too" description={workflow.cleanupMode === "remove-source-after-import" ? "Only folders left empty by this import are removed. Deluno never removes the configured download root." : "Choose Remove after import to turn this on."} checked={workflow.removeEmptySourceFolders} onCheckedChange={(checked) => setWorkflow((current) => ({ ...current, removeEmptySourceFolders: checked }))} disabled={workflow.cleanupMode !== "remove-source-after-import"} />
              {/* Two settings used to be able to delete the same file on
                  different schedules, and this one deleting a torrent the client
                  was still sharing is what broke seeding (#287). Each now has a
                  domain, said once, where the choice is being made. */}
              {workflow.cleanupMode === "remove-source-after-import" ? (
                <p className="text-(length:--type-caption) text-muted-foreground">
                  This covers files Deluno found on disk. Anything it downloaded through a search source is handled by your sharing rule under Automation &amp; Recovery instead, so a download still being shared is never deleted out from under its client.
                </p>
              ) : null}
            </DrawerSection>
          </>
        ) : null}

        {drawer.kind === "callback" ? (
          <>
            <SecretUnreadableNotice item={drawer.editId ? processorConnections.find((connection) => connection.id === drawer.editId) : null} />
            {drawer.editId ? <ConnectionUpdatePanel kind="processor" id={drawer.editId} /> : null}
            <div className="rounded-[10px] border border-info/25 bg-info/5 px-3 py-2.5">
              <p className="text-(length:--type-caption) font-medium text-foreground">Two directions are involved</p>
              <p className="mt-1 text-(length:--type-caption) leading-relaxed text-muted-foreground">
                Deluno sends a job to the processor URL below. When the cleaned file is ready, the processor sends an event back to Deluno using the callback path included in that job. For the return message, create a Deluno API key with <span className="font-medium text-foreground">Media automation</span> access under <Link to="/system/api" className="text-info underline underline-offset-2">System → API</Link>.
              </p>
            </div>
            <DrawerSection title="Connection details">
              <Field label="Type" help="Deluno names the connection from its address; there is no name to type.">
                <Select value={callback.provider} onChange={(event) => setCallback((current) => ({ ...current, provider: event.target.value }))} options={[{ value: "generic-webhook", label: "Generic processor webhook" }, { value: "fileflows-webhook", label: "FileFlows webhook" }]} />
              </Field>
              <Field label="Processor job URL" help="Deluno sends a job here when a download needs processing. Your processor reports back to Deluno when the cleaned file is ready.">
                <Input value={callback.submissionUrl} onChange={(event) => setCallback((current) => ({ ...current, submissionUrl: event.target.value }))} placeholder="https://processor.example/webhooks/deluno" className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
              </Field>
              <Field label="Health check address" optional help="An address Deluno can ask to see whether the processor is up; a normal answer means healthy. Leave empty and the test can only confirm the job URL answers.">
                <Input value={callback.healthCheckUrl} onChange={(event) => setCallback((current) => ({ ...current, healthCheckUrl: event.target.value }))} placeholder="https://processor.example/health" className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
              </Field>
            </DrawerSection>
            <DrawerSection title="Authentication">
              <FieldRow>
                <Field label="Token sent to the processor" optional help="Stored encrypted and sent only with Deluno's job request. This is not the Deluno API key used for the return message.">
                  <Input type="password" value={callback.secret} onChange={(event) => setCallback((current) => ({ ...current, secret: event.target.value }))} autoComplete="new-password" />
                </Field>
                <Field label="Processor token header" help="Authorization sends a Bearer token; use X-Api-Key when the processor expects that header.">
                  <Input value={callback.authHeaderName} onChange={(event) => setCallback((current) => ({ ...current, authHeaderName: event.target.value }))} />
                </Field>
              </FieldRow>
              <SwitchRow label="Enabled" description="Disabled processor connections stay configured but are never used." checked={callback.isEnabled} onCheckedChange={(checked) => setCallback((current) => ({ ...current, isEnabled: checked }))} />
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove !== null}
        onOpenChange={(open) => {
          if (!open) setConfirmRemove(null);
        }}
        title={`Remove “${confirmRemove?.name}”?`}
        description="Libraries pointing at it fall back to watching the processed output folder until another processor is connected."
        confirmLabel="Disconnect processor"
        busy={busy?.startsWith("remove:") ?? false}
        onConfirm={() => confirmRemove && void removeCallback(confirmRemove)}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

/* --------------------------------------------------------------- utils */

function emptyWorkflow(): WorkflowForm {
  // #914: a new library holds the file and asks by default. Nothing but an
  // existing library that already chose "Import the original file" keeps
  // that choice.
  return { importWorkflow: "standard", processorConnectionId: "", processorName: "", processorOutputPath: "", processorTimeoutMinutes: String(DEFAULT_STALL_MINUTES), processorFailureMode: "block", cleanupMode: "keep-source", removeEmptySourceFolders: false };
}
const UNMATCHED_PROCESSOR = "__unmatched__";
/** A library's processor by id (#1077), so a rename never loses it; the name is tried only for a link that never matched a connection. */
/** The processor's own name, from a connection named "<name> on <machine>"; null for the plain "Processor on <machine>" form. */
export function processorProductName(connectionName: string): string | null {
  const at = connectionName.indexOf(" on ");
  const product = at > 0 ? connectionName.slice(0, at) : connectionName;
  return product === "Processor" ? null : product;
}

export function findLinkedProcessor(connections: ProcessorConnectionItem[], id: string | null | undefined, name: string | null | undefined) {
  if (id) return connections.find((connection) => connection.id === id) ?? null;
  return name ? connections.find((connection) => connection.name === name) ?? null : null;
}
function workflowFrom(library: LibraryItem): WorkflowForm {
  return {
    importWorkflow: library.importWorkflow ?? "standard",
    processorConnectionId: library.processorConnectionId ?? "",
    processorName: library.processorName ?? "",
    processorOutputPath: library.processorOutputPath ?? "",
    processorTimeoutMinutes: String(library.processorTimeoutMinutes || DEFAULT_STALL_MINUTES),
    processorFailureMode: library.processorFailureMode ?? "block",
    cleanupMode: library.cleanupMode ?? "keep-source",
    removeEmptySourceFolders: library.removeEmptySourceFolders ?? false
  };
}
/** The line under a processor's name; says so when there is no health-check address to test it with (#1055). */
export function processorSub(connection: Pick<ProcessorConnectionItem, "secretConfigured" | "healthCheckUrl" | "healthStatus"> & { secretUnreadable?: boolean }): string {
  if (connection.secretUnreadable) return "Saved token can't be read here · Enter it again (Edit)";
  const token = connection.secretConfigured ? "Token saved" : "No token";
  return !connection.healthCheckUrl && connection.healthStatus === "degraded" ? `${token} · Add a health check address (Edit) to test it fully` : token;
}
function emptyCallback(): CallbackForm {
  return { provider: "generic-webhook", submissionUrl: "", healthCheckUrl: "", authHeaderName: "Authorization", secret: "", isEnabled: true };
}
function healthTone(status: string): NonNullable<ChipProps["tone"]> {
  switch (status) {
    case "healthy":
      return "ok";
    case "degraded":
    case "untested":
      return "warn";
    case "unknown":
      return "idle";
    default:
      return "bad";
  }
}
