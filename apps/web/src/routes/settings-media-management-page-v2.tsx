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
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useLoaderData, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import { Loader2, Wifi } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { NamingFormatField, NamingPatternEditor, previewNamingFormat, type NamingFormatKind } from "../components/app/naming-format-field";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { settingsOverviewLoader } from "./settings-overview-page";
import { fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type ProcessorConnectionItem, type ProcessorConnectionTestResult, type QualityProfileItem } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";

const TIMEOUT_OPTIONS = [
  { value: "60", label: "1 hour" },
  { value: "180", label: "3 hours" },
  { value: "360", label: "6 hours" },
  { value: "720", label: "12 hours" },
  { value: "1440", label: "24 hours" }
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

export function SettingsMediaManagementPage() {
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
  if (kind === "episode-file") return { label: "Episode files", placeholder: "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}" };
  return { label: "Custom format", placeholder: "{Title}" };
}

function updateCustomPattern(settings: PlatformSettingsSnapshot, kind: NamingFormatKind, value: string) {
  if (kind === "movie-folder") return { ...settings, movieFolderFormat: value };
  if (kind === "series-folder") return { ...settings, seriesFolderFormat: value };
  if (kind === "episode-file") return { ...settings, episodeFileFormat: value };
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

  const dirty = useMemo(() => JSON.stringify(form) !== JSON.stringify(saved), [form, saved]);
  const state: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  const customDrawerDirty = customDrawer !== null && customDrawer.value !== customDrawer.previousValue;
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
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
        episodeFileFormat: form.episodeFileFormat,
        renameOnImport: form.renameOnImport,
        useHardlinks: form.useHardlinks,
        cleanupEmptyFolders: form.cleanupEmptyFolders
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
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={librarySetupNavItems} />

      <ListCard title="Naming" count="The titles people see in your library">
        <div className="grid md:grid-cols-[minmax(0,1.45fr)_minmax(22rem,0.8fr)]">
          <div className="divide-y divide-hairline">
            <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
              <div className="grid content-start gap-1.5">
                <p className="text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">Movie folders</p>
                <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">Used when Deluno creates or renames a movie folder.</p>
              </div>
              <NamingFormatField kind="movie-folder" value={form.movieFolderFormat} onChange={(value) => setForm((current) => ({ ...current, movieFolderFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("movie-folder", active, draftValue, previousValue)} placeholder="{Movie Title} ({Release Year})" showExamples={false} />
            </div>
            <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)] md:grid-cols-2">
              <div className="grid content-start gap-[var(--grid-gap)]">
                <div className="grid content-start gap-1.5">
                  <p className="text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">TV show folders</p>
                  <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">Used when Deluno creates or renames a show folder.</p>
                </div>
                <NamingFormatField kind="series-folder" value={form.seriesFolderFormat} onChange={(value) => setForm((current) => ({ ...current, seriesFolderFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("series-folder", active, draftValue, previousValue)} placeholder="{Series Title} ({Series Year})" showExamples={false} />
              </div>
              <div className="grid content-start gap-[var(--grid-gap)]">
                <div className="grid content-start gap-1.5">
                  <p className="text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">Episode files</p>
                  <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">Used when Deluno renames imported episode files.</p>
                </div>
                <NamingFormatField kind="episode-file" value={form.episodeFileFormat} onChange={(value) => setForm((current) => ({ ...current, episodeFileFormat: value }))} onCustomModeChange={(active, draftValue, previousValue) => handleCustomMode("episode-file", active, draftValue, previousValue)} placeholder="{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}" showExamples={false} />
              </div>
            </div>
          </div>
          <aside className="border-t border-hairline bg-surface-1/20 p-[var(--card-pad-x)] lg:border-l lg:border-t-0">
            <div className="grid gap-[var(--grid-gap)]">
              <div className="grid gap-1">
                <p className="text-[length:var(--type-body-sm)] font-semibold leading-tight text-foreground">Live preview</p>
                <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">See how Deluno will name new and imported media.</p>
              </div>
              <div className="grid gap-[var(--grid-gap)]">
                <div className="grid gap-[var(--grid-gap)] border-b border-hairline pb-[var(--grid-gap)] last:border-b-0 last:pb-0">
                  <NamingPreview label="Movie folder" value={`Movies\\${previewNamingFormat(form.movieFolderFormat || "{Movie Title} ({Release Year})")}`} />
                </div>
                <div className="grid gap-[var(--grid-gap)] border-b border-hairline pb-[var(--grid-gap)] last:border-b-0 last:pb-0">
                  <NamingPreview label="TV show folder" value={`TV Shows\\${previewNamingFormat(form.seriesFolderFormat || "{Series Title} ({Series Year})")}`} />
                </div>
                <div className="grid gap-[var(--grid-gap)] border-b border-hairline pb-[var(--grid-gap)] last:border-b-0 last:pb-0">
                  <NamingPreview label="Episode file" value={previewNamingFormat(form.episodeFileFormat || "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}")} />
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
  const state: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (state === "saving") return;
    setSaveState("saving");
    try {
      const nextSettings = await settingsMutation.mutate({
        renameOnImport: form.renameOnImport,
        useHardlinks: form.useHardlinks,
        cleanupEmptyFolders: form.cleanupEmptyFolders
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
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={librarySetupNavItems} />

      <ListCard title="Import Policy" count="What Deluno does after a download finishes">
        <ImportPolicyFields
          form={form}
          onFormChange={(update) => setForm((current) => update(current))}
        />
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save import settings" />
    </form>
  );
}

function ImportPolicyFields({
  form,
  onFormChange
}: {
  form: PlatformSettingsSnapshot;
  onFormChange: (update: (current: PlatformSettingsSnapshot) => PlatformSettingsSnapshot) => void;
}) {
  return (
    <>
      <div className="grid divide-y divide-hairline md:grid-cols-3 md:divide-x md:divide-y-0">
      <div className="p-[var(--card-pad-x)]">
        <SwitchRow label="Rename files when imported" description="Apply the naming choices from Media Naming as the file enters the library." checked={form.renameOnImport} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, renameOnImport: checked }))} />
      </div>
      <div className="p-[var(--card-pad-x)]">
        <SwitchRow label="Keep seeding without a second copy" description="Use a hardlink when the drives support it, so the download client and library share one set of file data." checked={form.useHardlinks} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, useHardlinks: checked }))} />
      </div>
      <div className="p-[var(--card-pad-x)]">
        <SwitchRow label="Remove empty folders after import" description="Clean up folders left empty by the import. Deluno never removes the download root." checked={form.cleanupEmptyFolders} onCheckedChange={(checked) => onFormChange((current) => ({ ...current, cleanupEmptyFolders: checked }))} />
      </div>
      </div>
    </>
  );
}

