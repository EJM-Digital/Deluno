import { useState, type FormEvent, type ReactNode } from "react";
import { BookOpenText, Copy, KeyRound, LoaderCircle, ShieldCheck, Trash2 } from "lucide-react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { SaveStatus, useSaveStatus } from "../components/shell/save-status";
import { toast } from "../components/shell/toaster";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { PresetField } from "../components/ui/preset-field";
import { authedFetch } from "../lib/use-auth";
import { fetchJson, type ApiKeyItem, type CreatedApiKeyResponse } from "../lib/api";
import { RouteSkeleton } from "../components/shell/skeleton";

interface SystemApiLoaderData {
  apiKeys: ApiKeyItem[];
}

export async function systemApiLoader(): Promise<SystemApiLoaderData> {
  const apiKeys = await fetchJson<ApiKeyItem[]>("/api/api-keys");
  return { apiKeys };
}

export function SystemApiPage() {
  const loaderData = useLoaderData() as SystemApiLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const apiKeys = loaderData.apiKeys;
  const revalidator = useRevalidator();
  const save = useSaveStatus();
  const [name, setName] = useState("External automation");
  const [scopes, setScopes] = useState("all");
  const [createdKey, setCreatedKey] = useState<CreatedApiKeyResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    save.markSyncing("Generating...");

    try {
      const created = await fetchJson<CreatedApiKeyResponse>("/api/api-keys", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name, scopes })
      });

      setCreatedKey(created);
      setName("");
      save.markSaved("API key generated");
      toast.success("API key generated");
      revalidator.revalidate();
    } catch (error) {
      const message = error instanceof Error ? error.message : "API key could not be generated.";
      save.markError(message);
      toast.error(message);
    } finally {
      setBusy(false);
    }
  }

  async function handleCopy(value: string) {
    await navigator.clipboard.writeText(value);
    toast.success("Copied to clipboard");
  }

  async function handleDelete(item: ApiKeyItem) {
    if (!window.confirm(`Revoke "${item.name}"? Anything using this key will stop authenticating immediately.`)) {
      return;
    }

    setDeletingId(item.id);
    try {
      const response = await authedFetch(`/api/api-keys/${item.id}`, { method: "DELETE" });
      if (!response.ok) {
        throw new Error("API key could not be revoked.");
      }

      if (createdKey?.item.id === item.id) {
        setCreatedKey(null);
      }

      toast.success("API key revoked");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "API key could not be revoked.");
    } finally {
      setDeletingId(null);
    }
  }

  return (
    <div className="settings-split settings-split-config-heavy">
      <Card className="settings-panel">
        <CardHeader>
          <CardTitle className="flex items-center justify-between gap-3">
            Generate an API key
            <SaveStatus state={save.state} message={save.message} />
          </CardTitle>
          <CardDescription>
            API keys are shown once. Deluno stores only a hash, so copy the generated value before leaving this page.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-[var(--field-group-pad)]">
          {createdKey ? (
            <div className="rounded-2xl border border-primary/35 bg-primary/10 p-[var(--tile-pad)]">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <p className="flex items-center gap-2 font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
                    <ShieldCheck className="h-5 w-5 text-primary" />
                    Key created
                  </p>
                  <p className="mt-2 max-w-2xl density-help leading-relaxed text-muted-foreground">
                    Store this in the calling app now. It will not be shown again after refresh.
                  </p>
                </div>
                <Badge variant="success">one-time secret</Badge>
              </div>
              <div className="mt-4 flex min-w-0 flex-col gap-3 sm:flex-row">
                <code className="min-w-0 flex-1 overflow-x-auto rounded-xl border border-hairline bg-background/70 p-3 font-mono text-[length:var(--type-body)] text-foreground">
                  {createdKey.apiKey}
                </code>
                <Button type="button" variant="outline" onClick={() => void handleCopy(createdKey.apiKey)}>
                  <Copy className="h-4 w-4" />
                  Copy
                </Button>
              </div>
            </div>
          ) : null}

            <form className="grid gap-[var(--grid-gap)]" onSubmit={handleCreate}>
              <Field label="Key name">
                <Input
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                  placeholder="External automation"
                  required
                />
                <p className="density-help mt-2 text-muted-foreground">
                  Use a human name that tells you where the key is used. Example: dashboard, mobile app, Home Assistant, backup script.
                </p>
              </Field>

              <Field label="Access">
                <PresetField
                  value={scopes}
                  onChange={setScopes}
                  options={[
                    { label: "Full local API access", value: "all" },
                    { label: "Read-only telemetry", value: "read" },
                    { label: "Media automation", value: "read, queue, imports, health" }
                  ]}
                  customLabel="Custom scopes"
                  customPlaceholder="read, queue, imports"
                />
                <p className="density-help mt-2 text-muted-foreground">
                  Scope names are stored now so we can enforce fine-grained permissions later without changing existing keys.
                </p>
              </Field>

              <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.75)]">
                <p className="font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
                  How integrations authenticate
                </p>
                <p className="mt-2 density-help leading-relaxed text-muted-foreground">
                  Send the key as <code className="font-mono text-foreground">X-Api-Key</code>. Bearer tokens with a
                  <code className="ml-1 font-mono text-foreground">deluno_</code> prefix are also accepted for clients that only support Authorization headers.
                </p>
              </div>

              <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.75)]">
                <p className="font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
                  Integration endpoints
                </p>
                <p className="mt-2 density-help leading-relaxed text-muted-foreground">
                  External tools should start with the manifest, then read queue/health/activity. Processor tools can post events
                  so Deluno can pause standard import until a cleaned output is ready.
                </p>
                <div className="mt-3 grid gap-2">
                  {[
                    ["read", "GET", "/api/integrations/external/manifest"],
                    ["read", "GET", "/api/integrations/external/health"],
                    ["read", "GET", "/api/integrations/external/queue"],
                    ["read", "GET", "/api/integrations/external/activity"],
                    ["imports", "POST", "/api/integrations/external/import-preview"],
                    ["imports", "POST", "/api/integrations/processors/events"],
                    ["queue", "POST", "/api/integrations/external/trigger-refresh"]
                  ].map(([scope, method, path]) => (
                    <div key={`${method}:${path}`} className="grid gap-2 rounded-lg border border-hairline bg-background/35 p-3 sm:grid-cols-[6rem_1fr_6rem] sm:items-center">
                      <span className="font-mono text-[length:var(--type-caption)] font-bold text-primary">{method}</span>
                      <code className="overflow-x-auto font-mono text-[length:var(--type-caption)] text-foreground">{path}</code>
                      <span className="text-[length:var(--type-caption)] text-muted-foreground">scope: {scope}</span>
                    </div>
                  ))}
                </div>
                <pre className="mt-3 overflow-x-auto rounded-xl border border-hairline bg-background/70 p-3 text-[length:var(--type-caption)] text-muted-foreground">
{`curl -H "X-Api-Key: deluno_..." http://127.0.0.1:5099/api/integrations/external/health

curl -H "Authorization: Bearer deluno_..." http://127.0.0.1:5099/api/integrations/external/queue?take=25`}
                </pre>
              </div>

              <div className="grid gap-3 lg:grid-cols-3">
                <ApiGuideCard
                  title="1. Discover"
                  body="Read the manifest to learn API version, supported routes, scopes, and feature flags before calling anything else."
                />
                <ApiGuideCard
                  title="2. Observe"
                  body="Use health, queue, activity, and import preview for dashboards, processors, scripts, and local automation."
                />
                <ApiGuideCard
                  title="3. Coordinate"
                  body="Use processor events when a Refiner-style tool cleans media before Deluno imports, renames, and files it."
                />
              </div>

              <Button type="submit" disabled={busy || !name.trim()}>
                {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <KeyRound className="h-4 w-4" />}
                Generate API key
              </Button>
            </form>
        </CardContent>
      </Card>

      <Card className="settings-panel">
        <CardHeader>
          <CardTitle>Active keys</CardTitle>
          <CardDescription>Revoke keys that are no longer needed. Revocation takes effect immediately.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          {apiKeys.length === 0 ? (
            <div className="rounded-xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
              <p className="font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
                No API keys yet
              </p>
              <p className="mt-2 density-help leading-relaxed text-muted-foreground">
                Generate one when another app or script needs to consume Deluno without a browser session.
              </p>
            </div>
          ) : (
            apiKeys.map((item) => (
              <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.75)]">
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p className="truncate font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
                      {item.name}
                    </p>
                    <p className="mt-1 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                      {item.prefix}
                    </p>
                  </div>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={deletingId === item.id}
                    onClick={() => void handleDelete(item)}
                  >
                    {deletingId === item.id ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                    Revoke
                  </Button>
                </div>
                <div className="mt-4 grid gap-2 sm:grid-cols-3">
                  <ApiKeyStat label="Scopes" value={item.scopes} />
                  <ApiKeyStat label="Created" value={formatWhen(item.createdUtc)} />
                  <ApiKeyStat label="Last used" value={item.lastUsedUtc ? formatWhen(item.lastUsedUtc) : "Never"} />
                </div>
              </div>
            ))
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function Field({ children, label }: { children: ReactNode; label: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
    </div>
  );
}

function ApiKeyStat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-hairline bg-background/35 p-3">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <p className="mt-1 break-words text-[length:var(--type-body)] font-semibold text-foreground">{value}</p>
    </div>
  );
}

function ApiGuideCard({ body, title }: { body: string; title: string }) {
  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.75)]">
      <p className="flex items-center gap-2 font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
        <BookOpenText className="h-4 w-4 text-primary" />
        {title}
      </p>
      <p className="mt-2 density-help leading-relaxed text-muted-foreground">{body}</p>
    </div>
  );
}

function formatWhen(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}
