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
import { toneSortValue } from "../lib/status-tones";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { TagPicker } from "../components/app/tag-picker";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { Loader2, Plus, RotateCcw, Send } from "lucide-react";
import { Button } from "../components/ui/button";
import { Checkbox, CheckboxRow } from "../components/ui/checkbox";
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
import { readErrorText, fetchJson, type NotificationPreferences, type NotificationProvider, type NotificationProviderConfig, type NotificationWebhookDeliveryItem, type NotificationWebhookItem, type PlatformSettingsSnapshot } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { ConnectionSelectAll, ConnectionSelectButton, ConnectionSelectionBar, ConnectionTagsDialog } from "../components/app/connection-tags-dialog";
import { useRowSelection } from "../lib/connection-tags";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

/** One tick per trigger, like Radarr and Sonarr's per-connection On... boxes (#1016). Stored as a comma-separated list in eventFilters; "all" means every event. */
const TRIGGER_OPTIONS = [
  { value: "grab", label: "Grab", description: "A download was sent to a client." },
  { value: "import", label: "Import", description: "A file was added to the library." },
  { value: "upgrade", label: "Upgrade", description: "A better release replaced a file." },
  { value: "health", label: "Health problems", description: "Something needs attention, and when it is fine again." },
  { value: "retention", label: "Leaving soon", description: "Leaving soon, removed, waiting for you or failed." },
  { value: "app.update", label: "Deluno update", description: "Deluno started running a newer version. Also needs the Application update switch in Triggers below." },
  { value: "test", label: "Test events only", description: "Only the Test button." }
];

function parseTriggers(eventFilters: string): string[] {
  return eventFilters.split(",").map((part) => part.trim()).filter(Boolean);
}

const EVENT_OPTIONS = [
  { value: "all", label: "All events" },
  { value: "grab", label: "Grab — a download was sent to a client" },
  { value: "import", label: "Import — a file was added to the library" },
  { value: "upgrade", label: "Upgrade — a better release replaced a file" },
  { value: "health", label: "Health alerts" },
  { value: "retention", label: "Retention — Leaving soon, removed, waiting for you, failed" },
  { value: "test", label: "Test events only" }
];

interface LoaderData {
  settings: PlatformSettingsSnapshot;
  webhooks: NotificationWebhookItem[];
  deliveries: NotificationWebhookDeliveryItem[];
  preferences: NotificationPreferences;
}

/**
 * Import outcomes (#965): both already fire and are gated in the backend
 * (ImportPipelineService). On by default, matching what every install has
 * always done.
 */
const IMPORT_PREFERENCE_ROWS: Array<{
  key: keyof NotificationPreferences;
  name: string;
  description: string;
}> = [
  { key: "importCompletedEnabled", name: "Import completed", description: "A file was added to the library." },
  { key: "importFailedEnabled", name: "Import failed", description: "An import could not finish." }
];

/**
 * Every lifecycle trigger beyond import-failed (#856). None of these had a
 * screen before this — including the four (Upgrade, Rename, File delete,
 * Manual interaction) already implemented and gated in the backend. Each
 * defaults off, matching today's behaviour, so nothing new fires unannounced.
 */
const LIFECYCLE_PREFERENCE_ROWS: Array<{
  key: keyof NotificationPreferences;
  name: string;
  description: string;
}> = [
  { key: "grabEnabled", name: "Grab", description: "A release was sent to a download client." },
  { key: "addedEnabled", name: "Title added", description: "A movie or show was added to the library." },
  { key: "upgradeEnabled", name: "Upgrade", description: "A better release replaced a file already held." },
  { key: "renameEnabled", name: "Rename", description: "A file was renamed to match the naming settings." },
  { key: "fileDeletedEnabled", name: "File delete", description: "A file was removed, including one replaced by an upgrade." },
  { key: "manualInteractionRequiredEnabled", name: "Manual interaction required", description: "An import failed in a way only a person can resolve." },
  { key: "updateEnabled", name: "Application update", description: "Deluno itself started running a newer version than it did last run." },
  { key: "providerHealthChangedEnabled", name: "Provider health changed", description: "An indexer, download client, media server, or processor became unhealthy or recovered." }
];

