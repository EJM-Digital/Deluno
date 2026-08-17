/**
 * File handling & naming, and Processing workflow — two routes, one module.
 *
 *   File handling  → page form: Folder naming · Import behaviour · PageFooter
 *   Processing     → ListCard of libraries → drawer (workflow per library),
 *                    plus optional completion callbacks as their own list.
 *
 * Contracts: PUT /api/settings, PUT /api/libraries/{id}/workflow,
 * GET/POST/DELETE /api/integrations/processors/connections (+ /{id}/test).
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useLocation, useRevalidator } from "react-router-dom";
import { Loader2, Plus, Wifi } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { NamingFormatField } from "../components/app/naming-format-field";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { settingsOverviewLoader } from "./settings-overview-page";
import { fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type ProcessorConnectionItem, type ProcessorConnectionTestResult, type QualityProfileItem } from "../lib/api";
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
  return location.pathname.startsWith("/settings/processing") ? <ProcessingWorkflowPage /> : <FileHandlingPage />;
}

/* ==================================================== file handling */

function FileHandlingPage() {
  const { settings } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
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
      const response = await authedFetch("/api/settings", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(form) });
      if (!response.ok) throw new Error("File handling could not be saved.");
      setSaved(form);
      setSaveState("saved");
      setMessage("Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={librarySetupNavItems} />

      <ListCard title="Folder and file naming" count="How Deluno names media when it creates or renames it">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Movie folders" help="Used when Deluno creates or renames a movie folder.">
            <NamingFormatField kind="movie-folder" value={form.movieFolderFormat} onChange={(value) => setForm((current) => ({ ...current, movieFolderFormat: value }))} placeholder="{Movie Title} ({Release Year})" />
          </Field>
          <Field label="Series folders" help="Used when Deluno creates or renames a TV show folder.">
            <NamingFormatField kind="series-folder" value={form.seriesFolderFormat} onChange={(value) => setForm((current) => ({ ...current, seriesFolderFormat: value }))} placeholder="{Series Title} ({Series Year})" />
          </Field>
          <Field label="Episode files" help="Used when Deluno renames imported episode files.">
            <NamingFormatField kind="episode-file" value={form.episodeFileFormat} onChange={(value) => setForm((current) => ({ ...current, episodeFileFormat: value }))} placeholder="{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}" />
          </Field>
        </div>
      </ListCard>

      <ListCard title="On import" count="What Deluno does once a download finishes">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <SwitchRow label="Rename on import" description="Rename files and folders using the patterns above." checked={form.renameOnImport} onCheckedChange={(checked) => setForm((current) => ({ ...current, renameOnImport: checked }))} />
          <SwitchRow label="Use hardlinks" description="Keep seeding without a second full copy, when the filesystem supports it." checked={form.useHardlinks} onCheckedChange={(checked) => setForm((current) => ({ ...current, useHardlinks: checked }))} />
          <SwitchRow label="Clean up empty folders" description="Remove leftover empty folders after an import." checked={form.cleanupEmptyFolders} onCheckedChange={(checked) => setForm((current) => ({ ...current, cleanupEmptyFolders: checked }))} />
          <SwitchRow label="Unmonitor at cutoff" description="Stop watching a title once its file reaches the cutoff quality." checked={form.unmonitorWhenCutoffMet} onCheckedChange={(checked) => setForm((current) => ({ ...current, unmonitorWhenCutoffMet: checked }))} />
          <Field label="Default completed-file location" optional help="Fallback for manual imports and downloads not linked to a client. For a normal client, set its completed folder in the client itself and use File locations on Connections when Deluno sees those files under a different path.">
            <PathInput value={form.downloadsPath ?? ""} onChange={(value) => setForm((current) => ({ ...current, downloadsPath: value }))} browseTitle="Choose downloads folder" />
          </Field>
        </div>
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save file handling" onDiscard={() => setForm(saved)} />
    </form>
  );
}

/* ============================================== processing workflow */

