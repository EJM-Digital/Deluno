import { settleFormAfterSave } from "../../lib/settle-form-after-save";
import { SecretUnreadableNotice } from "./secret-unreadable-notice";
import { useEffect, useState, type FormEvent } from "react";
import { Copy, Loader2, Wifi } from "lucide-react";
import { fetchJson, readValidationProblem } from "../../lib/api";
import type { LibraryItem, QualityProfileItem } from "../../lib/api/types";
import { authedFetch } from "../../lib/use-auth";
import { resolveDrawerSaveState, shouldClearSaveState } from "../../lib/drawer-save-state";
import { useUnsavedChanges } from "../../hooks/use-unsaved-changes";
import { healthChip, relative } from "../../routes/connections/format";
import { TagPicker } from "./tag-picker";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../ui/drawer";
import { Field, FieldRow } from "../ui/field";
import { Input } from "../ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { Select } from "../ui/select";
import { Switch, SwitchRow } from "../ui/switch";
import { ConnectionVersionBeside } from "./connection-version";
import { toneSortValue } from "../../lib/status-tones";
import { ConnectionUpdatePanel } from "./connection-update-panel";
import { useConnectionVersions, versionKey } from "../../lib/connection-versions";

/** Mirrors `Deluno.Connections.Contracts.SeerrConnection`. */
export interface SeerrServer {
  id: string;
  name: string;
  baseUrl: string;
  hasApiKey: boolean;
  /** Saved on another computer and cannot be read here (#1094): listed as missing until entered again. */
  secretUnreadable?: boolean;
  secretProblem?: string | null;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthLatencyMs: number | null;
  lastHealthTestUtc: string | null;
  serverName: string | null;
  version: string | null;
  /**
   * #1033: where an approved request lands when no destination rule matches
   * it — the fallback library and quality profile, the last resort before
   * Deluno refuses rather than guessing. Ordinary destination rules
   * (genre, anime, original language, "requested in 4K", …) are tried
   * first, on the Destination Rules page.
   */
  movieLibraryId: string | null;
  movieQualityProfileId: string | null;
  seriesLibraryId: string | null;
  seriesQualityProfileId: string | null;
  /** #851: whether a webhook secret has been generated for this connection yet. */
  hasWebhookSecret: boolean;
  /** #1046: comma-separated tag names given to every title this connection adds. */
  tags: string;
  /** #1046: extra tag names given only when the request was for 4K. */
  tags4k: string;
}

/** Mirrors `Deluno.Connections.Contracts.SeerrAcquisitionItem` (#851/#852). */
export interface SeerrAcquisition {
  id: string;
  connectionId: string;
  connectionName: string;
  mediaType: string;
  entityId: string;
  title: string;
  seerrMediaId: number;
  status: "added" | "failed" | "available" | "available-failed" | string;
  message: string | null;
  createdUtc: string;
  updatedUtc: string;
  /** #985: who asked for it in Seerr, the way Seerr showed them; null when the webhook sent none. */
  requestedBy: string | null;
  /** #1033: the destination rule that placed this request, in plain words; null when the fallback library was used instead. */
  ruleId: string | null;
  ruleName: string | null;
}

/** Mirrors `Deluno.Retention.RetentionSeerrCleanup`. */
export interface SeerrCleanup {
  serverId: string;
  entityId: string;
  title: string;
  attempts: number;
  problem: string | null;
  attemptedUtc: string;
}

interface SeerrTestResult {
  ok: boolean;
  message: string;
}

interface SeerrForm {
  name: string;
  baseUrl: string;
  apiKey: string;
  isEnabled: boolean;
  movieLibraryId: string;
  movieQualityProfileId: string;
  seriesLibraryId: string;
  seriesQualityProfileId: string;
  tags: string;
  tags4k: string;
}