/**
 * Deluno's own recovery/cleanup actions (#858): each already lands in
 * Activity; these gate the same actions reaching the outbound path too.
 * Off by default, matching the conservative default for every other
 * automation-triggered toggle.
 */
const RECOVERY_CLEANUP_PREFERENCE_ROWS: Array<{
  key: keyof NotificationPreferences;
  name: string;
  description: string;
}> = [
  { key: "stalledDownloadHandledEnabled", name: "Stalled/slow download handled", description: "A download that stopped progressing had a health rule's action applied to it." },
  { key: "queueItemRemovedEnabled", name: "Queue item removed", description: "A download was removed from a client's queue, from Transfers." },
  { key: "orphanedDownloadRemovedEnabled", name: "Orphaned download removed", description: "A completed download folder nothing claimed was removed." },
  { key: "forcedImportEnabled", name: "Forced re-download", description: "Someone forced a re-download of a title from its page." },
  { key: "strikesPurgedEnabled", name: "Download-health strikes purged", description: "Every recorded download-health strike was cleared in one go." },
  { key: "cleanupClearedEnabled", name: "Cleanup cleared", description: "A refused download was removed from its client and its files deleted." },
  { key: "unmatchedImportGivenUpEnabled", name: "Unmatched import given up", description: "Deluno gave up waiting for an unmatched import to be resolved by hand." }
];

interface WebhookForm {
  name: string;
  url: string;
  eventFilters: string;
  isEnabled: boolean;
  provider: NotificationProvider;
  config: NotificationProviderConfig;
  /** Comma-separated tag names (#990). Blank fires this connection for any title; a tagged connection fires for a title only when it shares one of them. Tags never limit notifications that are not about one title. */
  tags: string;
  /** Radarr/Sonarr's Include Health Warnings (#1016). */
  includeHealthWarnings: boolean;
}

const PROVIDER_OPTIONS: Array<{ value: NotificationProvider; label: string }> = [
  { value: "generic", label: "Generic webhook" },
  { value: "discord", label: "Discord" },
  { value: "slack", label: "Slack" },
  { value: "telegram", label: "Telegram" },
  { value: "pushover", label: "Pushover" },
  { value: "ntfy", label: "ntfy" },
  { value: "gotify", label: "Gotify" },
  { value: "apprise", label: "Apprise (generic URL)" }
];

/** Providers that need a target URL, vs. ones addressed purely by credentials. */
function providerNeedsUrl(provider: NotificationProvider): boolean {
  return provider !== "telegram" && provider !== "pushover";
}
function providerUrlLabel(provider: NotificationProvider): string {
  switch (provider) {
    case "ntfy":
      return "Topic URL";
    case "gotify":
      return "Server URL";
    default:
      return "URL";
  }
}
function providerUrlPlaceholder(provider: NotificationProvider): string {
  switch (provider) {
    case "ntfy":
      return "https://ntfy.sh/my-deluno-topic";
    case "gotify":
      return "https://gotify.example.com";
    case "slack":
      return "https://hooks.slack.com/services/…";
    default:
      return "https://example.com/hooks/deluno";
  }
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsNotificationsLoader(): Promise<LoaderData> {
  const [settings, webhooks, deliveries, preferences] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<NotificationWebhookItem[]>("/api/notification-webhooks"),
    fetchJson<NotificationWebhookDeliveryItem[]>("/api/notification-webhooks/deliveries?take=100"),
    fetchJson<NotificationPreferences>("/api/notification-preferences")
  ]);
  return { settings, webhooks, deliveries, preferences };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["SettingsChanged"], areas: [ConfigAreas.notifications] };

