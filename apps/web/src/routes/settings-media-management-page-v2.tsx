import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useLocation, useRevalidator } from "react-router-dom";
import { Cable, FolderOpen, Link2, LoaderCircle, Sparkles, Trash2, Workflow } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { PathInput } from "../components/ui/path-input";
import { NamingFormatField } from "../components/app/naming-format-field";
import { settingsOverviewLoader } from "./settings-overview-page";
import { emptyPlatformSettingsSnapshot, fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type ProcessorConnectionItem, type ProcessorConnectionTestResult, type QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

interface SettingsMediaManagementLoaderData extends SettingsOverviewLoaderData {
  processorConnections: ProcessorConnectionItem[];
}

export async function settingsMediaManagementLoader(): Promise<SettingsMediaManagementLoaderData> {
  const [overview, processorConnections] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections")
  ]);
  return { ...overview, processorConnections };
}

export function SettingsMediaManagementPage() {
  const loaderData = useLoaderData() as SettingsMediaManagementLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const location = useLocation();
  const isProcessingPage = location.pathname.startsWith("/settings/processing");
  const { libraries, processorConnections: loadedProcessorConnections, settings } = loaderData;
  const revalidator = useRevalidator();
  const [formState, setFormState] = useState(settings);
  const [busy, setBusy] = useState(false);
  const [workflowBusyKey, setWorkflowBusyKey] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [connectionBusyKey, setConnectionBusyKey] = useState<string | null>(null);
  const [processorConnections, setProcessorConnections] = useState(loadedProcessorConnections);
  const [connectionForm, setConnectionForm] = useState({
    name: "",
    provider: "generic-webhook",
    submissionUrl: "",
    authHeaderName: "Authorization",
    secret: "",
    isEnabled: true
  });

  useEffect(() => {
    setProcessorConnections(loadedProcessorConnections);
  }, [loadedProcessorConnections]);

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setMessage(null);

    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formState)
      });

      if (!response.ok) {
        throw new Error("Media-management settings could not be saved.");
      }

      setMessage("Media-management settings saved.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Media-management settings could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  async function handleSaveWorkflow(
    library: LibraryItem,
    workflow: Pick<LibraryItem, "importWorkflow" | "processorName" | "processorOutputPath" | "processorTimeoutMinutes" | "processorFailureMode">
  ) {
    setWorkflowBusyKey(library.id);
    setMessage(null);

    try {
      const response = await authedFetch(`/api/libraries/${library.id}/workflow`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(workflow)
      });

      if (!response.ok) {
        throw new Error("Import workflow could not be saved.");
      }

      setMessage(`Import workflow saved for ${library.name}.`);
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Import workflow could not be saved.");
    } finally {
      setWorkflowBusyKey(null);
    }
  }

  async function handleCreateConnection(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setConnectionBusyKey("create");
    setMessage(null);
    try {
      const response = await authedFetch("/api/integrations/processors/connections", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(connectionForm)
      });
      if (!response.ok) throw new Error("Processor connection could not be saved.");
      const savedConnection = await response.json() as ProcessorConnectionItem;
      setProcessorConnections((current) => [...current.filter((connection) => connection.id !== savedConnection.id), savedConnection]);
      setConnectionForm({ name: "", provider: "generic-webhook", submissionUrl: "", authHeaderName: "Authorization", secret: "", isEnabled: true });
      setMessage("Optional completion callback saved. Select it only when your existing automation should notify Deluno as soon as processing ends.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Processor connection could not be saved.");
    } finally {
      setConnectionBusyKey(null);
    }
  }

  async function handleTestConnection(connection: ProcessorConnectionItem) {
    setConnectionBusyKey(`test:${connection.id}`);
    setMessage(null);
    try {
      const response = await authedFetch(`/api/integrations/processors/connections/${connection.id}/test`, { method: "POST" });
      if (!response.ok) throw new Error("Processor connection test failed.");
      const result = await response.json() as ProcessorConnectionTestResult;
      setProcessorConnections((current) => current.map((item) => item.id === connection.id
        ? { ...item, healthStatus: result.status, lastHealthMessage: result.message }
        : item));
      setMessage(`${connection.name}: ${result.message}`);
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Processor connection test failed.");
    } finally {
      setConnectionBusyKey(null);
    }
  }

  async function handleDeleteConnection(connection: ProcessorConnectionItem) {
    setConnectionBusyKey(`delete:${connection.id}`);
    setMessage(null);
    try {
      const response = await authedFetch(`/api/integrations/processors/connections/${connection.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Processor connection could not be removed.");
      setProcessorConnections((current) => current.filter((item) => item.id !== connection.id));
      setMessage(`${connection.name} was removed. Libraries using its name will wait for a manual callback until another connection is selected.`);
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Processor connection could not be removed.");
    } finally {
      setConnectionBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title={isProcessingPage ? "Processing workflow" : "File handling & naming"}
      description={isProcessingPage
        ? "Optional: configure the external processing hand-off Deluno uses before it imports and renames the finished file."
        : "Choose how Deluno names completed files and what it does when it imports them into your media library."}
    >
      {message ? (
        <div className="density-help rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-muted-foreground">
          {message}
        </div>
      ) : null}

       <div className="space-y-[var(--page-gap)]">
         {!isProcessingPage ? (
         <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Import and file handling</CardTitle>
            <CardDescription>
              Choose how Deluno names folders and what it should do after a download finishes. Presets are safe defaults; advanced patterns are optional.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handleSave}>
              <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
                <SectionIntro
                  icon={Sparkles}
                  title="Folder naming"
                  copy="Pick the naming style Deluno should use when it creates or renames media. The recommended preset is the safest choice for most libraries."
                />
                <div className="mt-5 grid gap-[var(--grid-gap)]">
                  <Field label="Movie folders" description="Used when Deluno creates or renames a movie folder.">
                  <NamingFormatField
                    kind="movie-folder"
                    value={formState.movieFolderFormat}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, movieFolderFormat: value }))
                    }
                    placeholder="{Movie Title} ({Release Year})"
                  />
                  </Field>
                  <Field label="Series folders" description="Used when Deluno creates or renames a TV show folder.">
                  <NamingFormatField
                    kind="series-folder"
                    value={formState.seriesFolderFormat}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, seriesFolderFormat: value }))
                    }
                    placeholder="{Series Title} ({Series Year})"
                  />
                  </Field>
                  <Field label="Episode files" description="Used when Deluno renames imported episode files.">
                  <NamingFormatField
                    kind="episode-file"
                    value={formState.episodeFileFormat}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, episodeFileFormat: value }))
                    }
                    placeholder="{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}"
                  />
                  </Field>
                </div>
              </div>

              <div className="grid gap-[var(--grid-gap)] lg:grid-cols-[minmax(0,0.95fr)_minmax(0,1.05fr)]">
                <Field
                  label="Default completed-file location"
                  description="Fallback for manual imports or downloads not linked to a client. For a normal client, configure its completed folder in that client and use its File locations only when Deluno sees the same files under a different path."
                >
                  <PathInput
                    value={formState.downloadsPath ?? ""}
                    onChange={(nextValue) =>
                      setFormState((current) => ({ ...current, downloadsPath: nextValue }))
                    }
                    browseTitle="Choose downloads folder"
                  />
                </Field>

                <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
                  <SectionIntro
                    icon={FolderOpen}
                    title="What happens on import"
                    copy="Choose how Deluno should handle files after a download finishes."
                  />
                  <div className="mt-4 grid gap-3 sm:grid-cols-2">
                <ToggleField
                  label="Rename on import"
                      description="Rename files and folders using the presets above."
                  checked={formState.renameOnImport}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, renameOnImport: checked }))
                  }
                />
                <ToggleField
                  label="Use hardlinks"
                      description="Keep seeding while avoiding a second full copy when the filesystem supports it."
                  checked={formState.useHardlinks}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, useHardlinks: checked }))
                  }
                />
                <ToggleField
                  label="Cleanup empty folders"
                      description="Remove leftover empty folders after import."
                  checked={formState.cleanupEmptyFolders}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, cleanupEmptyFolders: checked }))
                  }
                />
                <ToggleField
                  label="Unmonitor at cutoff"
                  description="Stop watching a title after import reaches the selected cutoff quality."
                  checked={formState.unmonitorWhenCutoffMet}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, unmonitorWhenCutoffMet: checked }))
                  }
                />
                  </div>
                </div>
              </div>

              <Button type="submit" disabled={busy}>
                {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Save file handling
              </Button>
            </form>
          </CardContent>
         </Card>
         ) : null}

         {isProcessingPage ? <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Processing workflow by library</CardTitle>
            <CardDescription>
              Choose whether a finished download imports immediately or waits for a cleaner processor output first.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
              <SectionIntro
                icon={Cable}
                title="Optional processor notifications"
                copy="Most processed libraries only need a processed-files folder that Deluno can access. Add a callback only when your existing automation can tell Deluno exactly when a matching output is ready; Deluno does not configure or control FileFlows, Weir, or another processor."
              />
              <form className="mt-4 grid gap-3" onSubmit={handleCreateConnection}>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label="Callback name" description="Use a name you will recognise in Transfers, such as Processed media notifier.">
                    <input value={connectionForm.name} onChange={(event) => setConnectionForm((current) => ({ ...current, name: event.target.value }))} placeholder="Processed media notifier" className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none" />
                  </Field>
                  <Field label="Callback type" description="Use the generic callback contract if you choose to notify Deluno from existing automation.">
                    <select value={connectionForm.provider} onChange={(event) => setConnectionForm((current) => ({ ...current, provider: event.target.value }))} className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none">
                      <option value="generic-webhook">Generic processor webhook</option>
                      <option value="fileflows-webhook">FileFlows webhook (compatible existing setups)</option>
                    </select>
                  </Field>
                </div>
                <Field label="Notification webhook URL" description="Optional endpoint that your existing automation receives when Deluno has a completed download. It can later call Deluno’s processor-event endpoint with the same hand-off ID.">
                  <input value={connectionForm.submissionUrl} onChange={(event) => setConnectionForm((current) => ({ ...current, submissionUrl: event.target.value }))} placeholder="https://processor.example/webhooks/deluno" className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none" />
                </Field>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label="Access token (optional)" description="Stored securely and sent only to this processor. Leave blank when the webhook does not require one.">
                    <input value={connectionForm.secret} onChange={(event) => setConnectionForm((current) => ({ ...current, secret: event.target.value }))} type="password" autoComplete="new-password" className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none" />
                  </Field>
                  <Field label="Token header" description="Authorization sends a Bearer token. Use a processor-specific header, such as X-Api-Key, when required.">
                    <input value={connectionForm.authHeaderName} onChange={(event) => setConnectionForm((current) => ({ ...current, authHeaderName: event.target.value }))} className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none" />
                  </Field>
                </div>
                <div className="flex justify-end"><Button type="submit" size="sm" disabled={connectionBusyKey !== null}>{connectionBusyKey === "create" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null} Save optional callback</Button></div>
              </form>
              {processorConnections.length ? (
                <div className="mt-4 space-y-2">
                  {processorConnections.map((connection) => (
                    <div key={connection.id} className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-hairline bg-background/30 p-3 text-sm">
                      <div><p className="font-semibold text-foreground">{connection.name}</p><p className="mt-1 text-muted-foreground">{connection.provider === "fileflows-webhook" ? "FileFlows webhook" : "Generic callback"} · {connection.healthStatus} · {connection.secretConfigured ? "token saved" : "no token"}</p></div>
                      <div className="flex gap-2"><Button type="button" size="sm" variant="outline" aria-label={`Test processor connection ${connection.name}`} disabled={connectionBusyKey !== null} onClick={() => void handleTestConnection(connection)}>{connectionBusyKey === `test:${connection.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null} Test</Button><Button type="button" size="sm" variant="outline" aria-label={`Remove processor connection ${connection.name}`} disabled={connectionBusyKey !== null} onClick={() => void handleDeleteConnection(connection)}><Trash2 className="h-4 w-4" /> Remove</Button></div>
                    </div>
                  ))}
                </div>
              ) : <p className="mt-4 text-sm text-muted-foreground">No callback configured. That is normal: Deluno can watch the processed-files folder directly without calling the processor or requiring it to call back.</p>}
            </div>
            <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
              <SectionIntro
                icon={Workflow}
                title="Standard or refined"
              copy="Standard import sends a completed download straight to its final destination. Process before import is for workflows where another app independently cleans audio, subtitles, or tracks before Deluno imports and renames the finished file."
              />
            </div>
            {libraries.map((library) => (
              <LibraryWorkflowCard
                key={library.id}
                library={library}
                processorConnections={processorConnections}
                busy={workflowBusyKey === library.id}
                onSave={handleSaveWorkflow}
              />
            ))}
          </CardContent>
        </Card> : null}
      </div>
    </SettingsShell>
  );
}