interface WorkflowForm {
  importWorkflow: string;
  processorName: string;
  processorOutputPath: string;
  processorTimeoutMinutes: string;
  processorFailureMode: string;
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
  const dirty =
    drawer.kind === "library"
      ? JSON.stringify(workflow) !== JSON.stringify(workflowInitial)
      : drawer.kind === "callback"
        ? Boolean(callback.name.trim() || callback.submissionUrl.trim() || callback.secret.trim())
        : false;
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  const blocker = useUnsavedChanges(dirty);
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
            processorFailureMode: workflow.processorFailureMode
          })
        });
        if (!response.ok) throw new Error("Import workflow could not be saved.");
        setWorkflowInitial(workflow);
        setMessage("Saved just now");
      } else if (drawer.kind === "callback") {
        const response = await authedFetch("/api/integrations/processors/connections", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(callback) });
        if (!response.ok) throw new Error("Callback could not be saved.");
        setCallback(emptyCallback());
        setMessage("Callback saved");
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
      if (!response.ok) throw new Error("Callback test failed.");
      const result = (await response.json()) as ProcessorConnectionTestResult;
      toast.success(`${connection.name}: ${result.message}`);
    });
  }
  async function removeCallback(connection: ProcessorConnectionItem) {
    await run(`remove:${connection.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/connections/${connection.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Callback could not be removed.");
    }, `${connection.name} removed`);
    setConfirmRemove(null);
  }

  const isRefined = workflow.importWorkflow === "refine-before-import";

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={librarySetupNavItems} actions={<Button type="button" variant="outline" onClick={openCallback}><Plus className="h-4 w-4" />New callback</Button>} />

      <ListCard title="Import workflow" count="Standard import, or wait for a processor to clean the file first">
        {libraries.length === 0 ? (
          <ListEmpty title="No libraries yet" description="Create a library first; each one chooses whether completed downloads import straight away or wait for a processor." />
        ) : (
          <ListTable columns={[{ label: "Library" }, { label: "Workflow" }, { label: "Processed folder", width: "minmax(0,1.4fr)" }, { label: "If it fails" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
            {libraries.map((library) => {
              const refined = (library.importWorkflow ?? "standard") === "refine-before-import";
              const ready = !refined || Boolean(library.processorOutputPath);
              return (
                <ListRow key={library.id} onClick={() => openLibrary(library)} selected={drawer.kind === "library" && drawer.id === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={refined ? "Process before import" : "Standard import"} secondary={refined ? library.processorName || "Watches the folder — no callback" : "Straight to the library"} />
                  <ListCell mono primary={refined ? library.processorOutputPath || <span className="font-sans text-warning">Not set</span> : <span className="font-sans text-muted-foreground">—</span>} secondary={refined ? `Waits up to ${Math.round((library.processorTimeoutMinutes || 360) / 60)} h` : undefined} />
                  <ListCell primary={refined ? FAILURE_OPTIONS.find((option) => option.value === library.processorFailureMode)?.label ?? library.processorFailureMode : <span className="text-muted-foreground">—</span>} />
                  <ListCell mobile>
                    <Chip tone={ready ? (refined ? "info" : "ok") : "warn"}>{refined ? (ready ? "Refined" : "Needs a folder") : "Standard"}</Chip>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Completion callbacks" count="Optional — only when your automation can tell Deluno a file is ready">
        {processorConnections.length === 0 ? (
          <ListEmpty
            title="No callback configured"
            description="This is the normal setup: Deluno watches the processed-files folder directly and never needs to call your processor, or be called by it."
            actions={<Button type="button" size="sm" variant="outline" onClick={openCallback}><Plus className="h-3.5 w-3.5" />New callback</Button>}
          />
        ) : (
          <ListTable columns={[{ label: "Name" }, { label: "Type" }, { label: "Notifies", width: "minmax(0,1.4fr)" }, { label: "Health", width: LIST_TRACK.status, mobile: true }, { label: "", width: "150px", srOnly: true, mobile: true }]} chevron={false}>
            {processorConnections.map((connection) => (
              <ListRow key={connection.id}>
                <ListNameCell name={connection.name} sub={connection.secretConfigured ? "Token saved" : "No token"} />
                <ListCell primary={connection.provider === "fileflows-webhook" ? "FileFlows webhook" : "Generic callback"} />
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
        title={drawer.kind === "callback" ? "New completion callback" : editingLibrary?.name ?? "Import workflow"}
        description={drawer.kind === "callback" ? "Let existing automation tell Deluno the moment a processed file is ready." : `Import workflow · ${editingLibrary?.rootPath ?? ""}`}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={message} saveLabel={drawer.kind === "callback" ? "Save callback" : "Save workflow"} onCancel={requestClose} disabled={busy !== null || (drawer.kind === "library" && isRefined && !workflow.processorOutputPath.trim())} />}
      >
        {drawer.kind === "library" ? (
          <>
            <DrawerSection title="Workflow">
              <Field label="When a download finishes" help={isRefined ? "Deluno waits for a cleaned file in the processed folder, matches it to the download, then imports and renames it." : "Completed downloads go straight through destination routing, import, rename and metadata refresh."}>
                <Select value={workflow.importWorkflow} onChange={(event) => setWorkflow((current) => ({ ...current, importWorkflow: event.target.value }))} options={[{ value: "standard", label: "Standard import" }, { value: "refine-before-import", label: "Process before import" }]} />
              </Field>
            </DrawerSection>
            {isRefined ? (
              <DrawerSection title="Processing">
                <Field label="Processed-files folder Deluno can see" help="Your processor writes cleaned files here. Deluno imports one only when it matches a waiting download.">
                  <PathInput value={workflow.processorOutputPath} onChange={(value) => setWorkflow((current) => ({ ...current, processorOutputPath: value }))} browseTitle="Choose processed-output folder" />
                </Field>
                <FieldRow>
                  <Field label="Wait up to" help="Then Deluno asks you to review.">
                    <Select value={workflow.processorTimeoutMinutes} onChange={(event) => setWorkflow((current) => ({ ...current, processorTimeoutMinutes: event.target.value }))} options={TIMEOUT_OPTIONS} />
                  </Field>
                  <Field label="If processing fails">
                    <Select value={workflow.processorFailureMode} onChange={(event) => setWorkflow((current) => ({ ...current, processorFailureMode: event.target.value }))} options={FAILURE_OPTIONS} />
                  </Field>
                </FieldRow>
                <Field label="Completion callback" optional help="Leave as watched-folder unless your automation can notify Deluno with the hand-off id.">
                  <Select value={workflow.processorName} onChange={(event) => setWorkflow((current) => ({ ...current, processorName: event.target.value }))} placeholder="No callback — watch the folder">
                    {processorConnections.map((connection) => (
                      <option key={connection.id} value={connection.name}>
                        {connection.name}
                        {connection.isEnabled ? "" : " (disabled)"}
                      </option>
                    ))}
                    {workflow.processorName && !processorConnections.some((connection) => connection.name === workflow.processorName) ? <option value={workflow.processorName}>{workflow.processorName} (existing)</option> : null}
                  </Select>
                </Field>
              </DrawerSection>
            ) : null}
          </>
        ) : null}

        {drawer.kind === "callback" ? (
          <>
            <DrawerSection title="Basics">
              <FieldRow>
                <Field label="Name" help="A name you'll recognise in Transfers.">
                  <Input value={callback.name} onChange={(event) => setCallback((current) => ({ ...current, name: event.target.value }))} placeholder="Processed media notifier" autoComplete="off" />
                </Field>
                <Field label="Type">
                  <Select value={callback.provider} onChange={(event) => setCallback((current) => ({ ...current, provider: event.target.value }))} options={[{ value: "generic-webhook", label: "Generic processor webhook" }, { value: "fileflows-webhook", label: "FileFlows webhook" }]} />
                </Field>
              </FieldRow>
              <Field label="Notification URL" help="Deluno posts here when a completed download is waiting; your automation calls back with the same hand-off id.">
                <Input value={callback.submissionUrl} onChange={(event) => setCallback((current) => ({ ...current, submissionUrl: event.target.value }))} placeholder="https://processor.example/webhooks/deluno" className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
              </Field>
            </DrawerSection>
            <DrawerSection title="Authentication">
              <FieldRow>
                <Field label="Access token" optional help="Stored encrypted and sent only to this processor.">
                  <Input type="password" value={callback.secret} onChange={(event) => setCallback((current) => ({ ...current, secret: event.target.value }))} autoComplete="new-password" />
                </Field>
                <Field label="Token header" help="Authorization sends a Bearer token; use X-Api-Key when required.">
                  <Input value={callback.authHeaderName} onChange={(event) => setCallback((current) => ({ ...current, authHeaderName: event.target.value }))} />
                </Field>
              </FieldRow>
              <SwitchRow label="Enabled" description="Disabled callbacks stay configured but are never called." checked={callback.isEnabled} onCheckedChange={(checked) => setCallback((current) => ({ ...current, isEnabled: checked }))} />
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
        description="Libraries pointing at it fall back to watching the processed folder until another callback is chosen."
        confirmLabel="Remove callback"
        busy={busy?.startsWith("remove:") ?? false}
        onConfirm={() => confirmRemove && void removeCallback(confirmRemove)}
      />
      <ConfirmDialog
        open={confirmDiscard || blocker.state === "blocked"}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
          if (blocker.state === "blocked") blocker.reset();
        }}
        title="Discard unsaved changes?"
        description="Your edits haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          if (blocker.state === "blocked") {
            setDrawer({ kind: "closed" });
            blocker.proceed();
          } else {
            closeDrawer();
          }
        }}
      />
    </div>
  );
}

/* --------------------------------------------------------------- utils */

function emptyWorkflow(): WorkflowForm {
  return { importWorkflow: "standard", processorName: "", processorOutputPath: "", processorTimeoutMinutes: "360", processorFailureMode: "block" };
}
function workflowFrom(library: LibraryItem): WorkflowForm {
  return {
    importWorkflow: library.importWorkflow ?? "standard",
    processorName: library.processorName ?? "",
    processorOutputPath: library.processorOutputPath ?? "",
    processorTimeoutMinutes: String(library.processorTimeoutMinutes || 360),
    processorFailureMode: library.processorFailureMode ?? "block"
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