export function SettingsNotificationsPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { settings, webhooks, deliveries, preferences } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const sorted = useMemo(() => [...webhooks].sort((a, b) => a.name.localeCompare(b.name)), [webhooks]);
  const [busyPreference, setBusyPreference] = useState<string | null>(null);

  async function togglePreference(key: keyof NotificationPreferences, checked: boolean) {
    setBusyPreference(key);
    try {
      const response = await authedFetch("/api/notification-preferences", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...preferences, [key]: checked })
      });
      if (!response.ok) throw new Error(await readErrorText(response, "Could not save this preference."));
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not save this preference.");
    } finally {
      setBusyPreference(null);
    }
  }

  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<WebhookForm>(emptyForm);
  const [initialForm, setInitialForm] = useState<WebhookForm>(emptyForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [urlError, setUrlError] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const selection = useRowSelection();
  const [tagsOpen, setTagsOpen] = useState(false);

  const isOpen = mode.kind !== "closed";
  const triggers = useMemo(() => parseTriggers(form.eventFilters), [form.eventFilters]);
  const editing = mode.kind === "edit" ? webhooks.find((webhook) => webhook.id === mode.id) ?? null : null;
  const dirty = isOpen && (
    form.name !== initialForm.name ||
    form.url !== initialForm.url ||
    form.eventFilters !== initialForm.eventFilters ||
    form.isEnabled !== initialForm.isEnabled ||
    form.provider !== initialForm.provider ||
    form.tags !== initialForm.tags ||
    form.includeHealthWarnings !== initialForm.includeHealthWarnings ||
    JSON.stringify(form.config) !== JSON.stringify(initialForm.config)
  );
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  function open(webhook: NotificationWebhookItem | null) {
    const next = webhook
      ? {
          name: webhook.name,
          url: webhook.url,
          eventFilters: webhook.eventFilters || "all",
          isEnabled: webhook.isEnabled,
          provider: webhook.provider ?? "generic",
          config: parseConfig(webhook.configJson),
          tags: webhook.tags ?? "",
          includeHealthWarnings: webhook.includeHealthWarnings ?? false
        }
      : emptyForm();
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
    if (providerNeedsUrl(form.provider) && !form.url.trim()) {
      setUrlError("Paste the URL Deluno should POST to.");
      return;
    }
    setBusy("save");
    setSaveState("saving");
    try {
      const nextForm: WebhookForm = {
        name: form.name.trim() || "Webhook",
        url: form.url.trim(),
        eventFilters: form.eventFilters,
        isEnabled: form.isEnabled,
        provider: form.provider,
        config: form.config,
        tags: form.tags.trim(),
        includeHealthWarnings: form.includeHealthWarnings
      };
      const requestBody = { ...nextForm, configJson: serializeConfig(form.config) };
      const response = await authedFetch(mode.kind === "edit" ? `/api/notification-webhooks/${mode.id}` : "/api/notification-webhooks", { method: mode.kind === "edit" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(requestBody) });
      if (!response.ok) throw new Error(await readErrorText(response, mode.kind === "edit" ? "Webhook could not be saved." : "Webhook could not be added."));
      if (mode.kind === "create") {
        const created = (await response.json()) as NotificationWebhookItem;
        setMode({ kind: "edit", id: created.id });
        setSaveMessage("Webhook added");
      } else {
        setSaveMessage("Saved just now");
      }
      setForm(nextForm);
      setInitialForm(nextForm);
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
      if (!response.ok && response.status !== 204) throw new Error(await readErrorText(response, "Webhook could not be removed."));
    }, `${editing?.name ?? "Webhook"} removed`);
    if (!ok) return;
    setConfirmRemove(false);
    setInitialForm(form);
    closeDrawer();
  }

  async function toggleWebhook(webhook: NotificationWebhookItem, isEnabled: boolean) {
    await run(`toggle:${webhook.id}`, async () => {
      const response = await authedFetch(`/api/notification-webhooks/${webhook.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: webhook.name, url: webhook.url, eventFilters: webhook.eventFilters, isEnabled, provider: webhook.provider, configJson: webhook.configJson, includeHealthWarnings: webhook.includeHealthWarnings }) });
      if (!response.ok) throw new Error(await readErrorText(response, `Could not ${isEnabled ? "enable" : "pause"} ${webhook.name}.`));
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
      if (!response.ok) throw new Error(await readErrorText(response, "Test notification could not be sent."));
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
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        tabs={systemSettingsNavItems}
        actions={
          <PageToolbarAction onClick={() => open(null)}>New webhook</PageToolbarAction>
        }
      />

      <ListCard
        title="Webhooks"
        count={webhooks.length ? `${webhooks.length} ${webhooks.length === 1 ? "webhook" : "webhooks"} · ${webhooks.filter((webhook) => webhook.isEnabled).length} enabled · send Deluno events to other tools` : undefined}
        actions={webhooks.length > 0 ? <ConnectionSelectButton active={selection.active} onClick={selection.toggleActive} /> : undefined}
      >
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
          <>
          {selection.active ? (
            <ConnectionSelectionBar count={selection.selected.size} noun="notification connections" onTags={() => setTagsOpen(true)} onClear={selection.clear} />
          ) : null}
          <ListTable
            listId="notification-connections"
            items={sorted}
            columns={[
              { id: "name", label: "Name", sort: (webhook) => webhook.name.toLowerCase() },
              ...(selection.active ? [{ id: "select", label: <ConnectionSelectAll ids={sorted.map((webhook) => webhook.id)} selected={selection.selected} noun="notification connections" onChange={selection.setAll} />, width: "28px", mobile: true, movable: false, sortable: false }] : []),
              { id: "sendsTo", label: "Sends to", width: "minmax(0,1.4fr)", sort: (webhook) => (webhook.url || providerLabel(webhook.provider)).toLowerCase() },
              { id: "events", label: "Events", sort: (webhook) => eventLabel(webhook.eventFilters).toLowerCase() },
              { id: "lastFired", label: "Last fired", sort: (webhook) => (webhook.lastFiredUtc ? new Date(webhook.lastFiredUtc) : null) },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (webhook) => { const chip = statusChip(webhook, settings.enableNotifications); return toneSortValue(chip.tone, chip.label); } },
              { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (webhook) => webhook.isEnabled }
            ]}
          >
            {(rows) => rows.map((webhook) => {
              const chip = statusChip(webhook, settings.enableNotifications);
              return (
                <ListRow key={webhook.id} onClick={() => open(webhook)} selected={mode.kind === "edit" && mode.id === webhook.id}>
                  <ListNameCell name={webhook.name} sub={webhook.lastError ? "Last delivery failed" : providerLabel(webhook.provider)} />
                  {selection.active ? (
                    <ListCell mobile>
                      <Checkbox
                        aria-label={`Select ${webhook.name}`}
                        checked={selection.selected.has(webhook.id)}
                        onClick={(event) => event.stopPropagation()}
                        onCheckedChange={() => selection.toggle(webhook.id)}
                      />
                    </ListCell>
                  ) : null}
                  <ListCell mono primary={webhook.url || providerLabel(webhook.provider)} />
                  <ListCell primary={eventLabel(webhook.eventFilters)} secondary={parseTriggers(webhook.eventFilters).includes("all") ? "Every event type" : "Chosen events"} />
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
          </>
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

      <ListCard title="Import notifications" count="Independently toggleable · on by default, matching what every install has always done">
        <ListTable
          listId="notification-events-import"
          items={IMPORT_PREFERENCE_ROWS}
          columns={[
            { id: "event", label: "Event", sort: (row) => row.name.toLowerCase() },
            { id: "fires", label: "Fires when", sort: (row) => row.description.toLowerCase() },
            { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (row) => Boolean(preferences[row.key]) }
          ]}
          chevron={false}
        >
          {(rows) => rows.map((row) => (
            <ListRow key={row.key}>
              <ListNameCell name={row.name} />
              <ListCell primary={row.description} />
              <ListCell mobile>
                <Switch
                  size="sm"
                  aria-label={row.name}
                  checked={Boolean(preferences[row.key])}
                  disabled={busyPreference === row.key}
                  onCheckedChange={(checked) => void togglePreference(row.key, checked)}
                />
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      </ListCard>

      <ListCard title="Lifecycle notifications" count="Independently toggleable · off by default so nothing new fires unannounced">
        <ListTable
          listId="notification-events-lifecycle"
          items={LIFECYCLE_PREFERENCE_ROWS}
          columns={[
            { id: "event", label: "Event", sort: (row) => row.name.toLowerCase() },
            { id: "fires", label: "Fires when", sort: (row) => row.description.toLowerCase() },
            { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (row) => Boolean(preferences[row.key]) }
          ]}
          chevron={false}
        >
          {(rows) => rows.map((row) => (
            <ListRow key={row.key}>
              <ListNameCell name={row.name} />
              <ListCell primary={row.description} />
              <ListCell mobile>
                <Switch
                  size="sm"
                  aria-label={row.name}
                  checked={Boolean(preferences[row.key])}
                  disabled={busyPreference === row.key}
                  onCheckedChange={(checked) => void togglePreference(row.key, checked)}
                />
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      </ListCard>

      <ListCard title="Recovery & cleanup" count="Off by default · Deluno's own strike/cleanup/queue actions, not just Activity entries">
        <ListTable
          listId="notification-events-recovery"
          items={RECOVERY_CLEANUP_PREFERENCE_ROWS}
          columns={[
            { id: "event", label: "Event", sort: (row) => row.name.toLowerCase() },
            { id: "fires", label: "Fires when", sort: (row) => row.description.toLowerCase() },
            { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (row) => Boolean(preferences[row.key]) }
          ]}
          chevron={false}
        >
          {(rows) => rows.map((row) => (
            <ListRow key={row.key}>
              <ListNameCell name={row.name} />
              <ListCell primary={row.description} />
              <ListCell mobile>
                <Switch
                  size="sm"
                  aria-label={row.name}
                  checked={Boolean(preferences[row.key])}
                  disabled={busyPreference === row.key}
                  onCheckedChange={(checked) => void togglePreference(row.key, checked)}
                />
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      </ListCard>

      <ListCard title="Delivery history" count={deliveries.length ? `${deliveries.length} recent delivery attempts · failures remain replayable` : undefined}>
        {deliveries.length === 0 ? (
          <ListEmpty title="No delivery history yet" description="Deluno will keep a bounded record after the first matching event or test." />
        ) : (
          <ListTable
            chevron={false}
            listId="notification-deliveries"
            items={deliveries}
            columns={[
              { id: "event", label: "Event", sort: (delivery) => delivery.title.toLowerCase() },
              { id: "webhook", label: "Webhook", sort: (delivery) => (webhooks.find((webhook) => webhook.id === delivery.webhookId)?.name ?? delivery.webhookId).toLowerCase() },
              { id: "attempts", label: "Attempts", sort: (delivery) => delivery.attemptCount },
              { id: "result", label: "Last result", sort: (delivery) => delivery.status },
              { id: "action", label: "Action", width: "minmax(8rem,auto)", mobile: true }
            ]}
          >
            {(rows) => rows.map((delivery) => (
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
            <Field label="Provider" help="Each provider gets its own payload shape.">
              <Select
                value={form.provider}
                onChange={(event) => setForm((current) => ({ ...current, provider: event.target.value as NotificationProvider }))}
                options={PROVIDER_OPTIONS}
              />
            </Field>
          </FieldRow>
          <Field label="Send when" help="Tick what this connection should hear about. Each connection chooses for itself.">
            <div className="space-y-1" role="group" aria-label="Send when">
              <CheckboxRow
                label="Everything"
                description="Every kind of event Deluno sends."
                checked={triggers.includes("all")}
                onCheckedChange={(checked) => setForm((current) => ({ ...current, eventFilters: checked ? "all" : "grab,import,upgrade,health" }))}
              />
              {TRIGGER_OPTIONS.map((option) => (
                <CheckboxRow
                  key={option.value}
                  label={option.label}
                  description={option.description}
                  checked={triggers.includes("all") || triggers.includes(option.value)}
                  disabled={triggers.includes("all")}
                  onCheckedChange={(checked) => setForm((current) => {
                    const next = parseTriggers(current.eventFilters).filter((value) => value !== option.value && value !== "all");
                    if (checked) next.push(option.value);
                    return { ...current, eventFilters: next.length ? next.join(",") : "all" };
                  })}
                />
              ))}
            </div>
          </Field>
          {triggers.includes("all") || triggers.includes("health") ? (
            <SwitchRow
              label="Include health warnings"
              description="Also send the smaller advisory warnings (metadata, subtitle providers, Seerr, tags). Off, this connection hears only about problems that stop Deluno working."
              checked={form.includeHealthWarnings}
              onCheckedChange={(checked) => setForm((current) => ({ ...current, includeHealthWarnings: checked }))}
            />
          ) : null}
          <Field label="Tags" optional help="Leave blank to fire for any title. With tags set, Deluno fires this connection for a title only when the title shares one of them — for example, keep an anime channel to anime titles. Tags only limit notifications about a title: health warnings, library pauses, app updates and other general notices always go to every connection.">
            <TagPicker label="Notification tags" value={form.tags} onChange={(tags) => setForm((current) => ({ ...current, tags }))} />
          </Field>
          {providerNeedsUrl(form.provider) ? (
            <Field label={providerUrlLabel(form.provider)} error={urlError} help={form.provider === "generic" ? "Deluno sends a JSON body; no authentication headers are added." : undefined}>
              <Input value={form.url} onChange={(event) => { setUrlError(null); setForm((current) => ({ ...current, url: event.target.value })); }} placeholder={providerUrlPlaceholder(form.provider)} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
            </Field>
          ) : null}
          {form.provider === "telegram" ? (
            <FieldRow>
              <Field label="Bot token" help="From @BotFather.">
                <Input value={form.config.botToken ?? ""} onChange={(event) => setForm((current) => ({ ...current, config: { ...current.config, botToken: event.target.value } }))} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
              </Field>
              <Field label="Chat ID">
                <Input value={form.config.chatId ?? ""} onChange={(event) => setForm((current) => ({ ...current, config: { ...current.config, chatId: event.target.value } }))} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
              </Field>
            </FieldRow>
          ) : null}
          {form.provider === "pushover" ? (
            <FieldRow>
              <Field label="App token">
                <Input value={form.config.appToken ?? ""} onChange={(event) => setForm((current) => ({ ...current, config: { ...current.config, appToken: event.target.value } }))} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
              </Field>
              <Field label="User key">
                <Input value={form.config.userKey ?? ""} onChange={(event) => setForm((current) => ({ ...current, config: { ...current.config, userKey: event.target.value } }))} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
              </Field>
            </FieldRow>
          ) : null}
          {form.provider === "gotify" ? (
            <Field label="App token">
              <Input value={form.config.appToken ?? ""} onChange={(event) => setForm((current) => ({ ...current, config: { ...current.config, appToken: event.target.value } }))} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
            </Field>
          ) : null}
          {form.provider === "ntfy" ? (
            <Field label="Access token" help="Only needed for a protected topic.">
              <Input value={form.config.accessToken ?? ""} onChange={(event) => setForm((current) => ({ ...current, config: { ...current.config, accessToken: event.target.value } }))} className="font-mono text-(length:--type-caption)" autoComplete="off" spellCheck={false} />
            </Field>
          ) : null}
          <SwitchRow label="Enabled" description="Paused webhooks stay configured but never fire." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Delivery" aside={editing.lastFiredUtc ? `last fired ${relative(editing.lastFiredUtc)}` : "never fired"}>
            {editing.lastError ? <p className="text-(length:--type-caption) text-destructive">{editing.lastError}</p> : null}
            {!settings.enableNotifications ? <p className="text-(length:--type-caption) text-warning">Notifications are paused for the whole install, so this webhook will not fire.</p> : null}
            <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void sendTest()} disabled={busy !== null || dirty}>
              {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
              Send a test event
            </Button>
            {dirty ? <p className="text-(length:--type-caption) text-muted-foreground">Save your changes first.</p> : null}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger title="Delete this webhook" description="Nothing else changes; Deluno just stops posting here." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy !== null}>Delete</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConnectionTagsDialog
        open={tagsOpen}
        kind="notification"
        noun="notification connections"
        ids={[...selection.selected]}
        onOpenChange={setTagsOpen}
        onApplied={() => {
          selection.clear();
          revalidator.revalidate();
        }}
      />
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
  return { name: "", url: "", eventFilters: "all", isEnabled: true, provider: "generic", config: {}, tags: "", includeHealthWarnings: false };
}
function parseConfig(configJson: string | null): NotificationProviderConfig {
  if (!configJson) return {};
  try {
    return JSON.parse(configJson) as NotificationProviderConfig;
  } catch {
    return {};
  }
}
function serializeConfig(config: NotificationProviderConfig): string | null {
  const entries = Object.entries(config).filter(([, value]) => Boolean(value && String(value).trim()));
  return entries.length ? JSON.stringify(Object.fromEntries(entries)) : null;
}
function providerLabel(provider: NotificationProvider): string {
  return PROVIDER_OPTIONS.find((option) => option.value === provider)?.label ?? "Outbound webhook";
}
function eventLabel(value: string) {
  const parts = parseTriggers(value);
  if (parts.length === 0 || parts.includes("all")) return "All events";
  return parts
    .map((part) => TRIGGER_OPTIONS.find((option) => option.value === part)?.label ?? EVENT_OPTIONS.find((option) => option.value === part)?.label.split(" — ")[0] ?? part)
    .join(", ");
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
