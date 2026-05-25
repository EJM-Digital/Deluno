import { useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Loader2, Save, RotateCcw } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { toast } from "../components/shell/toaster";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

// ---------------- types ----------------

// Matches Deluno.Downloader.Torrent.Engine.TorrentEngineConfig (camelCased over the wire).
type TorrentEngineConfig = {
  listenPort: number;
  allowUpnp: boolean;
  allowLsd: boolean;
  maxGlobalConnections: number;
  maxUploadBytesPerSecond: number;
  maxDownloadBytesPerSecond: number;
  defaultRatioTarget: number | null;
  defaultSeedTimeTargetMinutes: number | null;
  magnetMetadataTimeoutSeconds: number;
};

type LoaderData = { config: TorrentEngineConfig };

// ---------------- loader ----------------

export async function settingsBuiltinTorrentLoader(): Promise<LoaderData> {
  const res = await authedFetch("/api/downloader/torrent-config");
  if (!res.ok) throw new Error(`Load failed (${res.status})`);
  return { config: (await res.json()) as TorrentEngineConfig };
}

// ---------------- defaults ----------------

const factoryDefaults = (): TorrentEngineConfig => ({
  listenPort: 51413,
  allowUpnp: false,
  allowLsd: false,
  maxGlobalConnections: 200,
  maxUploadBytesPerSecond: 0,
  maxDownloadBytesPerSecond: 0,
  defaultRatioTarget: 1.0,
  defaultSeedTimeTargetMinutes: null,
  magnetMetadataTimeoutSeconds: 300,
});

// ---------------- page ----------------

