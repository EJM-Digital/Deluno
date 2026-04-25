import { useState, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { CheckCircle2, KeyRound, LoaderCircle, RefreshCw, SearchCheck } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { PresetField } from "../components/ui/preset-field";
import { SaveStatus, useSaveStatus } from "../components/shell/save-status";
import { toast } from "../components/shell/toaster";
import { settingsOverviewLoader } from "./settings-overview-page";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type LibraryItem,
  type MetadataRefreshJobsResponse,
  type MetadataProviderStatus,
  type MetadataTestResponse,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  metadataStatus: MetadataProviderStatus | null;
  settings: PlatformSettingsSnapshot;
}

export async function settingsMetadataLoader(): Promise<SettingsOverviewLoaderData> {
  const [overview, metadataStatus] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null)
  ]);

  return { ...overview, metadataStatus };
}

export function SettingsMetadataPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  const { libraries, metadataStatus, settings } = loaderData ?? {
    libraries: [],
    metadataStatus: null,
    qualityProfiles: [],
    settings: emptyPlatformSettingsSnapshot
  };
  const revalidator = useRevalidator();
  const [formState, setFormState] = useState(settings);
  const [tmdbApiKey, setTmdbApiKey] = useState("");
  const [isSaving, setIsSaving] = useState(false);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<MetadataTestResponse | null>(null);
  const save = useSaveStatus();

  async function handleSave(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsSaving(true);
    save.markSyncing("Saving…");

    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          ...formState,
          metadataTmdbApiKey: tmdbApiKey.trim() || undefined
        })
      });

      if (!response.ok) {
        throw new Error("Metadata settings could not be saved.");
      }

      save.markSaved();
      toast.success("Metadata settings saved");
      setTmdbApiKey("");
      revalidator.revalidate();
    } catch (error) {
      const msg = error instanceof Error ? error.message : "Metadata settings could not be saved.";
      save.markError(msg);
      toast.error(msg);
    } finally {
      setIsSaving(false);
    }
  }

  async function handleTestProvider() {
    setBusyAction("test");
    try {
      const result = await fetchJson<MetadataTestResponse>("/api/metadata/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ query: "The Matrix", mediaType: "movies", year: 1999 })
      });
      setTestResult(result);
      if (!result.isConfigured) {
        toast.warning(result.message);
      } else {
        toast.success(`TMDb returned ${result.resultCount} result${result.resultCount === 1 ? "" : "s"}.`);
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Metadata provider test failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleQueueRefresh(mediaType: "movies" | "tv" | "all", forceAll: boolean) {
    setBusyAction(`refresh-${mediaType}-${forceAll ? "all" : "missing"}`);
    try {
      const targets = mediaType === "all" ? ["/api/movies/metadata/jobs", "/api/series/metadata/jobs"] : [
        mediaType === "movies" ? "/api/movies/metadata/jobs" : "/api/series/metadata/jobs"
      ];
      const results = await Promise.all(targets.map((path) =>
        fetchJson<MetadataRefreshJobsResponse>(path, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ forceAll, take: 500 })
        })
      ));
      const enqueued = results.reduce((sum, item) => sum + item.enqueuedCount, 0);
      toast.success(`Queued ${enqueued} metadata refresh job${enqueued === 1 ? "" : "s"}.`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Metadata refresh jobs could not be queued.");
    } finally {
      setBusyAction(null);
    }
  }

  return (
    <SettingsShell
      title="Metadata"
      description="Connect the provider Deluno uses for title lookup, posters, backdrops, ratings, genres, external IDs, and future sidecar output."
    >
      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle className="flex items-center justify-between gap-3">
              Provider and output
              <SaveStatus state={save.state} message={save.message} />
            </CardTitle>
            <CardDescription>Use TMDb for live lookup now; use output settings to control how metadata is written beside managed files later.</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="mb-4 grid gap-3 sm:grid-cols-3">
              <SetupStep
                icon={<KeyRound className="h-4 w-4" />}
                title="1. Add key"
                copy={settings.metadataTmdbApiKeyConfigured ? "TMDb is already connected." : "Paste a TMDb API key once for this Deluno install."}
                complete={settings.metadataTmdbApiKeyConfigured}
              />
              <SetupStep
                icon={<SearchCheck className="h-4 w-4" />}
                title="2. Test lookup"
                copy="Run a live lookup so you know title search is ready before adding media."
                complete={Boolean(testResult?.isConfigured && testResult.resultCount > 0)}
              />
              <SetupStep
                icon={<RefreshCw className="h-4 w-4" />}
                title="3. Refresh library"
                copy="Queue missing or full refresh jobs after the provider is connected."
                complete={false}
              />
            </div>
            <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handleSave}>
              <div className="grid gap-3 sm:grid-cols-2">
                <ToggleField
                  label="Write NFO sidecars"
                  checked={formState.metadataNfoEnabled}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, metadataNfoEnabled: checked }))
                  }
                />
                <ToggleField
                  label="Export artwork sidecars"
                  checked={formState.metadataArtworkEnabled}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, metadataArtworkEnabled: checked }))
                  }
                />
              </div>

              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="TMDb API key">
                  <Input
                    value={tmdbApiKey}
                    onChange={(event) => setTmdbApiKey(event.target.value)}
                    type="password"
                    placeholder={settings.metadataTmdbApiKeyConfigured ? "Configured - enter a new key to replace" : "Paste TMDb API key"}
                  />
                  <p className="density-help mt-2 text-muted-foreground">
                    Used for live movie and TV lookup. Leave blank when saving if you want to keep the current key.
                  </p>
                </Field>
                <Field label="Certification country">
                  <PresetField
                    value={formState.metadataCertificationCountry}
                    onChange={(value) =>
                      setFormState((current) => ({
                        ...current,
                        metadataCertificationCountry: value
                      }))
                    }
                    options={[
                      { label: "United States (US)", value: "US" },
                      { label: "United Kingdom (GB)", value: "GB" },
                      { label: "Australia (AU)", value: "AU" },
                      { label: "Canada (CA)", value: "CA" },
                      { label: "New Zealand (NZ)", value: "NZ" },
                      { label: "Germany (DE)", value: "DE" },
                      { label: "France (FR)", value: "FR" },
                      { label: "Japan (JP)", value: "JP" }
                    ]}
                    customLabel="Other country code"
                    customPlaceholder="ISO country code, e.g. NL"
                  />
                </Field>
                <Field label="Metadata language">
                  <PresetField
                    value={formState.metadataLanguage}
                    onChange={(value) =>
                      setFormState((current) => ({
                        ...current,
                        metadataLanguage: value
                      }))
                    }
                    options={[
                      { label: "English (en)", value: "en" },
                      { label: "English - Australia (en-AU)", value: "en-AU" },
                      { label: "English - United Kingdom (en-GB)", value: "en-GB" },
                      { label: "German (de)", value: "de" },
                      { label: "French (fr)", value: "fr" },
                      { label: "Spanish (es)", value: "es" },
                      { label: "Japanese (ja)", value: "ja" }
                    ]}
                    customLabel="Other language code"
                    customPlaceholder="IETF language tag, e.g. pt-BR"
                  />
                </Field>
              </div>

              <div className="grid gap-3 sm:grid-cols-3">
                <StatRow label="Movie folder format" value={formState.movieFolderFormat} />
                <StatRow label="Series folder format" value={formState.seriesFolderFormat} />
                <StatRow label="Episode file format" value={formState.episodeFileFormat} />
              </div>

              <Button type="submit" disabled={isSaving}>
                {isSaving ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Save metadata settings
              </Button>
            </form>
          </CardContent>
        </Card>

        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Current posture</CardTitle>
            <CardDescription>What Deluno will use for lookup, enrichment jobs, and future sidecar generation.</CardDescription>
          </CardHeader>
          <CardContent className="density-help space-y-3 text-muted-foreground">
            <BacklogRow
              title="Provider status"
              copy={
                metadataStatus
                  ? `${metadataStatus.provider.toUpperCase()} is running in ${metadataStatus.mode} mode. ${metadataStatus.message}`
                  : "Metadata provider status is not available yet."
              }
            />
            <BacklogRow
              title="TMDb key"
              copy={settings.metadataTmdbApiKeyConfigured ? "A TMDb API key is stored for this Deluno install." : "No TMDb key is stored yet. Add-title lookup is disabled until a provider key is saved."}
            />
            <BacklogRow
              title="Libraries in scope"
              copy={`${libraries.length} library roots are now available to inherit this metadata behavior.`}
            />
            <BacklogRow
              title="NFO and artwork"
              copy={`${formState.metadataNfoEnabled ? "Enabled" : "Disabled"} NFO output and ${formState.metadataArtworkEnabled ? "enabled" : "disabled"} artwork sidecars are now persisted at the platform level.`}
            />
            <BacklogRow
              title="Locale posture"
              copy={`Certification country ${formState.metadataCertificationCountry || "US"} and language ${formState.metadataLanguage || "en"} are now stored for future scraper/export flows.`}
            />
            <div className="rounded-xl border border-hairline bg-surface-1 p-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <p className="font-medium text-foreground">Provider operations</p>
                  <p className="mt-1">Test TMDb and queue refresh jobs without leaving settings.</p>
                </div>
                <Button type="button" size="sm" variant="outline" onClick={() => void handleTestProvider()} disabled={busyAction !== null}>
                  {busyAction === "test" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <SearchCheck className="h-4 w-4" />}
                  Test
                </Button>
              </div>
              {testResult ? (
                <div className="mt-3 rounded-lg border border-hairline bg-background/40 p-3">
                  <p className="font-mono text-[11px] uppercase text-muted-foreground">
                    {testResult.provider} · {testResult.mode} · {testResult.resultCount} results
                  </p>
                  <p className="mt-1 text-foreground">{testResult.message}</p>
                  {testResult.sampleResults.length ? (
                    <p className="mt-2 text-xs">
                      Sample: {testResult.sampleResults.slice(0, 3).map((item) => `${item.title}${item.year ? ` (${item.year})` : ""}`).join(", ")}
                    </p>
                  ) : null}
                </div>
              ) : null}
              <div className="mt-3 grid gap-2 sm:grid-cols-3">
                <RefreshButton
                  label="Fill missing"
                  busy={busyAction === "refresh-all-missing"}
                  disabled={busyAction !== null}
                  onClick={() => void handleQueueRefresh("all", false)}
                />
                <RefreshButton
                  label="Refresh all movies"
                  busy={busyAction === "refresh-movies-all"}
                  disabled={busyAction !== null}
                  onClick={() => void handleQueueRefresh("movies", true)}
                />
                <RefreshButton
                  label="Refresh all TV"
                  busy={busyAction === "refresh-tv-all"}
                  disabled={busyAction !== null}
                  onClick={() => void handleQueueRefresh("tv", true)}
                />
              </div>
            </div>
          </CardContent>
        </Card>
      </div>
    </SettingsShell>
  );
}

function RefreshButton({
  busy,
  disabled,
  label,
  onClick
}: {
  busy: boolean;
  disabled: boolean;
  label: string;
  onClick: () => void;
}) {
  return (
    <Button type="button" variant="outline" size="sm" className="justify-start" disabled={disabled} onClick={onClick}>
      {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
      {label}
    </Button>
  );
}

function SetupStep({
  complete,
  copy,
  icon,
  title
}: {
  complete: boolean;
  copy: string;
  icon: ReactNode;
  title: string;
}) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <div className="flex items-center gap-2">
        <span className={complete ? "text-success" : "text-muted-foreground"}>
          {complete ? <CheckCircle2 className="h-4 w-4" /> : icon}
        </span>
        <p className="density-control-text font-semibold text-foreground">{title}</p>
      </div>
      <p className="density-help mt-2 text-muted-foreground">{copy}</p>
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

function StatRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <p className="density-control-text mt-2 text-foreground">{value}</p>
    </div>
  );
}

function BacklogRow({ title, copy }: { title: string; copy: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="font-medium text-foreground">{title}</p>
      <p className="mt-1">{copy}</p>
    </div>
  );
}
