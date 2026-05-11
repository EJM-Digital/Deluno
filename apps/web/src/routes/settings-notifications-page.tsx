import { useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Bell, CheckCircle2, LoaderCircle, Plus, Send, Trash2, X } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { toast } from "../components/shell/toaster";
import { fetchJson, type NotificationWebhookItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

interface LoaderData {
  webhooks: NotificationWebhookItem[];
}

export async function settingsNotificationsLoader(): Promise<LoaderData> {
  const webhooks = await fetchJson<NotificationWebhookItem[]>("/api/notification-webhooks");
  return { webhooks };
}

const EVENT_OPTIONS = [
  { value: "all", label: "All events" },
  { value: "grab", label: "Grab (download started)" },
  { value: "import", label: "Import (file moved)" },
  { value: "upgrade", label: "Upgrade (better quality found)" },
  { value: "health", label: "Health alerts" },
  { value: "test", label: "Test events" }
];

interface WebhookFormState {
  name: string;
  url: string;
  eventFilters: string;
  isEnabled: boolean;
}

function emptyForm(): WebhookFormState {
  return { name: "", url: "", eventFilters: "all", isEnabled: true };
}

export function SettingsNotificationsPage() {
  const loaderData = useLoaderData() as LoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { webhooks } = loaderData;
  const revalidator = useRevalidator();

  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState<WebhookFormState>(emptyForm);
  const [busyKey, setBusyKey] = useState<string | null>(null);

  async function handleCreate(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!form.url.trim()) {
      toast.error("Webhook URL is required.");
      return;
    }
    setBusyKey("create");
    try {
      const res = await authedFetch("/api/notification-webhooks", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: form.name.trim() || "Webhook",
          url: form.url.trim(),
          eventFilters: form.eventFilters,
          isEnabled: form.isEnabled
        })
      });
      if (!res.ok) throw new Error("Webhook could not be created.");
      toast.success("Webhook added");
      setForm(emptyForm());
      setShowCreate(false);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Webhook could not be created.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleToggle(webhook: NotificationWebhookItem) {
    setBusyKey(`toggle:${webhook.id}`);
    try {
      const res = await authedFetch(`/api/notification-webhooks/${webhook.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: webhook.name,
          url: webhook.url,
          eventFilters: webhook.eventFilters,
          isEnabled: !webhook.isEnabled
        })
      });
      if (!res.ok) throw new Error("Webhook could not be updated.");
      toast.success(webhook.isEnabled ? `"${webhook.name}" disabled` : `"${webhook.name}" enabled`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Update failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleTest(webhook: NotificationWebhookItem) {
    setBusyKey(`test:${webhook.id}`);
    try {
      const res = await authedFetch(`/api/notification-webhooks/${webhook.id}/test`, { method: "POST" });
      if (!res.ok) throw new Error("Test notification could not be sent.");
      toast.success(`Test sent to "${webhook.name}"`);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Test failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDelete(webhook: NotificationWebhookItem) {
    if (!window.confirm(`Remove webhook "${webhook.name}"? This cannot be undone.`)) return;
    setBusyKey(`delete:${webhook.id}`);
    try {
      const res = await authedFetch(`/api/notification-webhooks/${webhook.id}`, { method: "DELETE" });
      if (!res.ok && res.status !== 204) throw new Error("Webhook could not be removed.");
      toast.success("Webhook removed");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Remove failed.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title="Notifications"
      description="Send outbound webhook events to Discord, Slack, Gotify, ntfy, or any HTTP endpoint when Deluno grabs, imports, upgrades, or detects a health issue."
    >
      <div className="settings-split settings-split-config-heavy">
        <div className="settings-panel space-y-[calc(var(--field-group-pad)*0.9)]">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="font-semibold text-foreground">Webhook endpoints</h3>
              <p className="text-[12px] text-muted-foreground">
                Deluno will POST a JSON payload to each enabled endpoint when the selected events fire.
              </p>
            </div>
            {!showCreate && (
              <Button size="sm" onClick={() => setShowCreate(true)} className="gap-2 shrink-0">
                <Plus className="h-4 w-4" />
                Add webhook
              </Button>
            )}
          </div>

          {showCreate && (
            <div className="rounded-2xl border border-primary/25 bg-surface-1 p-5 shadow-[0_0_30px_hsl(var(--primary)/0.06)]">
              <div className="mb-4 flex items-center justify-between">
                <p className="font-semibold text-foreground">New webhook</p>
                <button
                  type="button"
                  onClick={() => { setShowCreate(false); setForm(emptyForm()); }}
                  className="rounded-xl p-1.5 text-muted-foreground hover:bg-muted/30 hover:text-foreground"
                >
                  <X className="h-4 w-4" />
                </button>
              </div>
              <form className="space-y-3" onSubmit={handleCreate}>
                <FieldRow label="Name">
                  <Input
                    value={form.name}
                    onChange={(e) => setForm((prev) => ({ ...prev, name: e.target.value }))}
                    placeholder="Discord alerts"
                  />
                </FieldRow>
                <FieldRow label="URL">
                  <Input
                    value={form.url}
                    onChange={(e) => setForm((prev) => ({ ...prev, url: e.target.value }))}
                    placeholder="https://discord.com/api/webhooks/..."
                    required
                    type="url"
                  />
                </FieldRow>
                <FieldRow label="Events">
                  <select
                    value={form.eventFilters}
                    onChange={(e) => setForm((prev) => ({ ...prev, eventFilters: e.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    {EVENT_OPTIONS.map((opt) => (
                      <option key={opt.value} value={opt.value}>{opt.label}</option>
                    ))}
                  </select>
                </FieldRow>
                <label className="flex items-center gap-3 text-[13px] text-foreground">
                  <input
                    type="checkbox"
                    checked={form.isEnabled}
                    onChange={(e) => setForm((prev) => ({ ...prev, isEnabled: e.target.checked }))}
                  />
                  Enable immediately
                </label>
                <div className="flex gap-2 pt-1">
                  <Button type="submit" size="sm" disabled={busyKey === "create"} className="gap-2">
                    {busyKey === "create" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Bell className="h-4 w-4" />}
                    Add webhook
                  </Button>
                  <Button type="button" size="sm" variant="ghost" onClick={() => { setShowCreate(false); setForm(emptyForm()); }}>
                    Cancel
                  </Button>
                </div>
              </form>
            </div>
          )}

          {webhooks.length > 0 ? (
            <div className="space-y-2.5">
              {webhooks.map((webhook) => (
                <div
                  key={webhook.id}
                  className={`group rounded-2xl border border-hairline bg-surface-1 p-4 transition-opacity ${!webhook.isEnabled ? "opacity-60" : ""}`}
                >
                  <div className="flex items-start gap-3">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="font-medium text-foreground">{webhook.name}</p>
                        <span className={`rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide ${
                          webhook.isEnabled
                            ? "border-success/30 bg-success/10 text-success"
                            : "border-hairline text-muted-foreground"
                        }`}>
                          {webhook.isEnabled ? "enabled" : "disabled"}
                        </span>
                        <span className="rounded-full border border-hairline px-2 py-0.5 text-[10px] text-muted-foreground">
                          {webhook.eventFilters}
                        </span>
                      </div>
                      <p className="mt-1 break-all font-mono text-[11px] text-muted-foreground">{webhook.url}</p>
                      {webhook.lastFiredUtc && (
                        <p className="mt-0.5 text-[11px] text-muted-foreground">
                          Last fired: {new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(webhook.lastFiredUtc))}
                        </p>
                      )}
                      {webhook.lastError && (
                        <p className="mt-0.5 text-[11px] text-destructive">{webhook.lastError}</p>
                      )}
                    </div>
                    <div className="flex items-center gap-2 opacity-0 group-hover:opacity-100 transition-opacity">
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => void handleToggle(webhook)}
                        disabled={busyKey === `toggle:${webhook.id}`}
                        className="gap-1.5"
                      >
                        {busyKey === `toggle:${webhook.id}` ? (
                          <LoaderCircle className="h-3 w-3 animate-spin" />
                        ) : webhook.isEnabled ? (
                          <X className="h-3 w-3" />
                        ) : (
                          <CheckCircle2 className="h-3 w-3" />
                        )}
                        {webhook.isEnabled ? "Disable" : "Enable"}
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => void handleTest(webhook)}
                        disabled={busyKey === `test:${webhook.id}` || !webhook.isEnabled}
                        title={webhook.isEnabled ? "Send a test event to this webhook" : "Enable the webhook before testing"}
                        className="gap-1.5"
                      >
                        {busyKey === `test:${webhook.id}` ? <LoaderCircle className="h-3 w-3 animate-spin" /> : <Send className="h-3 w-3" />}
                        Test
                      </Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => void handleDelete(webhook)}
                        disabled={busyKey === `delete:${webhook.id}`}
                        title="Remove webhook"
                      >
                        {busyKey === `delete:${webhook.id}` ? (
                          <LoaderCircle className="h-3.5 w-3.5 animate-spin" />
                        ) : (
                          <Trash2 className="h-3.5 w-3.5 text-muted-foreground" />
                        )}
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : !showCreate ? (
            <div className="flex flex-col items-center gap-4 rounded-2xl border-2 border-dashed border-hairline py-12 text-center">
              <Bell className="h-8 w-8 text-muted-foreground/30" />
              <div>
                <p className="font-medium text-foreground">No webhooks yet</p>
                <p className="mt-1 text-[12px] text-muted-foreground">
                  Add a webhook to notify Discord, Slack, or any HTTP endpoint when Deluno acts.
                </p>
              </div>
              <Button size="sm" onClick={() => setShowCreate(true)} className="gap-2">
                <Plus className="h-4 w-4" />
                Add webhook
              </Button>
            </div>
          ) : null}
        </div>

        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>How webhooks work</CardTitle>
            <CardDescription>
              Deluno sends a JSON POST to every enabled webhook when a matching event fires.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4 text-[13px] text-muted-foreground">
            <div className="space-y-3">
              {[
                {
                  event: "grab",
                  desc: "Fires when Deluno dispatches a download to a client. Payload includes title, quality, indexer, and release name."
                },
                {
                  event: "import",
                  desc: "Fires after a file is moved to the library root. Payload includes final path, quality, and movie or episode details."
                },
                {
                  event: "upgrade",
                  desc: "Fires when an existing file is replaced with a better quality copy. Includes quality delta."
                },
                {
                  event: "health",
                  desc: "Fires when an indexer, download client, or metadata provider enters an unhealthy state."
                }
              ].map((item) => (
                <div key={item.event} className="rounded-xl border border-hairline bg-surface-1 p-3">
                  <p className="font-mono text-[11px] font-semibold uppercase tracking-widest text-primary">{item.event}</p>
                  <p className="mt-1">{item.desc}</p>
                </div>
              ))}
            </div>

            <div className="rounded-xl border border-hairline bg-surface-1 p-3">
              <p className="font-semibold text-foreground">Compatible services</p>
              <div className="mt-2 flex flex-wrap gap-2">
                {["Discord", "Slack", "Gotify", "ntfy", "Apprise", "Zapier", "n8n", "Any HTTP endpoint"].map((s) => (
                  <span key={s} className="rounded-full border border-hairline px-2 py-0.5 text-[11px] text-muted-foreground">{s}</span>
                ))}
              </div>
            </div>

            <div className="rounded-xl border border-hairline bg-surface-1 p-3">
              <p className="font-semibold text-foreground">Payload shape</p>
              <pre className="mt-2 overflow-x-auto text-[11px] text-muted-foreground">{`{
  "event": "grab",
  "title": "The Matrix",
  "quality": "Bluray 1080p",
  "releaseName": "The.Matrix.1999...",
  "indexer": "Prowlarr",
  "timestamp": "2026-01-01T00:00:00Z"
}`}</pre>
            </div>
          </CardContent>
        </Card>
      </div>
    </SettingsShell>
  );
}

function FieldRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
    </div>
  );
}
