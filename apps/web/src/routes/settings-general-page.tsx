import { useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { LoaderCircle } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { PresetField } from "../components/ui/preset-field";
import { SaveStatus, useSaveStatus } from "../components/shell/save-status";
import { toast } from "../components/shell/toaster";
import { settingsOverviewLoader } from "./settings-overview-page";
import { emptyPlatformSettingsSnapshot, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsGeneralLoader = settingsOverviewLoader;

export function SettingsGeneralPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  const settings = loaderData?.settings ?? emptyPlatformSettingsSnapshot;
  const revalidator = useRevalidator();
  const [formState, setFormState] = useState(settings);
  const [busy, setBusy] = useState(false);
  const save = useSaveStatus();

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    save.markSyncing("Saving…");

    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formState)
      });

      if (!response.ok) {
        throw new Error("General settings could not be saved.");
      }

      save.markSaved();
      toast.success("General settings saved");
      revalidator.revalidate();
    } catch (error) {
      const msg = error instanceof Error ? error.message : "General settings could not be saved.";
      save.markError(msg);
      toast.error(msg);
    } finally {
      setBusy(false);
    }
  }

  return (
    <SettingsShell
      title="General"
      description="Host, runtime, identity, and basic application behavior should live here rather than being mixed into media policy pages."
    >
      <div className="settings-split settings-split-balanced">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle className="flex items-center justify-between gap-3">
              Instance and host
              <SaveStatus state={save.state} message={save.message} />
            </CardTitle>
            <CardDescription>Persisted general settings for this Deluno instance.</CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handleSave}>
              <div className="grid gap-[var(--grid-gap)] sm:grid-cols-2">
                <Field label="Instance">
                  <Input
                    value={formState.appInstanceName}
                    onChange={(event) =>
                      setFormState((current) => ({ ...current, appInstanceName: event.target.value }))
                    }
                  />
                </Field>
                <Field label="Bind address">
                  <PresetField
                    value={formState.hostBindAddress}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, hostBindAddress: value }))
                    }
                    options={[
                      { label: "Local machine only (127.0.0.1)", value: "127.0.0.1" },
                      { label: "All network interfaces (0.0.0.0)", value: "0.0.0.0" },
                      { label: "IPv6 localhost (::1)", value: "::1" }
                    ]}
                    customLabel="Custom bind address"
                    customPlaceholder="IP address or hostname"
                  />
                </Field>
                <Field label="Port">
                  <PresetField
                    inputType="number"
                    value={String(formState.hostPort)}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, hostPort: Number(value || 5099) }))
                    }
                    options={[
                      { label: "Deluno default (5099)", value: "5099" },
                      { label: "Radarr-style port (7878)", value: "7878" },
                      { label: "Sonarr-style port (8989)", value: "8989" },
                      { label: "Prowlarr-style port (9696)", value: "9696" }
                    ]}
                    customLabel="Custom port"
                    customPlaceholder="Port number"
                  />
                </Field>
                <Field label="URL base">
                  <PresetField
                    value={formState.urlBase}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, urlBase: value }))
                    }
                    options={[
                      { label: "None, serve at /", value: "" },
                      { label: "/deluno", value: "/deluno" },
                      { label: "/media", value: "/media" }
                    ]}
                    customLabel="Custom URL base"
                    customPlaceholder="/my-deluno"
                  />
                </Field>
              </div>

              <div className="grid gap-3 sm:grid-cols-2">
                <ToggleField
                  label="Auto-start jobs"
                  checked={formState.autoStartJobs}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, autoStartJobs: checked }))
                  }
                />
                <ToggleField
                  label="Enable notifications"
                  checked={formState.enableNotifications}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, enableNotifications: checked }))
                  }
                />
              </div>

              <Button type="submit" disabled={busy}>
                {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Save general settings
              </Button>
            </form>
          </CardContent>
        </Card>

        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Status</CardTitle>
            <CardDescription>Current persisted general posture.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            <GeneralStat label="Instance" value={settings.appInstanceName} />
            <GeneralStat label="Host" value={`${settings.hostBindAddress}:${settings.hostPort}`} />
            <GeneralStat label="Authentication" value="Required" />
            <GeneralStat label="Updated" value={formatWhen(settings.updatedUtc)} />
          </CardContent>
        </Card>
      </div>
    </SettingsShell>
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

function ToggleField({
  checked,
  label,
  onChange
}: {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="density-field density-control-text flex items-center gap-3 rounded-xl border border-hairline bg-surface-1 text-foreground">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      {label}
    </label>
  );
}

function GeneralStat({ label, value }: { label: string; value: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <p className="density-control-text mt-2 text-foreground">{value}</p>
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
