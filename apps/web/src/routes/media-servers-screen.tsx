import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { toneSortValue } from "../lib/status-tones";
import { SecretUnreadableNotice } from "../components/app/secret-unreadable-notice";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { Loader2, Plus, Trash2, Wifi } from "lucide-react";
import { fetchJson, readValidationProblem } from "../lib/api";
import type { IntegrationFailure, LibraryItem, QualityProfileItem } from "../lib/api/types";
import { authedFetch } from "../lib/use-auth";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { healthChip, relative } from "./connections/format";
import { ConnectionVersionBeside } from "../components/app/connection-version";
import { ConnectionUpdatePanel } from "../components/app/connection-update-panel";
import { useConnectionVersions, versionKey } from "../lib/connection-versions";
import { SeerrServersCard, type SeerrCleanup, type SeerrServer } from "../components/app/seerr-servers-card";

const TABS = configurationNavAreas.find((area) => area.to === "/indexers/indexers")?.items ?? [];

/** Mirrors `Deluno.Connections.Contracts.MediaServerConnection`. */
export interface MediaServerConnection {
  id: string;
  kind: "plex" | "jellyfin" | "emby" | string;
  name: string;
  baseUrl: string;
  hasToken: boolean;
  /** Saved on another computer and cannot be read here (#1094): listed as missing until entered again. */
  secretUnreadable?: boolean;
  secretProblem?: string | null;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthLatencyMs: number | null;
  lastHealthTestUtc: string | null;
  serverName: string | null;
  serverId: string | null;
  lastHealthFailure?: IntegrationFailure | null;
  leavingSoonOnHome: boolean;
}

/** Mirrors `Deluno.Retention.RetentionCollectionState` (#521). */
interface LeavingSoonCollection {
  serverId: string;
  mediaType: "movies" | "tv" | string;
  members: number;
  skipped: number;
  problem: string | null;
  attemptedUtc: string;
  syncedUtc: string | null;
}

interface MediaServerTestResult {
  ok: boolean;
  status: string;
  message: string;
  latencyMs: number | null;
  serverName: string | null;
}

/** Mirrors `Deluno.Connections.Contracts.MediaServerPathMappingItem` (#662). */
interface MediaServerPathMappingItem {
  id: string;
  mediaServerId: string;
  delunoPath: string;
  serverPath: string;
  isEnabled: boolean;
  priority: number;
}

interface MediaServerPathMappingTestResult {
  found: boolean;
  message: string;
}

const KINDS = [
  { value: "jellyfin", label: "Jellyfin", port: "8096", tokenLabel: "API key", tokenHelp: "Dashboard → API Keys. Use a key from an administrator, so Deluno can see what every user has watched." },
  { value: "emby", label: "Emby", port: "8096", tokenLabel: "API key", tokenHelp: "Settings → Advanced → API Keys. Use a key from an administrator, so Deluno can see what every user has watched." },
  { value: "plex", label: "Plex", port: "32400", tokenLabel: "X-Plex-Token", tokenHelp: "The token of the account whose watch history rules should read. Plex reports that account's history only." }
] as const;

interface ServerForm {
  kind: string;
  name: string;
  baseUrl: string;
  token: string;
  isEnabled: boolean;
  leavingSoonOnHome: boolean;
}

export async function mediaServersLoader() {
  const [servers, collections, seerrServers, seerrCleanups, libraries, qualityProfiles] = await Promise.all([
    fetchJson<MediaServerConnection[]>("/api/media-servers"),
    // Only a person who can see retention sees its collections; the servers stand on their own.
    fetchJson<LeavingSoonCollection[]>("/api/retention/collections").catch(() => [] as LeavingSoonCollection[]),
    fetchJson<SeerrServer[]>("/api/seerr-servers").catch(() => [] as SeerrServer[]),
    fetchJson<SeerrCleanup[]>("/api/retention/seerr-cleanups").catch(() => [] as SeerrCleanup[]),
    fetchJson<LibraryItem[]>("/api/libraries").catch(() => [] as LibraryItem[]),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles").catch(() => [] as QualityProfileItem[])
  ]);
  return { servers, collections, seerrServers, seerrCleanups, libraries, qualityProfiles };
}

function kindOf(kind: string) {
  return KINDS.find((item) => item.value === kind) ?? KINDS[0];
}

function formFrom(server: MediaServerConnection | null): ServerForm {
  return server
    ? { kind: server.kind, name: server.name, baseUrl: server.baseUrl, token: "", isEnabled: server.isEnabled, leavingSoonOnHome: server.leavingSoonOnHome ?? false }
    : { kind: "jellyfin", name: "Jellyfin", baseUrl: "", token: "", isEnabled: true, leavingSoonOnHome: false };
}