function NamingPreview({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid gap-1.5">
      <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-muted-foreground/70">{label}</span>
      <code className="break-words rounded-md border border-hairline bg-surface-1 px-2.5 py-2 text-[length:var(--type-caption)] leading-snug text-foreground">{value}</code>
    </div>
  );
}

/* ============================================== processing workflow */

interface WorkflowForm {
  importWorkflow: string;
  processorName: string;
  processorOutputPath: string;
  processorTimeoutMinutes: string;
  processorFailureMode: string;
  cleanupMode: string;
  removeEmptySourceFolders: boolean;
}
interface CallbackForm {
  name: string;
  provider: string;
  submissionUrl: string;
  authHeaderName: string;
  secret: string;
  isEnabled: boolean;
}
type ProcessingDrawer = { kind: "closed" } | { kind: "library"; id: string } | { kind: "callback" };

function ProcessingWorkflowPage() {
  const { libraries, processorConnections } = useLoaderData() as LoaderData;
  const location = useLocation();
  const navigate = useNavigate();
  const revalidator = useRevalidator();

  const [drawer, setDrawer] = useState<ProcessingDrawer>({ kind: "closed" });
  const [workflow, setWorkflow] = useState<WorkflowForm>(() => emptyWorkflow());
  const [workflowInitial, setWorkflowInitial] = useState<WorkflowForm>(() => emptyWorkflow());
  const [callback, setCallback] = useState<CallbackForm>(emptyCallback);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState<ProcessorConnectionItem | null>(null);
  const [confirmDiscard, setConfirmDiscard] = useState(false);

  const editingLibrary = drawer.kind === "library" ? libraries.find((library) => library.id === drawer.id) ?? null : null;
  const isRefined = workflow.importWorkflow === "refine-before-import";
  const selectedProcessor = workflow.processorName ? processorConnections.find((connection) => connection.name === workflow.processorName) : null;
  const workflowReady = !isRefined || (Boolean(workflow.processorOutputPath.trim()) && (!workflow.processorName || Boolean(selectedProcessor?.isEnabled)));
  const dirty =
    drawer.kind === "library"
      ? JSON.stringify(workflow) !== JSON.stringify(workflowInitial)
      : drawer.kind === "callback"
        ? Boolean(callback.name.trim() || callback.submissionUrl.trim() || callback.secret.trim())
        : false;
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
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
  function openCallback() {
    setDrawer({ kind: "callback" });
    setCallback(emptyCallback());
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
            processorName: workflow.processorName || null,
            processorOutputPath: workflow.processorOutputPath || null,
            processorTimeoutMinutes: Number(workflow.processorTimeoutMinutes || 360),
            processorFailureMode: workflow.processorFailureMode,
            cleanupMode: workflow.cleanupMode,
            removeEmptySourceFolders: workflow.removeEmptySourceFolders
          })
        });
        if (!response.ok) throw new Error("Import workflow could not be saved.");
        setWorkflowInitial(workflow);
        setMessage("Saved just now");
      } else if (drawer.kind === "callback") {
        const response = await authedFetch("/api/integrations/processors/connections", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(callback) });
        if (!response.ok) throw new Error("Processor connection could not be saved.");
        setCallback(emptyCallback());
        setMessage("Processor connected");
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
      if (!response.ok) throw new Error("Processor connection test failed.");
      const result = (await response.json()) as ProcessorConnectionTestResult;
      toast.success(`${connection.name}: ${result.message}`);
    });
  }
  async function removeCallback(connection: ProcessorConnectionItem) {
    await run(`remove:${connection.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/connections/${connection.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Processor connection could not be disconnected.");
    }, `${connection.name} disconnected`);
    setConfirmRemove(null);
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={librarySetupNavItems} actions={<PageToolbarAction variant="outline" onClick={openCallback}>Connect processor</PageToolbarAction>} />

      <ListCard title="Finished download workflow" count="Select a library to choose what happens next">
        {libraries.length === 0 ? (
          <ListEmpty title="No libraries yet" description="Create a library first; each one chooses whether completed downloads import straight away or wait for a processor." />
        ) : (
          <ListTable columns={[{ label: "Library" }, { label: "What happens" }, { label: "Processed folder", width: "minmax(0,1.4fr)" }, { label: "After import" }, { label: "If processing fails" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
            {libraries.map((library) => {
              const refined = (library.importWorkflow ?? "standard") === "refine-before-import";
              const processor = library.processorName ? processorConnections.find((connection) => connection.name === library.processorName) : null;
              const ready = !refined || (Boolean(library.processorOutputPath) && (!library.processorName || Boolean(processor?.isEnabled)));
              return (
                <ListRow key={library.id} onClick={() => openLibrary(library)} selected={drawer.kind === "library" && drawer.id === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={refined ? "Process, then import" : "Import immediately"} secondary={refined ? library.processorName || "Waits for cleaned output" : "Use the finished download"} />
                  <ListCell mono primary={refined ? library.processorOutputPath || <span className="font-sans text-warning">Not set</span> : <span className="font-sans text-muted-foreground">—</span>} secondary={refined ? `Waits up to ${Math.round((library.processorTimeoutMinutes || 360) / 60)} h` : undefined} />
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

      <ListCard title="Processor connections" count="Optional — only when an external processor cleans files for Deluno">
        {processorConnections.length === 0 ? (
          <ListEmpty
            title="No processor connected"
            description="This is optional. Deluno can watch the processed output folder itself. Connect a processor when it needs to receive a job from Deluno and report back when the cleaned file is ready."
            actions={<Button type="button" size="sm" variant="outline" onClick={openCallback}>Connect processor</Button>}
          />
        ) : (
          <ListTable columns={[{ label: "Name" }, { label: "Type" }, { label: "Notifies", width: "minmax(0,1.4fr)" }, { label: "Health", width: LIST_TRACK.status, mobile: true }, { label: "", width: "150px", srOnly: true, mobile: true }]} chevron={false}>
            {processorConnections.map((connection) => (
              <ListRow key={connection.id}>
                <ListNameCell name={connection.name} sub={connection.secretConfigured ? "Token saved" : "No token"} />
                <ListCell primary={connection.provider === "fileflows-webhook" ? "FileFlows webhook" : "Generic processor notification"} />
                <ListCell mono primary={connection.submissionUrl || "—"} />
                <ListCell mobile>
                  <Chip tone={healthTone(connection.healthStatus)}>{connection.healthStatus}</Chip>
                </ListCell>
                <ListCell mobile align="end">
                  <span className="flex justify-end gap-2">
                    <Button type="button" variant="outline" size="sm" className="h-7 px-2" disabled={busy !== null} onClick={() => void testCallback(connection)}>
                      {busy === `test:${connection.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : <Wifi className="h-3 w-3" />}
                      Test
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
      </ListCard>

      <Drawer
        open={drawer.kind !== "closed"}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={drawer.kind === "callback" ? "Connect a processor" : "How to handle finished downloads"}
        description={drawer.kind === "callback" ? "Deluno sends the processor a job, then the processor reports back when the cleaned file is ready." : `${editingLibrary?.name ?? "Library"} · ${editingLibrary?.rootPath ?? ""}`}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={message} saveLabel={drawer.kind === "callback" ? "Connect processor" : "Save workflow"} onCancel={requestClose} disabled={busy !== null || (drawer.kind === "library" && !workflowReady)} />}
      >
        {drawer.kind === "library" ? (
          <>
            <DrawerSection title="Import timing">
              <Field label="When a download finishes" help="Choose whether Deluno should use the download as soon as it is complete, or wait for an external processor such as FileFlows to produce a cleaned file.">
                <SegmentedControl<"standard" | "refine-before-import">
                  value={workflow.importWorkflow as "standard" | "refine-before-import"}
                  onValueChange={(importWorkflow) => setWorkflow((current) => ({ ...current, importWorkflow }))}
                  options={[{ value: "standard", label: "Import immediately" }, { value: "refine-before-import", label: "Wait for processing" }]}
                  aria-label="Import timing"
                />
              </Field>
              <div className="rounded-[10px] border border-hairline bg-surface-1/40 px-3 py-2.5">
                <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">{isRefined ? "Deluno will wait for the cleaned file" : "Deluno will import the finished download"}</p>
                <p className="mt-1 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{isRefined ? "The file is not sent to your library until the output folder contains a matching processed file." : "Deluno places the finished file in this library, applies its naming rules, and then refreshes its details."}</p>
              </div>
            </DrawerSection>
            {isRefined ? (
              <DrawerSection title="External processing">
                <Field label="Processed output folder" help="A separate folder where your processor writes cleaned files. Deluno waits until the file is readable and stable, matches it to the waiting download, then imports it.">
                  <PathInput value={workflow.processorOutputPath} onChange={(value) => setWorkflow((current) => ({ ...current, processorOutputPath: value }))} browseTitle="Choose processed-output folder" />
                </Field>
                <FieldRow>
                  <Field label="Wait up to" help="Then Deluno asks you to review.">
                    <Select value={workflow.processorTimeoutMinutes} onChange={(event) => setWorkflow((current) => ({ ...current, processorTimeoutMinutes: event.target.value }))} options={TIMEOUT_OPTIONS} />
                  </Field>
                  <Field label="If processing fails" help="Deluno keeps the original out of the library unless you explicitly choose Import the original file.">
                    <Select value={workflow.processorFailureMode} onChange={(event) => setWorkflow((current) => ({ ...current, processorFailureMode: event.target.value }))} options={FAILURE_OPTIONS} />
                  </Field>
                </FieldRow>
                <Field label="Completion signal" optional help="Leave this on folder watching unless a connected processor sends a completion message back to Deluno. A disabled connection cannot be selected.">
                  <Select value={workflow.processorName} onChange={(event) => setWorkflow((current) => ({ ...current, processorName: event.target.value }))} placeholder="Watch the folder (no processor message)">
                    {processorConnections.map((connection) => (
                      <option key={connection.id} value={connection.name} disabled={!connection.isEnabled}>
                        {connection.name}
                        {connection.isEnabled ? "" : " (disabled)"}
                      </option>
                    ))}
                    {workflow.processorName && !processorConnections.some((connection) => connection.name === workflow.processorName) ? <option value={workflow.processorName}>{workflow.processorName} (existing)</option> : null}
                  </Select>
                </Field>
                {workflow.processorName && !selectedProcessor?.isEnabled ? <p className="text-[length:var(--type-caption)] text-warning">This processor connection is disabled or missing. Enable it, choose folder watching, or select another connection before saving.</p> : null}
                </DrawerSection>
              ) : null}
            <DrawerSection title="Source cleanup">
              <Field label="After import" help="This is Deluno's own cleanup step after a successful import. It is separate from FileFlows, Cleanarr, or any other processor. The copy in your library stays.">
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
                <p className="text-[length:var(--type-caption)] text-muted-foreground">
                  This covers files Deluno found on disk. Anything it downloaded through a search source is handled by your sharing rule under Automation &amp; Recovery instead, so a download still being shared is never deleted out from under its client.
                </p>
              ) : null}
            </DrawerSection>
          </>
        ) : null}

        {drawer.kind === "callback" ? (
          <>
            <div className="rounded-[10px] border border-info/25 bg-info/[0.05] px-3 py-2.5">
              <p className="text-[length:var(--type-caption)] font-medium text-foreground">Two directions are involved</p>
              <p className="mt-1 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">
                Deluno sends a job to the processor URL below. When the cleaned file is ready, the processor sends an event back to Deluno using the callback path included in that job. For the return message, create a Deluno API key with <span className="font-medium text-foreground">Media automation</span> access under <Link to="/system/api" className="text-info underline underline-offset-2">System → API</Link>.
              </p>
            </div>
            <DrawerSection title="Connection details">
              <FieldRow>
                <Field label="Name" help="A name you'll recognise in Transfers.">
                  <Input value={callback.name} onChange={(event) => setCallback((current) => ({ ...current, name: event.target.value }))} placeholder="Processed media notifier" autoComplete="off" />
                </Field>
                <Field label="Type">
                  <Select value={callback.provider} onChange={(event) => setCallback((current) => ({ ...current, provider: event.target.value }))} options={[{ value: "generic-webhook", label: "Generic processor webhook" }, { value: "fileflows-webhook", label: "FileFlows webhook" }]} />
                </Field>
              </FieldRow>
              <Field label="Processor job URL" help="Deluno sends a job here when a download needs processing. Your processor reports back to Deluno when the cleaned file is ready.">
                <Input value={callback.submissionUrl} onChange={(event) => setCallback((current) => ({ ...current, submissionUrl: event.target.value }))} placeholder="https://processor.example/webhooks/deluno" className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
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
  return { importWorkflow: "standard", processorName: "", processorOutputPath: "", processorTimeoutMinutes: "360", processorFailureMode: "block", cleanupMode: "keep-source", removeEmptySourceFolders: false };
}
function workflowFrom(library: LibraryItem): WorkflowForm {
  return {
    importWorkflow: library.importWorkflow ?? "standard",
    processorName: library.processorName ?? "",
    processorOutputPath: library.processorOutputPath ?? "",
    processorTimeoutMinutes: String(library.processorTimeoutMinutes || 360),
    processorFailureMode: library.processorFailureMode ?? "block",
    cleanupMode: library.cleanupMode ?? "keep-source",
    removeEmptySourceFolders: library.removeEmptySourceFolders ?? false
  };
}
function emptyCallback(): CallbackForm {
  return { name: "", provider: "generic-webhook", submissionUrl: "", authHeaderName: "Authorization", secret: "", isEnabled: true };
}
function healthTone(status: string): NonNullable<ChipProps["tone"]> {
  switch (status) {
    case "healthy":
      return "ok";
    case "degraded":
    case "untested":
      return "warn";
    case "unknown":
      return "muted";
    default:
      return "bad";
  }
}
