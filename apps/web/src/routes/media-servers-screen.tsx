import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Loader2, Wifi } from "lucide-react";
import { fetchJson, readValidationProblem } from "../lib/api";
import type { IntegrationFailure } from "../lib/api/types";
import { authedFetch } from "../lib/use-auth";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { healthChip, relative } from "./connections/format";

const TABS = configurationNavAreas.find((area) => area.to === "/indexers/indexers")?.items ?? [];

/** Mirrors `Deluno.Connections.Contracts.MediaServerConnection`. */
export interface MediaServerConnection {
  id: string;
  kind: "plex" | "jellyfin" | "emby" | string;
  name: string;
  baseUrl: string;
  hasToken: boolean;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthLatencyMs: number | null;
  lastHealthTestUtc: string | null;
  serverName: string | null;
  serverId: string | null;
  lastHealthFailure?: IntegrationFailure | null;
}

interface MediaServerTestResult {
  ok: boolean;
  status: string;
  message: string;
  latencyMs: number | null;
  serverName: string | null;
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
}

export async function mediaServersLoader() {
  return { servers: await fetchJson<MediaServerConnection[]>("/api/media-servers") };
}

function kindOf(kind: string) {
  return KINDS.find((item) => item.value === kind) ?? KINDS[0];
}

function formFrom(server: MediaServerConnection | null): ServerForm {
  return server
    ? { kind: server.kind, name: server.name, baseUrl: server.baseUrl, token: "", isEnabled: server.isEnabled }
    : { kind: "jellyfin", name: "Jellyfin", baseUrl: "", token: "", isEnabled: true };
}

function same(left: ServerForm, right: ServerForm) {
  return left.kind === right.kind && left.name === right.name && left.baseUrl === right.baseUrl && left.token === right.token && left.isEnabled === right.isEnabled;
}

/**
 * Plex, Jellyfin and Emby (#493).
 *
 * Retention rules read what these servers say has been watched, and tell them
 * when a title has gone. Each row shows the name the server gave itself on its
 * last test, beside the address, so it is always clear which machine a row
 * reaches - two servers on the same default port are otherwise easy to mix up.
 */
export function MediaServersPage() {
  const { servers } = useLoaderData() as { servers: MediaServerConnection[] };
  const revalidator = useRevalidator();

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

  const editing = drawer.id ? servers.find((server) => server.id === drawer.id) ?? null : null;
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
  }

  function close() {
    setDrawer({ open: false, id: null });
    setConfirmDiscard(false);
  }

  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else close();
  }

  function chooseKind(value: string) {
    const next = kindOf(value);
    setForm((current) => ({
      ...current,
      kind: value,
      name: !current.name.trim() || KINDS.some((item) => item.label === current.name) ? next.label : current.name
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
      setForm(settled);
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
        body: JSON.stringify({ kind: server.kind, name: server.name, baseUrl: server.baseUrl, token: "", isEnabled })
      });
      revalidator.revalidate();
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
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={TABS}
        actions={
          <PageToolbarAction type="button" onClick={() => open(null)}>
            Add server
          </PageToolbarAction>
        }
      />

      <ListCard title="Media servers" count={servers.length ? `${working} of ${servers.length} working` : undefined}>
        {servers.length === 0 ? (
          <ListEmpty
            title="No media servers"
            description="Add Plex, Jellyfin or Emby so retention rules can see what has been watched and remove cleaned-up titles from the server too."
          />
        ) : (
          <ListTable
            columns={[
              { label: "Server" },
              { label: "Address" },
              { label: "Reports itself as" },
              { label: "Last test" },
              { label: "Status", width: LIST_TRACK.status, mobile: true },
              { label: "On", width: LIST_TRACK.toggle, mobile: true }
            ]}
          >
            {servers.map((server) => {
              const chip = healthChip(server);
              return (
                <ListRow key={server.id} onClick={() => open(server)} selected={drawer.open && drawer.id === server.id}>
                  <ListNameCell name={server.name} sub={kindOf(server.kind).label} />
                  <ListCell mono primary={server.baseUrl} />
                  <ListCell primary={server.serverName ?? <span className="text-muted-foreground">Not tested yet</span>} />
                  <ListCell primary={relative(server.lastHealthTestUtc)} secondary={server.lastHealthMessage ?? undefined} />
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
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Name" error={errors.name}>
              <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} autoComplete="off" />
            </Field>
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
            <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
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
            <p className={`text-[length:var(--type-body-sm)] ${testResult.ok ? "text-success" : "text-destructive"}`} role="status">
              {testResult.message}
            </p>
          ) : null}
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void test()} disabled={busy !== null}>
            {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
            Test connection
          </Button>
        </DrawerSection>

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
