import { useEffect, useState, type FormEvent } from "react";
import { Loader2, Wifi } from "lucide-react";
import { readValidationProblem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { resolveDrawerSaveState, shouldClearSaveState } from "../../lib/drawer-save-state";
import { useUnsavedChanges } from "../../hooks/use-unsaved-changes";
import { healthChip, relative } from "../../routes/connections/format";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../ui/drawer";
import { Field } from "../ui/field";
import { Input } from "../ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { Switch, SwitchRow } from "../ui/switch";

/** Mirrors `Deluno.Connections.Contracts.SeerrConnection`. */
export interface SeerrServer {
  id: string;
  name: string;
  baseUrl: string;
  hasApiKey: boolean;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthLatencyMs: number | null;
  lastHealthTestUtc: string | null;
  serverName: string | null;
  version: string | null;
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
}

function formFrom(server: SeerrServer | null): SeerrForm {
  return server
    ? { name: server.name, baseUrl: server.baseUrl, apiKey: "", isEnabled: server.isEnabled }
    : { name: "Seerr", baseUrl: "", apiKey: "", isEnabled: true };
}

function same(left: SeerrForm, right: SeerrForm) {
  return left.name === right.name && left.baseUrl === right.baseUrl && left.apiKey === right.apiKey && left.isEnabled === right.isEnabled;
}

/**
 * Jellyseerr and Overseerr (#522), beside the media servers they sit in front
 * of. Retention rules read who requested what; a title a rule removes is
 * cleared from Seerr, and a clearing Seerr refused is listed on its server
 * until it goes through.
 */
export function SeerrServersCard({ servers, cleanups, onChanged }: { servers: SeerrServer[]; cleanups: SeerrCleanup[]; onChanged: () => void }) {
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
  }

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
      setForm(settled);
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

  async function toggle(server: SeerrServer, isEnabled: boolean) {
    setBusy(`toggle:${server.id}`);
    try {
      await authedFetch(`/api/seerr-servers/${server.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: server.name, baseUrl: server.baseUrl, apiKey: "", isEnabled })
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
              const waiting = cleanups.filter((cleanup) => cleanup.serverId === server.id);
              return (
                <ListRow key={server.id} onClick={() => open(server)} selected={drawer.open && drawer.id === server.id}>
                  <ListNameCell name={server.name} sub={server.version ? `Seerr ${server.version}` : "Seerr"} />
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
        <DrawerSection title="Basics">
          <Field label="Name" error={errors.name}>
            <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} autoComplete="off" />
          </Field>
          <Field label="Address" error={errors.baseUrl} help="Including the port, for example http://192.168.1.20:5055.">
            <Input value={form.baseUrl} onChange={(event) => setForm((current) => ({ ...current, baseUrl: event.target.value }))} placeholder="http://192.168.1.20:5055" autoComplete="off" className="font-mono" />
          </Field>
          <Field label="API key" error={errors.apiKey} help={editing?.hasApiKey ? "Stored encrypted. Leave empty to keep the current one." : "Settings → General → API Key in Seerr."}>
            <Input type="password" value={form.apiKey} onChange={(event) => setForm((current) => ({ ...current, apiKey: event.target.value }))} placeholder={editing?.hasApiKey ? "••••••••  (unchanged)" : ""} autoComplete="new-password" />
          </Field>
          <SwitchRow label="Enabled" description="Retention rules may read its requests and clear the titles they remove." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>

        <DrawerSection title="Health">
          {editing?.lastHealthTestUtc ? (
            <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
              <dt className="text-muted-foreground">Last test</dt>
              <dd className="flex items-center gap-2">
                <Chip tone={healthChip(editing).tone}>{healthChip(editing).label}</Chip>
                <span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span>
              </dd>
              {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
            </dl>
          ) : null}
          {testResult && !editing?.lastHealthTestUtc ? (
            <p className={`text-[length:var(--type-body-sm)] ${testResult.ok ? "text-success" : "text-destructive"}`} role="status">{testResult.message}</p>
          ) : null}
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void test()} disabled={busy !== null}>
            {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
            Test connection
          </Button>
        </DrawerSection>

        {editing && owed.length ? (
          <DrawerSection title="Still to clear" aside={`${owed.length} ${owed.length === 1 ? "title" : "titles"}`}>
            <dl className="grid grid-cols-[160px_1fr] gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
              {owed.map((cleanup) => (
                <div key={cleanup.entityId} className="contents">
                  <dt className="text-foreground">{cleanup.title}</dt>
                  <dd className="text-destructive">
                    {cleanup.problem ?? "Waiting"}
                    <span className="block text-[length:var(--type-caption)] text-muted-foreground">Removed by a retention rule. Tried {cleanup.attempts} {cleanup.attempts === 1 ? "time" : "times"}, last {relative(cleanup.attemptedUtc)}; tried again at the next check.</span>
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
