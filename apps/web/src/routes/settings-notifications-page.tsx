/**
 * Notifications — list → drawer.
 *
 *   PageToolbar (System settings tabs · New webhook)
 *   ListCard  (name · url · events · last fired · status · on · ›)
 *   Drawer    (Basics · Delivery · Delete)
 *
 * Contracts: GET/POST /api/notification-webhooks,
 * PUT/DELETE /api/notification-webhooks/{id}, POST …/{id}/test,
 * GET /api/notification-webhooks/deliveries, POST …/replay, PATCH /api/settings.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Loader2, Plus, RotateCcw, Send } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { fetchJson, type NotificationWebhookDeliveryItem, type NotificationWebhookItem, type PlatformSettingsSnapshot } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";

const EVENT_OPTIONS = [
  { value: "all", label: "All events" },
  { value: "grab", label: "Grab — a download was sent to a client" },
  { value: "import", label: "Import — a file was added to the library" },
  { value: "upgrade", label: "Upgrade — a better release replaced a file" },
  { value: "health", label: "Health alerts" },
  { value: "test", label: "Test events only" }
];

interface LoaderData {
  settings: PlatformSettingsSnapshot;
  webhooks: NotificationWebhookItem[];
  deliveries: NotificationWebhookDeliveryItem[];
}

interface WebhookForm {
  name: string;
  url: string;
  eventFilters: string;
  isEnabled: boolean;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsNotificationsLoader(): Promise<LoaderData> {
  const [settings, webhooks, deliveries] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<NotificationWebhookItem[]>("/api/notification-webhooks"),
    fetchJson<NotificationWebhookDeliveryItem[]>("/api/notification-webhooks/deliveries?take=100")
  ]);
  return { settings, webhooks, deliveries };
}

export function SettingsNotificationsPage() {
  const { settings, webhooks, deliveries } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const sorted = useMemo(() => [...webhooks].sort((a, b) => a.name.localeCompare(b.name)), [webhooks]);

  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<WebhookForm>(emptyForm);
  const [initialForm, setInitialForm] = useState<WebhookForm>(emptyForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [urlError, setUrlError] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? webhooks.find((webhook) => webhook.id === mode.id) ?? null : null;
  const dirty = isOpen && (form.name !== initialForm.name || form.url !== initialForm.url || form.eventFilters !== initialForm.eventFilters || form.isEnabled !== initialForm.isEnabled);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  function open(webhook: NotificationWebhookItem | null) {
    const next = webhook ? { name: webhook.name, url: webhook.url, eventFilters: webhook.eventFilters || "all", isEnabled: webhook.isEnabled } : emptyForm();
    setMode(webhook ? { kind: "edit", id: webhook.id } : { kind: "create" });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
    setUrlError(null);
  }
  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    if (!form.url.trim()) {
      setUrlError("Paste the URL Deluno should POST to.");
      return;
    }
    setBusy("save");
    setSaveState("saving");
    try {
      const payload = { name: form.name.trim() || "Webhook", url: form.url.trim(), eventFilters: form.eventFilters, isEnabled: form.isEnabled };
      const response = await authedFetch(mode.kind === "edit" ? `/api/notification-webhooks/${mode.id}` : "/api/notification-webhooks", { method: mode.kind === "edit" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      if (!response.ok) throw new Error(mode.kind === "edit" ? "Webhook could not be saved." : "Webhook could not be added.");
      if (mode.kind === "create") {
        const created = (await response.json()) as NotificationWebhookItem;
        setMode({ kind: "edit", id: created.id });
        setSaveMessage("Webhook added");
      } else {
        setSaveMessage("Saved just now");
      }
      setForm(payload);
      setInitialForm(payload);
      setSaveState("saved");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
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
      return true;
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Action failed.");
      return false;
    } finally {
      setBusy(null);
    }
  }

  async function handleRemove() {
    if (mode.kind !== "edit") return;
    const id = mode.id;
    const ok = await run("remove", async () => {
      const response = await authedFetch(`/api/notification-webhooks/${id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Webhook could not be removed.");
    }, `${editing?.name ?? "Webhook"} removed`);
    if (!ok) return;
    setConfirmRemove(false);
    setInitialForm(form);
    closeDrawer();
  }

  async function toggleWebhook(webhook: NotificationWebhookItem, isEnabled: boolean) {
    await run(`toggle:${webhook.id}`, async () => {
      const response = await authedFetch(`/api/notification-webhooks/${webhook.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: webhook.name, url: webhook.url, eventFilters: webhook.eventFilters, isEnabled }) });
      if (!response.ok) throw new Error(`Could not ${isEnabled ? "enable" : "pause"} ${webhook.name}.`);
    });
    if (mode.kind === "edit" && mode.id === webhook.id && !dirty) {
      const next = { ...form, isEnabled };
      setForm(next);
      setInitialForm(next);
    }
  }

  async function sendTest() {
    if (mode.kind !== "edit") return;
    const id = mode.id;
    await run("test", async () => {
      const response = await authedFetch(`/api/notification-webhooks/${id}/test`, { method: "POST" });
      if (!response.ok) throw new Error("Test notification could not be sent.");
    }, `Test event sent to ${editing?.name ?? "the webhook"}`);
  }

  async function replayDelivery(delivery: NotificationWebhookDeliveryItem) {
    await run(`replay:${delivery.id}`, async () => {
      const response = await authedFetch(`/api/notification-webhooks/deliveries/${delivery.id}/replay`, { method: "POST" });
      const result = response.ok ? (await response.json()) as { sent?: boolean; error?: string } : null;
      if (!response.ok || result?.sent !== true) {
        throw new Error(result?.error ?? "Delivery could not be replayed.");
      }
    }, "Delivery replayed");
  }

  async function toggleGlobal(enabled: boolean) {
    await run("global", async () => {
      await settingsMutation.mutate({ enableNotifications: enabled });
    });
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={systemSettingsNavItems}
        actions={
          <PageToolbarAction onClick={() => open(null)}>New webhook</PageToolbarAction>
        }
      />

      <ListCard title="Webhooks" count={webhooks.length ? `${webhooks.length} ${webhooks.length === 1 ? "webhook" : "webhooks"} · ${webhooks.filter((webhook) => webhook.isEnabled).length} enabled · send Deluno events to other tools` : undefined}>
        {webhooks.length === 0 ? (
          <ListEmpty
            title="No webhooks yet"
            description="Deluno can POST a JSON payload to any URL when it grabs, imports or upgrades a title, or when a health check fails."
            actions={
              <Button type="button" size="sm" onClick={() => open(null)}>
                <Plus className="h-3.5 w-3.5" />
                New webhook
              </Button>
            }
          />
        ) : (
          <ListTable columns={[{ label: "Name" }, { label: "Sends to", width: "minmax(0,1.4fr)" }, { label: "Events" }, { label: "Last fired" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]}>
            {sorted.map((webhook) => {
              const chip = statusChip(webhook, settings.enableNotifications);
              return (
                <ListRow key={webhook.id} onClick={() => open(webhook)} selected={mode.kind === "edit" && mode.id === webhook.id}>
                  <ListNameCell name={webhook.name} sub={webhook.lastError ? "Last delivery failed" : "Outbound webhook"} />
                  <ListCell mono primary={webhook.url} />
                  <ListCell primary={eventLabel(webhook.eventFilters)} secondary={webhook.eventFilters === "all" ? "Every event type" : "Filtered"} />
                  <ListCell numeric primary={webhook.lastFiredUtc ? relative(webhook.lastFiredUtc) : <span className="text-muted-foreground">Never</span>} secondary={webhook.lastError ?? undefined} />
                  <ListCell mobile>
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                  </ListCell>
                  <ListCell mobile>
                    <Switch size="sm" aria-label={`${webhook.isEnabled ? "Pause" : "Enable"} ${webhook.name}`} checked={webhook.isEnabled} disabled={busy === `toggle:${webhook.id}`} onCheckedChange={(checked) => void toggleWebhook(webhook, checked)} />
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Delivery">
        <ListTable columns={[{ label: "Setting" }, { label: "Applies to" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]} chevron={false}>
          <ListRow>
            <ListNameCell name="Send notifications" sub="Master switch. When off, no webhook fires — including tests." />
            <ListCell primary="All webhooks" secondary={`${webhooks.length} configured`} />
            <ListCell mobile>
              <Chip tone={settings.enableNotifications ? "ok" : "idle"}>{settings.enableNotifications ? "Sending" : "Paused"}</Chip>
            </ListCell>
            <ListCell mobile>
              <Switch size="sm" aria-label="Send notifications" checked={settings.enableNotifications} disabled={busy === "global"} onCheckedChange={(checked) => void toggleGlobal(checked)} />
            </ListCell>
          </ListRow>
        </ListTable>
      </ListCard>

      <ListCard title="Delivery history" count={deliveries.length ? `${deliveries.length} recent delivery attempts · failures remain replayable` : undefined}>
        {deliveries.length === 0 ? (
          <ListEmpty title="No delivery history yet" description="Deluno will keep a bounded record after the first matching event or test." />
        ) : (
          <ListTable columns={[{ label: "Event" }, { label: "Webhook" }, { label: "Attempts" }, { label: "Last result" }, { label: "Action", width: "minmax(8rem,auto)", mobile: true }]} chevron={false}>
            {deliveries.map((delivery) => (
              <ListRow key={delivery.id}>
                <ListNameCell name={delivery.title} sub={delivery.eventCategory} />
                <ListCell mono primary={webhooks.find((webhook) => webhook.id === delivery.webhookId)?.name ?? delivery.webhookId} />
                <ListCell numeric primary={`${delivery.attemptCount}/${delivery.maxAttempts}`} secondary={delivery.lastStatusCode ? `HTTP ${delivery.lastStatusCode}` : undefined} />
                <ListCell primary={<Chip tone={deliveryTone(delivery.status)}>{delivery.status}</Chip>} secondary={delivery.lastError ?? (delivery.lastAttemptUtc ? relative(delivery.lastAttemptUtc) : undefined)} />
                <ListCell mobile>
                  {delivery.status === "dead-letter" || delivery.status === "retrying" ? (
                    <Button type="button" variant="outline" size="sm" onClick={() => void replayDelivery(delivery)} disabled={busy !== null}>
                      {busy === `replay:${delivery.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RotateCcw className="h-3.5 w-3.5" />}
                      Replay
                    </Button>
                  ) : null}
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={isOpen}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={mode.kind === "create" ? "New webhook" : editing?.name ?? form.name}
        description={mode.kind === "create" ? "Deluno POSTs a JSON payload to this URL." : `Webhook · ${eventLabel(form.eventFilters)}`}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Add webhook" : "Save webhook"} onCancel={requestClose} saveEnabled={mode.kind === "create" ? true : undefined} disabled={busy !== null} />}
      >
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Name">
              <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} placeholder="Home Assistant" autoComplete="off" />
            </Field>
            <Field label="Events" help="Which events trigger a POST.">
              <Select value={form.eventFilters} onChange={(event) => setForm((current) => ({ ...current, eventFilters: event.target.value }))} options={EVENT_OPTIONS} />
            </Field>
          </FieldRow>
          <Field label="URL" error={urlError} help="Deluno sends a JSON body; no authentication headers are added.">
            <Input value={form.url} onChange={(event) => { setUrlError(null); setForm((current) => ({ ...current, url: event.target.value })); }} placeholder="https://example.com/hooks/deluno" className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
          </Field>
          <SwitchRow label="Enabled" description="Paused webhooks stay configured but never fire." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Delivery" aside={editing.lastFiredUtc ? `last fired ${relative(editing.lastFiredUtc)}` : "never fired"}>
            {editing.lastError ? <p className="text-[length:var(--type-caption)] text-destructive">{editing.lastError}</p> : null}
            {!settings.enableNotifications ? <p className="text-[length:var(--type-caption)] text-warning">Notifications are paused for the whole install, so this webhook will not fire.</p> : null}
            <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void sendTest()} disabled={busy !== null || dirty}>
              {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
              Send a test event
            </Button>
            {dirty ? <p className="text-[length:var(--type-caption)] text-muted-foreground">Save your changes first.</p> : null}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger title="Delete this webhook" description="Nothing else changes; Deluno just stops posting here." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy !== null}>Delete</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog open={confirmRemove} onOpenChange={setConfirmRemove} title={`Delete “${editing?.name ?? form.name}”?`} description="Deluno stops posting to this URL. This cannot be undone." confirmLabel="Delete webhook" busy={busy === "remove"} onConfirm={() => void handleRemove()} />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this webhook haven't been saved."
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

function emptyForm(): WebhookForm {
  return { name: "", url: "", eventFilters: "all", isEnabled: true };
}
function eventLabel(value: string) {
  return EVENT_OPTIONS.find((option) => option.value === value)?.label.split(" — ")[0] ?? value;
}
function statusChip(webhook: NotificationWebhookItem, globallyEnabled: boolean): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (!webhook.isEnabled) return { tone: "idle", label: "Off" };
  if (!globallyEnabled) return { tone: "warn", label: "Paused" };
  if (webhook.lastError) return { tone: "bad", label: "Failing" };
  return webhook.lastFiredUtc ? { tone: "ok", label: "Delivering" } : { tone: "idle", label: "Untested" };
}
function deliveryTone(status: string): NonNullable<ChipProps["tone"]> {
  if (status === "delivered") return "ok";
  if (status === "dead-letter") return "bad";
  if (status === "retrying") return "warn";
  return "idle";
}
function relative(iso: string) {
  const minutes = Math.round(Math.abs(Date.now() - new Date(iso).getTime()) / 60000);
  return minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`;
}