function LibraryWorkflowCard({
  busy,
  library,
  processorConnections,
  onSave
}: {
  busy: boolean;
  library: LibraryItem;
  processorConnections: ProcessorConnectionItem[];
  onSave: (
    library: LibraryItem,
    workflow: Pick<LibraryItem, "importWorkflow" | "processorName" | "processorOutputPath" | "processorTimeoutMinutes" | "processorFailureMode">
  ) => Promise<void>;
}) {
  const [state, setState] = useState({
    importWorkflow: library.importWorkflow ?? "standard",
    processorName: library.processorName ?? "",
    processorOutputPath: library.processorOutputPath ?? "",
    processorTimeoutMinutes: library.processorTimeoutMinutes || 360,
    processorFailureMode: library.processorFailureMode ?? "block"
  });
  const isRefined = state.importWorkflow === "refine-before-import";

  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="font-display text-base font-semibold text-foreground">{library.name}</p>
          <p className="density-help mt-1 text-muted-foreground">{library.rootPath}</p>
        </div>
        <span className="rounded-full border border-hairline px-2.5 py-1 text-xs text-muted-foreground">
          {library.mediaType === "tv" ? "TV" : "Movies"}
        </span>
      </div>

      <div className="mt-4 grid gap-3">
        <Field label="Workflow" description="Pick how Deluno should treat completed downloads for this library.">
          <select
            value={state.importWorkflow}
            onChange={(event) => setState((current) => ({ ...current, importWorkflow: event.target.value }))}
            className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
          >
            <option value="standard">Standard import</option>
            <option value="refine-before-import">Process before import</option>
          </select>
        </Field>

        {isRefined ? (
          <>
            <Field label="Optional completion callback" description="Leave this off for the normal watched-folder flow. Select one only when existing automation can notify Deluno with the matching hand-off ID.">
              <select
                value={state.processorName}
                onChange={(event) => setState((current) => ({ ...current, processorName: event.target.value }))}
                className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
              >
                <option value="">No callback — watch the processed-output folder</option>
                {processorConnections.map((connection) => <option key={connection.id} value={connection.name}>{connection.name}{connection.isEnabled ? "" : " (disabled)"}</option>)}
                {state.processorName && !processorConnections.some((connection) => connection.name === state.processorName) ? <option value={state.processorName}>{state.processorName} (existing optional callback)</option> : null}
              </select>
            </Field>
            <Field label="Processed-files folder Deluno sees" description="Your external processor writes cleaned files here. Deluno watches this accessible path and only imports a file when it can match it to the waiting download. If FileFlows sees this folder under a different path, configure that mount in FileFlows so this is the path Deluno can read.">
              <PathInput
                value={state.processorOutputPath}
                onChange={(nextValue) => setState((current) => ({ ...current, processorOutputPath: nextValue }))}
                browseTitle="Choose processed-output folder"
              />
            </Field>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Wait time" description="How long Deluno should wait before asking for review.">
                <select
                  value={String(state.processorTimeoutMinutes)}
                  onChange={(event) => setState((current) => ({ ...current, processorTimeoutMinutes: Number(event.target.value) }))}
                  className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                >
                  <option value="60">1 hour</option>
                  <option value="180">3 hours</option>
                  <option value="360">6 hours</option>
                  <option value="720">12 hours</option>
                  <option value="1440">24 hours</option>
                </select>
              </Field>
              <Field label="If cleaning fails" description="Choose the safe fallback for failed processing jobs.">
                <select
                  value={state.processorFailureMode}
                  onChange={(event) => setState((current) => ({ ...current, processorFailureMode: event.target.value }))}
                  className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                >
                  <option value="block">Stop and ask me</option>
                  <option value="manual-review">Send to manual review</option>
                  <option value="import-original">Import the original file</option>
                </select>
              </Field>
            </div>
          </>
        ) : (
          <p className="rounded-xl border border-hairline bg-background/30 px-3 py-2.5 text-sm text-muted-foreground">
            Completed downloads will go straight through Deluno&apos;s destination resolver, import mover, rename rules, and metadata refresh.
          </p>
        )}
      </div>

      <div className="mt-4 flex justify-end">
        <Button
          type="button"
          size="sm"
          disabled={busy || (isRefined && !state.processorOutputPath.trim())}
          onClick={() => void onSave(library, state)}
        >
          {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
          Save workflow
        </Button>
      </div>
    </div>
  );
}