function same(left: ServerForm, right: ServerForm) {
  return left.kind === right.kind && left.name === right.name && left.baseUrl === right.baseUrl && left.token === right.token && left.isEnabled === right.isEnabled && left.leavingSoonOnHome === right.leavingSoonOnHome;
}

/**
 * Plex, Jellyfin and Emby (#493).
 *
 * Retention rules read what these servers say has been watched, and tell them
 * when a title has gone. Each row shows the name the server gave itself on its
 * last test, beside the address, so it is always clear which machine a row
 * reaches - two servers on the same default port are otherwise easy to mix up.
 */
/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["LibraryChanged", "QualityProfileChanged"], areas: [ConfigAreas.mediaServers] };

export function MediaServersPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { servers, collections, seerrServers, seerrCleanups, libraries, qualityProfiles } = useLoaderData() as {
    servers: MediaServerConnection[];
    collections: LeavingSoonCollection[];
    seerrServers: SeerrServer[];
    seerrCleanups: SeerrCleanup[];
    libraries: LibraryItem[];
    qualityProfiles: QualityProfileItem[];
  };
  const { versions } = useConnectionVersions();
  const revalidator = useRevalidator();
  const location = useLocation();
  const navigate = useNavigate();
  const connectionParam = new URLSearchParams(location.search).get("connection");

  const [drawer, setDrawer] = useState<{ open: boolean; id: string | null }>({ open: false, id: null });
  const [form, setForm] = useState<ServerForm>(formFrom(null));
  const [initial, setInitial] = useState<ServerForm>(formFrom(null));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<MediaServerTestResult | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [mappings, setMappings] = useState<MediaServerPathMappingItem[]>([]);
  const [newMapping, setNewMapping] = useState({ delunoPath: "", serverPath: "" });
  const [mappingOpen, setMappingOpen] = useState(false);
  const [mappingResults, setMappingResults] = useState<Record<string, MediaServerPathMappingTestResult>>({});

  const editing = drawer.id ? servers.find((server) => server.id === drawer.id) ?? null : null;
  const editingCollections = editing ? collections.filter((collection) => collection.serverId === editing.id) : [];
  const kind = kindOf(form.kind);
  const dirty = drawer.open && !same(form, initial);
  const footerState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const working = useMemo(() => servers.filter((server) => server.isEnabled && server.healthStatus === "healthy").length, [servers]);

  function open(server: MediaServerConnection | null) {
    const next = formFrom(server);
    setDrawer({ open: true, id: server?.id ?? null });
    setForm(next);
    setInitial(next);
    setErrors({});
    setSaveState(undefined);
    setSaveMessage(null);
    setTestResult(null);
    setMappings([]);
    setNewMapping({ delunoPath: "", serverPath: "" });
    setMappingOpen(false);
    setMappingResults({});
    if (server) {
      fetchJson<MediaServerPathMappingItem[]>(`/api/media-servers/${server.id}/path-mappings`)
        .then(setMappings)
        .catch(() => setMappings([]));
    }
  }

  // A notice about a newer version opens this connection (#1118).
  useEffect(() => {
    if (drawer.open) return;
    const id = new URLSearchParams(location.search).get("connection");
    if (!id) return;
    const found = servers.find((item) => item.id === id);
    if (!found) return;
    open(found);
    navigate("/indexers/media-servers", { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [drawer.open, servers, location.search, navigate]);
  function close() {
    setDrawer({ open: false, id: null });
    setConfirmDiscard(false);
  }

  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else close();
  }

  function chooseKind(value: string) {
    setForm((current) => ({
      ...current,
      kind: value
    }));
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaveState("saving");
    setSaveMessage(null);
    setErrors({});
    try {
      const response = await authedFetch(editing ? `/api/media-servers/${editing.id}` : "/api/media-servers", {
        method: editing ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(form)
      });
      if (!response.ok) {
        const problem = await readValidationProblem(response.clone()).catch(() => null);
        if (problem?.errors) {
          setErrors(Object.fromEntries(Object.entries(problem.errors).map(([key, value]) => [key, value[0] ?? ""])));
        }
        throw new Error(problem?.errors ? "Check the highlighted fields." : "The server could not be saved.");
      }
      const saved = (await response.json()) as MediaServerConnection;
      const settled = formFrom(saved);
      setForm((current) => settleFormAfterSave(current, form, settled));
      setInitial(settled);
      setDrawer({ open: true, id: saved.id });
      setSaveState("saved");
      setSaveMessage(editing ? "Saved just now" : "Server added");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  async function test() {
    setBusy("test");
    setTestResult(null);
    try {
      const response = await authedFetch(editing ? `/api/media-servers/${editing.id}/test` : "/api/media-servers/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(form)
      });
      if (!response.ok) {
        const problem = await readValidationProblem(response.clone()).catch(() => null);
        if (problem?.errors) {
          setErrors(Object.fromEntries(Object.entries(problem.errors).map(([key, value]) => [key, value[0] ?? ""])));
        }
        setTestResult({ ok: false, status: "failed", message: problem?.errors ? "Fill in the highlighted fields first." : `The test could not run (HTTP ${response.status}).`, latencyMs: null, serverName: null });
        return;
      }
      setTestResult((await response.json()) as MediaServerTestResult);
      if (editing) revalidator.revalidate();
    } catch {
      setTestResult({ ok: false, status: "failed", message: "The test could not run. Check that Deluno is still reachable.", latencyMs: null, serverName: null });
    } finally {
      setBusy(null);
    }
  }

  async function toggle(server: MediaServerConnection, isEnabled: boolean) {
    setBusy(`toggle:${server.id}`);
    try {
      await authedFetch(`/api/media-servers/${server.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ kind: server.kind, name: server.name, baseUrl: server.baseUrl, token: "", isEnabled, leavingSoonOnHome: server.leavingSoonOnHome ?? false })
      });
      revalidator.revalidate();
    } finally {
      setBusy(null);
    }
  }

  async function addMapping() {
    if (!editing || !newMapping.delunoPath.trim() || !newMapping.serverPath.trim()) return;
    setBusy("mapping:add");
    try {
      const response = await authedFetch(`/api/media-servers/${editing.id}/path-mappings`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ delunoPath: newMapping.delunoPath, serverPath: newMapping.serverPath, isEnabled: true })
      });
      if (!response.ok) throw new Error();
      const created = (await response.json()) as MediaServerPathMappingItem;
      setMappings((current) => [...current, created]);
      setNewMapping({ delunoPath: "", serverPath: "" });
    } catch {
      setSaveMessage("The path mapping could not be saved.");
    } finally {
      setBusy(null);
    }
  }

  async function removeMapping(mappingId: string) {
    if (!editing) return;
    setBusy(`mapping:remove:${mappingId}`);
    try {
      const response = await authedFetch(`/api/media-servers/${editing.id}/path-mappings/${mappingId}`, { method: "DELETE" });
      if (!response.ok) throw new Error();
      setMappings((current) => current.filter((mapping) => mapping.id !== mappingId));
      setMappingResults((current) => {
        const next = { ...current };
        delete next[mappingId];
        return next;
      });
    } catch {
      setSaveMessage("The path mapping could not be removed.");
    } finally {
      setBusy(null);
    }
  }

  async function testMapping(mappingId: string) {
    if (!editing) return;
    setBusy(`mapping:test:${mappingId}`);
    try {
      const response = await authedFetch(`/api/media-servers/${editing.id}/path-mappings/${mappingId}/test`, { method: "POST" });
      const result = response.ok
        ? ((await response.json()) as MediaServerPathMappingTestResult)
        : { found: false, message: `The test could not run (HTTP ${response.status}).` };
      setMappingResults((current) => ({ ...current, [mappingId]: result }));
    } catch {
      setMappingResults((current) => ({ ...current, [mappingId]: { found: false, message: "The test could not run." } }));
    } finally {
      setBusy(null);
    }
  }

  async function remove() {
    if (!editing) return;
    setBusy("remove");
    try {
      const response = await authedFetch(`/api/media-servers/${editing.id}`, { method: "DELETE" });
      if (!response.ok) throw new Error();
      setConfirmRemove(false);
      setInitial(form);
      close();
      revalidator.revalidate();
    } catch {
      setSaveState("error");
      setSaveMessage("The server could not be removed.");
      setConfirmRemove(false);
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        tabs={TABS}
        actions={
          <PageToolbarAction type="button" onClick={() => open(null)}>
            Add server
          </PageToolbarAction>
        }
      />

      <ListCard title={servers.length ? `${working} of ${servers.length} working` : "0 servers"} titleCase={false}>
        {servers.length === 0 ? (
          <ListEmpty
            title="No media servers"
            description="Add Plex, Jellyfin or Emby so retention rules can see what has been watched and remove cleaned-up titles from the server too."
          />
        ) : (
          <ListTable
            listId="media-servers"
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
              const collectionProblem = collections.find((collection) => collection.serverId === server.id && collection.problem)?.problem;
              return (
                <ListRow key={server.id} onClick={() => open(server)} selected={drawer.open && drawer.id === server.id}>
                  <ListNameCell name={<>{server.name}<ConnectionVersionBeside info={versions.get(versionKey("mediaServer", server.id))} /></>} sub={kindOf(server.kind).label} />
                  <ListCell mono primary={server.baseUrl} />
                  <ListCell primary={server.serverName ?? <span className="text-muted-foreground">Not tested yet</span>} />
                  <ListCell primary={relative(server.lastHealthTestUtc)} secondary={collectionProblem ? `Leaving soon collection: ${collectionProblem}` : server.lastHealthMessage ?? undefined} />
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

      <SeerrServersCard openId={connectionParam} onOpened={() => navigate("/indexers/media-servers", { replace: true })} servers={seerrServers} cleanups={seerrCleanups} libraries={libraries} qualityProfiles={qualityProfiles} onChanged={() => revalidator.revalidate()} />

      <Drawer
        open={drawer.open}
        onOpenChange={(next) => {
          if (!next) requestClose();
        }}
        title={editing?.name ?? (form.name || "New media server")}
        description={editing ? `${kindOf(editing.kind).label} · ${editing.baseUrl}` : "A Plex, Jellyfin or Emby server retention rules can read from."}
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={editing ? "Save server" : "Add server"}
            onCancel={requestClose}
            saveEnabled={editing ? undefined : true}
            disabled={busy !== null}
          />
        }
      >
        <SecretUnreadableNotice item={editing} />
        {editing ? <ConnectionUpdatePanel kind="mediaServer" id={editing.id} /> : null}
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Server" error={errors.kind}>
              <Select value={form.kind} disabled={Boolean(editing)} onChange={(event) => chooseKind(event.target.value)} options={KINDS.map((item) => ({ value: item.value, label: item.label }))} />
            </Field>
          </FieldRow>
          <Field label="Address" error={errors.baseUrl} help={`Including the port, for example http://192.168.1.20:${kind.port}.`}>
            <Input value={form.baseUrl} onChange={(event) => setForm((current) => ({ ...current, baseUrl: event.target.value }))} placeholder={`http://192.168.1.20:${kind.port}`} autoComplete="off" className="font-mono" />
          </Field>
          <Field label={kind.tokenLabel} error={errors.token} help={editing?.hasToken ? "Stored encrypted. Leave empty to keep the current one." : kind.tokenHelp}>
            <Input type="password" value={form.token} onChange={(event) => setForm((current) => ({ ...current, token: event.target.value }))} placeholder={editing?.hasToken ? "••••••••  (unchanged)" : ""} autoComplete="new-password" />
          </Field>
          <SwitchRow label="Enabled" description="Retention rules may read from and update this server." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>

        <DrawerSection title="Health">
          {editing?.lastHealthTestUtc ? (
            <dl className="grid grid-cols-[120px_1fr] items-center gap-x-(--grid-gap) gap-y-2 text-(length:--type-body-sm)">
              <dt className="text-muted-foreground">Last test</dt>
              <dd className="flex items-center gap-2">
                <Chip tone={healthChip(editing).tone}>{healthChip(editing).label}</Chip>
                <span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span>
              </dd>
              {editing.serverName ? (<><dt className="text-muted-foreground">Reports itself as</dt><dd className="text-foreground">{editing.serverName}</dd></>) : null}
              {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
            </dl>
          ) : null}
          {/* A saved server's test lands in the facts above once it reloads;
              only a draft, which has nowhere else to say it, shows it here. */}
          {testResult && !editing?.lastHealthTestUtc ? (
            <p className={`text-(length:--type-body-sm) ${testResult.ok ? "text-success" : "text-destructive"}`} role="status">
              {testResult.message}
            </p>
          ) : null}
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void test()} disabled={busy !== null}>
            {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
            Test connection
          </Button>
        </DrawerSection>

        {editing ? (
          <Disclosure
            title="Advanced path mapping"
            summary={mappings.length ? `${mappings.length} saved path ${mappings.length === 1 ? "mapping" : "mappings"}` : "Only when Deluno and this server see different paths"}
            open={mappingOpen}
            onOpenChange={setMappingOpen}
          >
            <p className="text-(length:--type-caption) text-muted-foreground">
              Use this only when {editing.name} sees a folder under a different path than Deluno does — the usual case when they run in separate Docker containers or mount the same share differently. For example, Deluno reads <code className="font-mono">D:\Media\Movies</code> while {editing.name} sees the same files at <code className="font-mono">/data/Movies</code>.
            </p>
            {mappings.length ? (
              <div className="grid gap-2">
                {mappings.map((mapping) => {
                  const result = mappingResults[mapping.id];
                  return (
                    <div key={mapping.id} className="grid gap-1">
                      <div className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-(--field-pad-x) font-mono text-(length:--type-caption)">
                        <span className="min-w-0 flex-1 truncate text-muted-foreground" title={mapping.delunoPath}>{mapping.delunoPath}</span>
                        <span aria-hidden className="text-primary">→</span>
                        <span className="min-w-0 flex-1 truncate text-foreground" title={mapping.serverPath}>{mapping.serverPath}</span>
                        <Button type="button" variant="outline" size="sm" className="h-7 px-2 font-sans" onClick={() => void testMapping(mapping.id)} disabled={busy !== null}>
                          {busy === `mapping:test:${mapping.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                          Test
                        </Button>
                        <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove ${mapping.delunoPath} link`} onClick={() => void removeMapping(mapping.id)} disabled={busy !== null}>
                          <Trash2 className="h-3.5 w-3.5" />
                        </Button>
                      </div>
                      {result ? (
                        <p className={`font-sans text-(length:--type-caption) ${result.found ? "text-success" : "text-destructive"}`} role="status">
                          {result.message}
                        </p>
                      ) : null}
                    </div>
                  );
                })}
              </div>
            ) : null}
            <FieldRow>
              <Field label="Path as Deluno sees it">
                <Input value={newMapping.delunoPath} onChange={(event) => setNewMapping({ ...newMapping, delunoPath: event.target.value })} placeholder="D:\Media\Movies" className="font-mono text-(length:--type-caption)" />
              </Field>
              <Field label={`Same path as ${editing.name} sees it`}>
                <Input value={newMapping.serverPath} onChange={(event) => setNewMapping({ ...newMapping, serverPath: event.target.value })} placeholder="/data/Movies" className="font-mono text-(length:--type-caption)" />
              </Field>
            </FieldRow>
            <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void addMapping()} disabled={busy !== null || !newMapping.delunoPath.trim() || !newMapping.serverPath.trim()}>
              {busy === "mapping:add" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
              Link paths
            </Button>
          </Disclosure>
        ) : null}

        {editing ? (
          <DrawerSection title="Leaving soon" aside={editingCollections.length ? undefined : "nothing leaving"}>
            {editingCollections.length ? (
              <dl className="grid grid-cols-[120px_1fr] gap-x-(--grid-gap) gap-y-2 text-(length:--type-body-sm)">
                {editingCollections.map((collection) => (
                  <div key={collection.mediaType} className="contents">
                    <dt className="text-muted-foreground">{collection.mediaType === "tv" ? "TV collection" : "Movie collection"}</dt>
                    <dd className={collection.problem ? "text-destructive" : "text-foreground"}>
                      {collection.problem
                        ? `${collection.problem} Tried ${relative(collection.attemptedUtc)}; tried again at the next check.`
                        : `${collection.members === 0 ? "Removed, nothing is leaving" : `${collection.members} ${collection.members === 1 ? "title" : "titles"}`} · updated ${relative(collection.syncedUtc ?? collection.attemptedUtc)}`}
                      {collection.skipped > 0 ? <span className="block text-(length:--type-caption) text-muted-foreground">{collection.skipped} seasons or episodes cannot sit in a Plex show collection.</span> : null}
                    </dd>
                  </div>
                ))}
              </dl>
            ) : (
              <p className="text-(length:--type-body-sm) text-muted-foreground">When a retention rule reading this server marks a title, it appears in a Leaving soon collection here.</p>
            )}
            <SwitchRow
              label="Show on home"
              description="Puts the Leaving soon collections on this server's home screen."
              checked={form.leavingSoonOnHome}
              onCheckedChange={(checked) => setForm((current) => ({ ...current, leavingSoonOnHome: checked }))}
            />
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger
              title="Remove this server"
              description="Nothing on the server changes. Rules that use it stop reading from it."
              action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy !== null}>Remove…</Button>}
            />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Remove “${editing?.name ?? "server"}”?`}
        description="Nothing on the server itself changes. Retention rules that read from it stop doing so, and titles it held are judged without its watch history."
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
    </div>
  );
}