function formFrom(server: SeerrServer | null): SeerrForm {
  return server
    ? {
        name: server.name,
        baseUrl: server.baseUrl,
        apiKey: "",
        isEnabled: server.isEnabled,
        movieLibraryId: server.movieLibraryId ?? "",
        movieQualityProfileId: server.movieQualityProfileId ?? "",
        seriesLibraryId: server.seriesLibraryId ?? "",
        seriesQualityProfileId: server.seriesQualityProfileId ?? "",
        tags: server.tags ?? "",
        tags4k: server.tags4k ?? ""
      }
    : {
        name: "Seerr",
        baseUrl: "",
        apiKey: "",
        isEnabled: true,
        movieLibraryId: "",
        movieQualityProfileId: "",
        seriesLibraryId: "",
        seriesQualityProfileId: "",
        tags: "",
        tags4k: ""
      };
}

function same(left: SeerrForm, right: SeerrForm) {
  return (Object.keys(left) as (keyof SeerrForm)[]).every((key) => left[key] === right[key]);
}

/**
 * Jellyseerr and Overseerr (#522), beside the media servers they sit in front
 * of. Retention rules read who requested what; a title a rule removes is
 * cleared from Seerr, and a clearing Seerr refused is listed on its server
 * until it goes through.
 */
export function SeerrServersCard({
  servers,
  cleanups,
  libraries,
  qualityProfiles,
  onChanged,
  openId,
  onOpened
}: {
  servers: SeerrServer[];
  cleanups: SeerrCleanup[];
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  onChanged: () => void;
  /** A connection to open as soon as it is listed (from a notice about its newer version, #1118). */
  openId?: string | null;
  onOpened?: () => void;
}) {
  const { versions } = useConnectionVersions();
  const [drawer, setDrawer] = useState<{ open: boolean; id: string | null }>({ open: false, id: null });
  const [form, setForm] = useState<SeerrForm>(formFrom(null));
  const [initial, setInitial] = useState<SeerrForm>(formFrom(null));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<SeerrTestResult | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [webhookSecret, setWebhookSecret] = useState<string | null>(null);
  const [copied, setCopied] = useState<"url" | "secret" | null>(null);

  const editing = drawer.id ? servers.find((server) => server.id === drawer.id) ?? null : null;
  const owed = editing ? cleanups.filter((cleanup) => cleanup.serverId === editing.id) : [];
  const dirty = drawer.open && !same(form, initial);
  const footerState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  function open(server: SeerrServer | null) {
    const next = formFrom(server);
    setDrawer({ open: true, id: server?.id ?? null });
    setForm(next);
    setInitial(next);
    setErrors({});
    setSaveState(undefined);
    setSaveMessage(null);
    setTestResult(null);
    setWebhookSecret(null);
    setCopied(null);
  }

  // A notice about a newer version opens this Seerr server (#1118).
  useEffect(() => {
    if (!openId || drawer.open) return;
    const found = servers.find((item) => item.id === openId);
    if (!found) return;
    open(found);
    onOpened?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openId, drawer.open, servers]);
  function close() {
    setDrawer({ open: false, id: null });
    setConfirmDiscard(false);
  }

  async function fieldErrors(response: Response) {
    const problem = await readValidationProblem(response.clone()).catch(() => null);
    if (problem?.errors) setErrors(Object.fromEntries(Object.entries(problem.errors).map(([key, value]) => [key, value[0] ?? ""])));
    return Boolean(problem?.errors);
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaveState("saving");
    setSaveMessage(null);
    setErrors({});
    try {
      const response = await authedFetch(editing ? `/api/seerr-servers/${editing.id}` : "/api/seerr-servers", {
        method: editing ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(form)
      });
      if (!response.ok) throw new Error((await fieldErrors(response)) ? "Check the highlighted fields." : "The server could not be saved.");
      const saved = (await response.json()) as SeerrServer;
      const settled = formFrom(saved);
      setForm((current) => settleFormAfterSave(current, form, settled));
      setInitial(settled);
      setDrawer({ open: true, id: saved.id });
      setSaveState("saved");
      setSaveMessage(editing ? "Saved just now" : "Server added");
      onChanged();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  async function test() {
    setBusy("test");
    setTestResult(null);
    try {
      const response = await authedFetch(editing ? `/api/seerr-servers/${editing.id}/test` : "/api/seerr-servers/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(form)
      });
      if (!response.ok) {
        setTestResult({ ok: false, message: (await fieldErrors(response)) ? "Fill in the highlighted fields first." : `The test could not run (HTTP ${response.status}).` });
        return;
      }
      setTestResult((await response.json()) as SeerrTestResult);
      if (editing) onChanged();
    } catch {
      setTestResult({ ok: false, message: "The test could not run. Check that Deluno is still reachable." });
    } finally {
      setBusy(null);
    }
  }

  async function regenerateWebhookSecret() {
    if (!editing) return;
    setBusy("webhook-secret");
    try {
      const response = await authedFetch(`/api/seerr-servers/${editing.id}/webhook-secret`, { method: "POST" });
      if (!response.ok) throw new Error();
      const result = (await response.json()) as { secret: string };
      setWebhookSecret(result.secret);
      onChanged();
    } catch {
      setSaveState("error");
      setSaveMessage("The webhook secret could not be generated.");
    } finally {
      setBusy(null);
    }
  }

  async function copyToClipboard(value: string, which: "url" | "secret") {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(which);
      setTimeout(() => setCopied((current) => (current === which ? null : current)), 2000);
    } catch {
      // Clipboard access can be denied by the browser; the value is still selectable text.
    }
  }

  async function toggle(server: SeerrServer, isEnabled: boolean) {
    setBusy(`toggle:${server.id}`);
    try {
      await authedFetch(`/api/seerr-servers/${server.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: server.name,
          baseUrl: server.baseUrl,
          apiKey: "",
          isEnabled,
          movieLibraryId: server.movieLibraryId,
          movieQualityProfileId: server.movieQualityProfileId,
          seriesLibraryId: server.seriesLibraryId,
          seriesQualityProfileId: server.seriesQualityProfileId
        })
      });
      onChanged();
    } finally {
      setBusy(null);
    }
  }

  async function remove() {
    if (!editing) return;
    setBusy("remove");
    try {
      const response = await authedFetch(`/api/seerr-servers/${editing.id}`, { method: "DELETE" });
      if (!response.ok) throw new Error();
      setConfirmRemove(false);
      setInitial(form);
      close();
      onChanged();
    } catch {
      setSaveState("error");
      setSaveMessage("The server could not be removed.");
      setConfirmRemove(false);
    } finally {
      setBusy(null);
    }
  }

  return (
    <>
      <ListCard
        title="Request servers"
        count={servers.length ? `${servers.filter((server) => server.isEnabled && server.healthStatus === "healthy").length} of ${servers.length} working` : undefined}
        actions={<Button type="button" variant="outline" size="sm" onClick={() => open(null)}>Add Seerr</Button>}
      >
        {servers.length === 0 ? (
          <ListEmpty title="No Seerr server" description="Add Jellyseerr or Overseerr so retention rules can read who requested a title, and so a title a rule removes stops showing as available there." />
        ) : (
          <ListTable
            listId="seerr-servers"
            items={servers}
            columns={[
              { id: "server", label: "Server", sort: (server) => server.name.toLowerCase() },
              { id: "address", label: "Address", sort: (server) => server.baseUrl.toLowerCase() },
              { id: "reports", label: "Reports itself as", sort: (server) => (server.serverName ?? "").toLowerCase() },
              { id: "lastTest", label: "Last test", sort: (server) => (server.lastHealthTestUtc ? new Date(server.lastHealthTestUtc) : null) },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (server) => { const chip = healthChip(server); return toneSortValue(chip.tone, chip.label); } },
              { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (server) => server.isEnabled }
            ]}
          >
            {(rows) => rows.map((server) => {
              const chip = healthChip(server);
              const waiting = cleanups.filter((cleanup) => cleanup.serverId === server.id);
              return (
                <ListRow key={server.id} onClick={() => open(server)} selected={drawer.open && drawer.id === server.id}>
                  <ListNameCell name={<>{server.name}<ConnectionVersionBeside info={versions.get(versionKey("requests", server.id))} fallback={server.version} /></>} sub="Seerr" />
                  <ListCell mono primary={server.baseUrl} />
                  <ListCell primary={server.serverName ?? <span className="text-muted-foreground">Not tested yet</span>} />
                  <ListCell
                    primary={relative(server.lastHealthTestUtc)}
                    secondary={waiting.length ? `${waiting.length} removed ${waiting.length === 1 ? "title" : "titles"} still to clear: ${waiting[0].problem ?? "waiting"}` : server.lastHealthMessage ?? undefined}
                  />
                  <ListCell mobile>
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                  </ListCell>
                  <ListCell mobile>
                    <Switch size="sm" aria-label={`${server.isEnabled ? "Pause" : "Enable"} ${server.name}`} checked={server.isEnabled} disabled={busy === `toggle:${server.id}`} onCheckedChange={(checked) => void toggle(server, checked)} />
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={drawer.open}
        onOpenChange={(next) => {
          if (!next) {
            if (dirty) setConfirmDiscard(true);
            else close();
          }
        }}
        title={editing?.name ?? (form.name || "New Seerr server")}
        description={editing ? `Seerr · ${editing.baseUrl}` : "Jellyseerr or Overseerr, where people request titles."}
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={editing ? "Save server" : "Add server"}
            onCancel={() => (dirty ? setConfirmDiscard(true) : close())}
            saveEnabled={editing ? undefined : true}
            disabled={busy !== null}
          />
        }
      >
        <SecretUnreadableNotice item={editing} />
        {editing ? <ConnectionUpdatePanel kind="requests" id={editing.id} /> : null}
        <DrawerSection title="Basics">
          <Field label="Address" error={errors.baseUrl} help="Including the port, for example http://192.168.1.20:5055.">
            <Input value={form.baseUrl} onChange={(event) => setForm((current) => ({ ...current, baseUrl: event.target.value }))} placeholder="http://192.168.1.20:5055" autoComplete="off" className="font-mono" />
          </Field>
          <Field label="API key" error={errors.apiKey} help={editing?.hasApiKey ? "Stored encrypted. Leave empty to keep the current one." : "Settings → General → API Key in Seerr."}>
            <Input type="password" value={form.apiKey} onChange={(event) => setForm((current) => ({ ...current, apiKey: event.target.value }))} placeholder={editing?.hasApiKey ? "••••••••  (unchanged)" : ""} autoComplete="new-password" />
          </Field>
          <SwitchRow label="Enabled" description="Retention rules may read its requests and clear the titles they remove." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>

        <DrawerSection title="Turning approvals into downloads">
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            In Seerr, add a Webhook notification agent pointed at the URL below, and paste the secret into its Authorization Header field. An approved request is placed by Deluno's own destination rules (genre, anime, original language, "requested in 4K", …) on the Destination Rules page, evaluated against the title. The library below is only the fallback — used when no rule matches.
          </p>
          {editing ? (
            <div className="flex flex-col gap-2">
              <Field label="Webhook URL">
                <div className="flex items-center gap-2">
                  <Input readOnly value={`${window.location.origin}/api/seerr-servers/${editing.id}/webhook`} className="font-mono" onFocus={(event) => event.target.select()} />
                  <Button type="button" variant="outline" size="sm" onClick={() => void copyToClipboard(`${window.location.origin}/api/seerr-servers/${editing.id}/webhook`, "url")}>
                    <Copy className="h-3.5 w-3.5" />
                    {copied === "url" ? "Copied" : "Copy"}
                  </Button>
                </div>
              </Field>
              <Field label="Authorization header value" help={webhookSecret ? "Shown once. Paste it into Seerr's Authorization Header field now." : editing.hasWebhookSecret ? "Already generated. Regenerating replaces it, so the webhook needs re-pasting into Seerr." : "Generate a secret so Deluno can tell this is really Seerr calling."}>
                <div className="flex items-center gap-2">
                  <Input readOnly type={webhookSecret ? "text" : "password"} value={webhookSecret ?? (editing.hasWebhookSecret ? "••••••••••••••••" : "")} placeholder={editing.hasWebhookSecret ? undefined : "Not generated yet"} className="font-mono" onFocus={(event) => event.target.select()} />
                  {webhookSecret ? (
                    <Button type="button" variant="outline" size="sm" onClick={() => void copyToClipboard(webhookSecret, "secret")}>
                      <Copy className="h-3.5 w-3.5" />
                      {copied === "secret" ? "Copied" : "Copy"}
                    </Button>
                  ) : null}
                  <Button type="button" variant="outline" size="sm" onClick={() => void regenerateWebhookSecret()} disabled={busy !== null}>
                    {busy === "webhook-secret" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                    {editing.hasWebhookSecret ? "Regenerate" : "Generate"}
                  </Button>
                </div>
              </Field>
            </div>
          ) : (
            <p className="text-(length:--type-body-sm) text-muted-foreground">…save this server first…</p>
          )}
          <FieldRow>
            <Field label="Fallback movie library" optional help="Used only when no destination rule matches. Blank uses the first movie library.">
              <Select
                value={form.movieLibraryId}
                onChange={(event) => setForm((current) => ({ ...current, movieLibraryId: event.target.value }))}
                placeholder="First movie library"
                options={libraries.filter((library) => library.mediaType === "movies").map((library) => ({ value: library.id, label: library.name }))}
              />
            </Field>
            <Field label="Fallback movie quality profile" optional help="Blank keeps the library's own default.">
              <Select
                value={form.movieQualityProfileId}
                onChange={(event) => setForm((current) => ({ ...current, movieQualityProfileId: event.target.value }))}
                placeholder="Library default"
                options={qualityProfiles.map((profile) => ({ value: profile.id, label: profile.name }))}
              />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Fallback TV library" optional help="Used only when no destination rule matches. Blank uses the first TV library.">
              <Select
                value={form.seriesLibraryId}
                onChange={(event) => setForm((current) => ({ ...current, seriesLibraryId: event.target.value }))}
                placeholder="First TV library"
                options={libraries.filter((library) => library.mediaType === "tv").map((library) => ({ value: library.id, label: library.name }))}
              />
            </Field>
            <Field label="Fallback TV quality profile" optional help="Blank keeps the library's own default.">
              <Select
                value={form.seriesQualityProfileId}
                onChange={(event) => setForm((current) => ({ ...current, seriesQualityProfileId: event.target.value }))}
                placeholder="Library default"
                options={qualityProfiles.map((profile) => ({ value: profile.id, label: profile.name }))}
              />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Tags for every title it adds" optional help="Leave blank to give none. Every title this server adds gets these tags before anything else happens, so a destination rule, a download client or a notification can act on them, for example “requested by Seerr”.">
              <TagPicker label="Seerr tags for every title" value={form.tags} onChange={(tags) => setForm((current) => ({ ...current, tags }))} warnUnused={false} />
            </Field>
            <Field label="Extra tags for a 4K request" optional help="Given on top of the tags on the left, only when the request was for 4K.">
              <TagPicker label="Extra Seerr tags for a 4K request" value={form.tags4k} onChange={(tags4k) => setForm((current) => ({ ...current, tags4k }))} warnUnused={false} />
            </Field>
          </FieldRow>
        </DrawerSection>

        {editing ? <RecentAcquisitionsSection connectionId={editing.id} /> : null}

        <DrawerSection title="Health">
          {editing?.lastHealthTestUtc ? (
            <dl className="grid grid-cols-[120px_1fr] items-center gap-x-(--grid-gap) gap-y-2 text-(length:--type-body-sm)">
              <dt className="text-muted-foreground">Last test</dt>
              <dd className="flex items-center gap-2">
                <Chip tone={healthChip(editing).tone}>{healthChip(editing).label}</Chip>
                <span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span>
              </dd>
              {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
            </dl>
          ) : null}
          {testResult && !editing?.lastHealthTestUtc ? (
            <p className={`text-(length:--type-body-sm) ${testResult.ok ? "text-success" : "text-destructive"}`} role="status">{testResult.message}</p>
          ) : null}
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void test()} disabled={busy !== null}>
            {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
            Test connection
          </Button>
        </DrawerSection>

        {editing && owed.length ? (
          <DrawerSection title="Still to clear" aside={`${owed.length} ${owed.length === 1 ? "title" : "titles"}`}>
            <dl className="grid grid-cols-[160px_1fr] gap-x-(--grid-gap) gap-y-2 text-(length:--type-body-sm)">
              {owed.map((cleanup) => (
                <div key={cleanup.entityId} className="contents">
                  <dt className="text-foreground">{cleanup.title}</dt>
                  <dd className="text-destructive">
                    {cleanup.problem ?? "Waiting"}
                    <span className="block text-(length:--type-caption) text-muted-foreground">Removed by a retention rule. Tried {cleanup.attempts} {cleanup.attempts === 1 ? "time" : "times"}, last {relative(cleanup.attemptedUtc)}; tried again at the next check.</span>
                  </dd>
                </div>
              ))}
            </dl>
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger
              title="Remove this server"
              description="Nothing in Seerr changes. Rules stop reading its requests."
              action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy !== null}>Remove…</Button>}
            />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Remove “${editing?.name ?? "server"}”?`}
        description="Nothing in Seerr itself changes. Retention rules that use request fields stop matching until another Seerr server is added, and removed titles are no longer cleared from it."
        confirmLabel="Remove server"
        confirmVariant="destructive"
        busy={busy === "remove"}
        onConfirm={() => void remove()}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(next) => {
          if (!next) setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          setInitial(form);
          close();
        }}
      />
    </>
  );
}