function Field({
  children,
  description,
  label
}: {
  children: ReactNode;
  description?: string;
  label: string;
}) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-background/30">
      <p className="density-label font-semibold uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      {description ? <p className="density-help mt-1 text-muted-foreground">{description}</p> : null}
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
    </div>
  );
}

function ToggleField({
  checked,
  description,
  label,
  onChange
}: {
  checked: boolean;
  description: string;
  label: string;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="density-field density-control-text flex min-h-[84px] cursor-pointer items-start gap-3 rounded-xl border border-hairline bg-background/30 text-foreground transition hover:border-primary/25 hover:bg-surface-2">
      <input className="mt-1" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span>
        <span className="flex items-center gap-2 font-semibold">
          {label === "Use hardlinks" ? <Link2 className="h-4 w-4 text-primary" /> : null}
          {label === "Remove completed downloads" ? <Trash2 className="h-4 w-4 text-muted-foreground" /> : null}
          {label}
        </span>
        <span className="mt-1 block leading-relaxed text-muted-foreground">{description}</span>
      </span>
    </label>
  );
}

function SectionIntro({
  icon: Icon,
  title,
  copy
}: {
  icon: typeof Sparkles;
  title: string;
  copy: string;
}) {
  return (
    <div className="flex gap-3">
      <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
        <Icon className="h-5 w-5" />
      </div>
      <div>
        <h2 className="font-display text-lg font-semibold tracking-tight text-foreground">{title}</h2>
        <p className="density-help mt-1 max-w-3xl text-muted-foreground">{copy}</p>
      </div>
    </div>
  );
}
