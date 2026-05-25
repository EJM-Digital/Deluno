import { useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Loader2, Plus, Trash2 } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { toast } from "../components/shell/toaster";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

// ---------------- types ----------------

type NzbServerDto = {
  id: string;
  name: string;
  host: string;
  port: number;
  useTls: boolean;
  username: string | null;
  password: string | null; // empty string from API means "credentials stored, not returned"
  maxConnections: number;
  priority: number;
  tier: "Primary" | "Backup" | "Fill";
  retentionDays: number | null;
  enabled: boolean;
};

type Par2Status = {
  found: boolean;
  resolvedPath: string | null;
  version: string | null;
  error: string | null;
};

type DownloaderDiagnostics = {
  par2: Par2Status;
  extractors: Array<{ format: string; impl: string }>;
  activeJobs: {
    total: number;
    byProtocol: Record<string, number>;
    byState: Record<string, number>;
  };
};

type SecretsBackendInfo = {
  backend: string;
  isHardened: boolean;
  source: string;
  warnings: string[];
};

type LoaderData = {
  servers: NzbServerDto[];
  diagnostics: DownloaderDiagnostics;
  secrets: SecretsBackendInfo | null;
};

// ---------------- loader ----------------

export async function settingsBuiltinDownloaderLoader(): Promise<LoaderData> {
  const [serversRes, diagRes, secretsRes] = await Promise.all([
    authedFetch("/api/downloader/nzb-servers"),
    authedFetch("/api/downloader/diagnostics"),
    authedFetch("/api/diagnostics/secrets-backend"),
  ]);
  const servers = (await serversRes.json()) as NzbServerDto[];
  const diagnostics = (await diagRes.json()) as DownloaderDiagnostics;
  // Older builds may not have the secrets diagnostics endpoint — tolerate 404.
  const secrets = secretsRes.ok ? ((await secretsRes.json()) as SecretsBackendInfo) : null;
  return { servers, diagnostics, secrets };
}

// ---------------- defaults ----------------

const blankServer = (): NzbServerDto => ({
  id: "",
  name: "",
  host: "",
  port: 563,
  useTls: true,
  username: null,
  password: null,
  maxConnections: 8,
  priority: 0,
  tier: "Primary",
  retentionDays: null,
  enabled: true,
});

// ---------------- page ----------------