const ACQUISITION_LABELS: Record<string, { label: string; tone: "ok" | "bad" | "idle" }> = {
  added: { label: "Added, waiting to import", tone: "idle" },
  failed: { label: "Could not add", tone: "bad" },
  available: { label: "Imported and reported available", tone: "ok" },
  "available-failed": { label: "Imported, but Seerr could not be told", tone: "bad" }
};

/**
 * #851/#852: the last few approvals this server turned into acquisitions (or
 * tried to and failed), so the automatic action is visible, not silent.
 */
function RecentAcquisitionsSection({ connectionId }: { connectionId: string }) {
  const [items, setItems] = useState<SeerrAcquisition[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    setItems(null);
    fetchJson<SeerrAcquisition[]>(`/api/seerr-servers/${connectionId}/acquisitions`)
      .then((result) => {
        if (!cancelled) setItems(result);
      })
      .catch(() => {
        if (!cancelled) setItems([]);
      });
    return () => {
      cancelled = true;
    };
  }, [connectionId]);

  if (items !== null && items.length === 0) {
    return null;
  }

  return (
    <DrawerSection title="Recent acquisitions" aside={items ? `${items.length}` : undefined}>
      {items === null ? (
        <p className="text-(length:--type-body-sm) text-muted-foreground">Loading…</p>
      ) : (
        <dl className="grid grid-cols-[1fr_auto] items-baseline gap-x-(--grid-gap) gap-y-2 text-(length:--type-body-sm)">
          {items.map((item) => {
            const status = ACQUISITION_LABELS[item.status] ?? { label: item.status, tone: "idle" as const };
            return (
              <div key={item.id} className="contents">
                <dt className="text-foreground">
                  {item.title}
                  <span className="block text-(length:--type-caption) text-muted-foreground">
                    {relative(item.createdUtc)}
                    {item.requestedBy ? ` · requested by ${item.requestedBy}` : ""}
                    {item.ruleName ? ` · placed by the “${item.ruleName}” rule` : item.status === "added" || item.status === "available" ? " · placed by the fallback library" : ""}
                    {item.message ? ` · ${item.message}` : ""}
                  </span>
                </dt>
                <dd>
                  <Chip tone={status.tone}>{status.label}</Chip>
                </dd>
              </div>
            );
          })}
        </dl>
      )}
    </DrawerSection>
  );
}