export function SettingsBuiltinTorrentPage() {
  const data = useLoaderData() as LoaderData | undefined;
  if (!data) return <RouteSkeleton />;
  const revalidator = useRevalidator();

  const [config, setConfig] = useState<TorrentEngineConfig>(data.config);
  const [busy, setBusy] = useState(false);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      const res = await authedFetch("/api/downloader/torrent-config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(config),
      });
      if (!res.ok) {
        const body = await res.text();
        throw new Error(body || `Save failed (${res.status})`);
      }
      toast.success("Torrent engine config saved — restart Deluno to apply.");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Save failed");
    } finally {
      setBusy(false);
    }
  }

  function resetToDefaults() {
    if (!confirm("Reset all torrent engine settings to factory defaults?")) return;
    setConfig(factoryDefaults());
  }

  return (
    <SettingsShell
      title="Built-in torrent engine"
      description={
        "MonoTorrent-backed engine that runs in-process. Configures the listen port, peer-discovery " +
        "toggles, rate limits, and seeding policy applied to new torrents. Changes take effect on next " +
        "Deluno restart."
      }
    >
      <form onSubmit={save}>
        <Card>
          <CardHeader>
            <CardTitle>Network</CardTitle>
            <CardDescription>
              Listen port for incoming peer connections. UPnP and LSD are off by default — they leak the
              port to discovery services, which most private trackers prohibit. Turn on if you only use
              public swarms.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <Field
              label="Listen port"
              hint="TCP/UDP port for peer connections. 51413 matches qBittorrent's default."
            >
              <Input
                type="number"
                min={0}
                max={65535}
                value={config.listenPort}
                onChange={(e) => setConfig({ ...config, listenPort: Number(e.target.value) })}
              />
            </Field>
            <Checkbox
              label="Allow UPnP port forwarding"
              hint="Automatically open the listen port via the router. Private-tracker users should leave this off."
              checked={config.allowUpnp}
              onChange={(v) => setConfig({ ...config, allowUpnp: v })}
            />
            <Checkbox
              label="Allow Local Service Discovery (LSD)"
              hint="Multicast peer discovery on the LAN. Public-only setups only."
              checked={config.allowLsd}
              onChange={(v) => setConfig({ ...config, allowLsd: v })}
            />
          </CardContent>
        </Card>

        <Card className="mt-4">
          <CardHeader>
            <CardTitle>Limits</CardTitle>
            <CardDescription>
              Global caps across all torrents. 0 means unlimited.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <Field label="Max global connections">
              <Input
                type="number"
                min={1}
                value={config.maxGlobalConnections}
                onChange={(e) => setConfig({ ...config, maxGlobalConnections: Number(e.target.value) })}
              />
            </Field>
            <Field label="Upload rate cap (bytes/sec, 0 = unlimited)">
              <Input
                type="number"
                min={0}
                value={config.maxUploadBytesPerSecond}
                onChange={(e) =>
                  setConfig({ ...config, maxUploadBytesPerSecond: Number(e.target.value) })
                }
              />
            </Field>
            <Field label="Download rate cap (bytes/sec, 0 = unlimited)">
              <Input
                type="number"
                min={0}
                value={config.maxDownloadBytesPerSecond}
                onChange={(e) =>
                  setConfig({ ...config, maxDownloadBytesPerSecond: Number(e.target.value) })
                }
              />
            </Field>
          </CardContent>
        </Card>

        <Card className="mt-4">
          <CardHeader>
            <CardTitle>Seeding policy</CardTitle>
            <CardDescription>
              Applied to every new torrent. Per-torrent overrides come from the job (e.g. category-specific
              targets).
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <Field
              label="Default ratio target"
              hint="Stop seeding when uploaded/downloaded reaches this. 1.0 = seed back what you grabbed. Empty = no ratio cap."
            >
              <Input
                type="number"
                step="0.1"
                min={0}
                value={config.defaultRatioTarget ?? ""}
                placeholder="empty = no cap"
                onChange={(e) =>
                  setConfig({
                    ...config,
                    defaultRatioTarget: e.target.value === "" ? null : Number(e.target.value),
                  })
                }
              />
            </Field>
            <Field
              label="Default seed-time target (minutes)"
              hint="Stop seeding after this many minutes past completion. Empty = no time cap."
            >
              <Input
                type="number"
                min={0}
                value={config.defaultSeedTimeTargetMinutes ?? ""}
                placeholder="empty = no cap"
                onChange={(e) =>
                  setConfig({
                    ...config,
                    defaultSeedTimeTargetMinutes: e.target.value === "" ? null : Number(e.target.value),
                  })
                }
              />
            </Field>
          </CardContent>
        </Card>

        <Card className="mt-4">
          <CardHeader>
            <CardTitle>Magnet handling</CardTitle>
            <CardDescription>
              Magnet links need BEP-9 metadata exchange before download can start. This cap stops a magnet
              with no peers from blocking a worker slot forever.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <Field
              label="Magnet metadata timeout (seconds)"
              hint="5 minutes is generous; tracker-resolved magnets usually complete in seconds."
            >
              <Input
                type="number"
                min={5}
                value={config.magnetMetadataTimeoutSeconds}
                onChange={(e) =>
                  setConfig({ ...config, magnetMetadataTimeoutSeconds: Number(e.target.value) })
                }
              />
            </Field>
          </CardContent>
        </Card>

        <div className="mt-6 flex gap-2">
          <Button type="submit" disabled={busy}>
            {busy ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Save className="mr-2 h-4 w-4" />}
            Save
          </Button>
          <Button type="button" variant="outline" onClick={resetToDefaults} disabled={busy}>
            <RotateCcw className="mr-2 h-4 w-4" />
            Restore defaults
          </Button>
        </div>
      </form>
    </SettingsShell>
  );
}

// ---------------- form primitives ----------------

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1">
      <label className="text-sm font-medium">{label}</label>
      {children}
      {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
    </div>
  );
}

function Checkbox({
  label,
  hint,
  checked,
  onChange,
}: {
  label: string;
  hint?: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <label className="flex items-start gap-3 cursor-pointer">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="mt-1"
      />
      <div>
        <div className="text-sm font-medium">{label}</div>
        {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
      </div>
    </label>
  );
}