export function SettingsBuiltinDownloaderPage() {
  const data = useLoaderData() as LoaderData | undefined;
  if (!data) return <RouteSkeleton />;
  const revalidator = useRevalidator();

  // Local copy + the "draft" for the add-new form.
  const [servers, setServers] = useState<NzbServerDto[]>(data.servers);
  const [draft, setDraft] = useState<NzbServerDto>(blankServer());
  const [busy, setBusy] = useState<string | null>(null);

  async function saveServer(server: NzbServerDto, isNew: boolean) {
    setBusy(isNew ? "create" : server.id);
    try {
      const url = isNew
        ? "/api/downloader/nzb-servers"
        : `/api/downloader/nzb-servers/${server.id}`;
      const res = await authedFetch(url, {
        method: isNew ? "POST" : "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(server),
      });
      if (!res.ok) throw new Error(`Save failed (${res.status})`);
      toast.success(isNew ? "Server added" : "Server updated");
      if (isNew) setDraft(blankServer());
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Save failed");
    } finally {
      setBusy(null);
    }
  }

  async function deleteServer(id: string) {
    if (!confirm("Delete this server?")) return;
    setBusy(id);
    try {
      const res = await authedFetch(`/api/downloader/nzb-servers/${id}`, { method: "DELETE" });
      if (!res.ok) throw new Error(`Delete failed (${res.status})`);
      toast.success("Server removed");
      setServers((cur) => cur.filter((s) => s.id !== id));
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Delete failed");
    } finally {
      setBusy(null);
    }
  }

  return (
    <SettingsShell
      title="Built-in downloader"
      description={
        "News servers + diagnostics for the in-process NZB engine. Jobs you grab into a " +
        "Deluno NZB (built-in) download client land here. Configure at least one Primary " +
        "server to get downloads moving."
      }
    >
      <DiagnosticsCard diagnostics={data.diagnostics} secrets={data.secrets} />

      <Card>
        <CardHeader>
          <CardTitle>News servers</CardTitle>
          <CardDescription>
            Add the Usenet servers Deluno will fetch articles from. Primary is tried first,
            then Backup, then Fill. A missing article (430) on one server escalates to the
            next — not a job failure.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {servers.length === 0 ? (
            <p className="text-sm text-muted-foreground">No servers configured yet.</p>
          ) : (
            servers.map((s) => (
              <ServerRow
                key={s.id}
                server={s}
                onChange={(next) =>
                  setServers((cur) => cur.map((x) => (x.id === s.id ? next : x)))
                }
                onSave={(next) => saveServer(next, false)}
                onDelete={() => deleteServer(s.id)}
                busy={busy === s.id}
              />
            ))
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Add server</CardTitle>
          <CardDescription>
            Most Usenet providers use port 563 with TLS. Per-provider connection caps are
            stated in their docs; respect the cap or get rate-limited.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <ServerForm
            value={draft}
            onChange={setDraft}
            onSubmit={() => saveServer(draft, true)}
            submitting={busy === "create"}
            submitLabel="Add server"
          />
        </CardContent>
      </Card>
    </SettingsShell>
  );
}

// ---------------- subcomponents ----------------

function DiagnosticsCard({
  diagnostics,
  secrets,
}: {
  diagnostics: DownloaderDiagnostics;
  secrets: SecretsBackendInfo | null;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Engine diagnostics</CardTitle>
        <CardDescription>
          Current state of the in-process engine and its bundled tooling.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <div>
          <span className="font-medium">par2:</span>{" "}
          {diagnostics.par2.found ? (
            <span className="text-emerald-600 dark:text-emerald-400">
              {diagnostics.par2.version
                ? `bundled v${diagnostics.par2.version}`
                : "available"}{" "}
              ({diagnostics.par2.resolvedPath})
            </span>
          ) : (
            <span className="text-amber-600 dark:text-amber-400">
              not found: {diagnostics.par2.error}
            </span>
          )}
        </div>
        <div>
          <span className="font-medium">Archive extractors:</span>{" "}
          {diagnostics.extractors.map((e) => e.format).join(", ")}
        </div>
        {secrets && (
          <div>
            <span className="font-medium">Secrets backend:</span>{" "}
            {secrets.isHardened ? (
              <span className="text-emerald-600 dark:text-emerald-400">
                {secrets.backend} (hardened; {secrets.source})
              </span>
            ) : (
              <span className="text-amber-600 dark:text-amber-400">
                {secrets.backend} (not hardened; {secrets.source})
              </span>
            )}
          </div>
        )}
        <div>
          <span className="font-medium">Active jobs:</span> {diagnostics.activeJobs.total}
          {diagnostics.activeJobs.total > 0 && (
            <span className="ml-2 text-muted-foreground">
              ({Object.entries(diagnostics.activeJobs.byState)
                .map(([state, count]) => `${state}=${count}`)
                .join(", ")})
            </span>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

function ServerRow({
  server,
  onChange,
  onSave,
  onDelete,
  busy,
}: {
  server: NzbServerDto;
  onChange: (next: NzbServerDto) => void;
  onSave: (next: NzbServerDto) => void;
  onDelete: () => void;
  busy: boolean;
}) {
  return (
    <div className="rounded-md border p-3">
      <ServerForm
        value={server}
        onChange={onChange}
        onSubmit={() => onSave(server)}
        submitting={busy}
        submitLabel="Save changes"
        secondary={
          <Button type="button" variant="outline" onClick={onDelete} disabled={busy}>
            <Trash2 className="mr-2 h-4 w-4" />
            Remove
          </Button>
        }
      />
    </div>
  );
}

function ServerForm({
  value,
  onChange,
  onSubmit,
  submitting,
  submitLabel,
  secondary,
}: {
  value: NzbServerDto;
  onChange: (next: NzbServerDto) => void;
  onSubmit: () => void;
  submitting: boolean;
  submitLabel: string;
  secondary?: React.ReactNode;
}) {
  function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    onSubmit();
  }
  return (
    <form onSubmit={handleSubmit} className="grid gap-3">
      <div className="grid gap-3 sm:grid-cols-2">
        <LabeledInput
          label="Name"
          value={value.name}
          onChange={(v) => onChange({ ...value, name: v })}
          required
        />
        <LabeledInput
          label="Host"
          value={value.host}
          onChange={(v) => onChange({ ...value, host: v })}
          placeholder="news.example.com"
          required
        />
        <LabeledInput
          label="Port"
          type="number"
          value={value.port.toString()}
          onChange={(v) => onChange({ ...value, port: parseInt(v, 10) || 0 })}
          required
        />
        <LabeledInput
          label="Username (optional)"
          value={value.username ?? ""}
          onChange={(v) => onChange({ ...value, username: v.length > 0 ? v : null })}
        />
        <LabeledInput
          label={value.password === "" ? "Password (currently set; type to change)" : "Password (optional)"}
          type="password"
          value={value.password ?? ""}
          onChange={(v) => onChange({ ...value, password: v.length > 0 ? v : null })}
          placeholder={value.password === "" ? "********" : ""}
        />
        <LabeledInput
          label="Max connections"
          type="number"
          value={value.maxConnections.toString()}
          onChange={(v) => onChange({ ...value, maxConnections: parseInt(v, 10) || 1 })}
        />
        <LabeledInput
          label="Priority (lower = first)"
          type="number"
          value={value.priority.toString()}
          onChange={(v) => onChange({ ...value, priority: parseInt(v, 10) || 0 })}
        />
        <LabeledInput
          label="Retention days (optional)"
          type="number"
          value={value.retentionDays?.toString() ?? ""}
          onChange={(v) =>
            onChange({
              ...value,
              retentionDays: v.length > 0 ? parseInt(v, 10) || null : null,
            })
          }
        />
        <LabeledSelect
          label="Tier"
          value={value.tier}
          options={["Primary", "Backup", "Fill"]}
          onChange={(v) => onChange({ ...value, tier: v as NzbServerDto["tier"] })}
        />
      </div>
      <div className="flex items-center gap-4">
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={value.useTls}
            onChange={(e) => onChange({ ...value, useTls: e.target.checked })}
          />
          TLS
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={value.enabled}
            onChange={(e) => onChange({ ...value, enabled: e.target.checked })}
          />
          Enabled
        </label>
      </div>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={submitting}>
          {submitting ? (
            <Loader2 className="mr-2 h-4 w-4 animate-spin" />
          ) : (
            <Plus className="mr-2 h-4 w-4" />
          )}
          {submitLabel}
        </Button>
        {secondary}
      </div>
    </form>
  );
}

function LabeledInput({
  label,
  value,
  onChange,
  type = "text",
  placeholder,
  required,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  type?: string;
  placeholder?: string;
  required?: boolean;
}) {
  return (
    <label className="grid gap-1 text-sm">
      <span className="font-medium">{label}</span>
      <Input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        required={required}
      />
    </label>
  );
}

function LabeledSelect({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: string[];
  onChange: (v: string) => void;
}) {
  return (
    <label className="grid gap-1 text-sm">
      <span className="font-medium">{label}</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="h-9 rounded-md border bg-background px-3 text-sm"
      >
        {options.map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </select>
    </label>
  );
}
