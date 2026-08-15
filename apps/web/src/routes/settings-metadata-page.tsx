import { useState, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { CheckCircle2, CircleAlert, LoaderCircle, RefreshCw, SearchCheck } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { InputDescription } from "../components/ui/input-description";
import { PresetField } from "../components/ui/preset-field";
import { SaveStatus, useSaveStatus } from "../components/shell/save-status";
import { toast } from "../components/shell/toaster";
import { settingsOverviewLoader } from "./settings-overview-page";
import {
  fetchJson,
  type LibraryItem,
  type MetadataRefreshJobsResponse,
  type MetadataProviderStatus,
  type MetadataTestResponse,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

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
  if (!loaderData) return <RouteSkeleton />;

  const { libraries, metadataStatus, settings } = loaderData;
  const revalidator = useRevalidator();
  const [formState, setFormState] = useState(settings);
  const [isSaving, setIsSaving] = useState(false);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<MetadataTestResponse | null>(null);
  const save = useSaveStatus();
  const metadataReady = Boolean(metadataStatus?.isConfigured);

  async function handleSave(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsSaving(true);
    save.markSyncing("Saving…");

    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formState)
      });

      if (!response.ok) throw new Error("Metadata settings could not be saved.");

      save.markSaved();
      toast.success("Metadata settings saved");
      revalidator.revalidate();
    } catch (error) {
      const message = error instanceof Error ? error.message : "Metadata settings could not be saved.";
      save.markError(message);
      toast.error(message);
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
        toast.warning("Title matching is not available yet. You can still add media manually.");
      } else {
        toast.success(`Title matching returned ${result.resultCount} result${result.resultCount === 1 ? "" : "s"}.`);
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Title matching check failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleQueueRefresh(mediaType: "movies" | "tv" | "all", forceAll: boolean) {
    setBusyAction(`refresh-${mediaType}-${forceAll ? "all" : "missing"}`);
    try {
      const targets = mediaType === "all"
        ? ["/api/movies/metadata/jobs", "/api/series/metadata/jobs"]
        : [mediaType === "movies" ? "/api/movies/metadata/jobs" : "/api/series/metadata/jobs"];
      const results = await Promise.all(targets.map((path) =>
        fetchJson<MetadataRefreshJobsResponse>(path, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ forceAll, take: 500 })
        })
      ));
      const enqueued = results.reduce((sum, item) => sum + item.enqueuedCount, 0);
      toast.success(`Queued ${enqueued} library-detail refresh job${enqueued === 1 ? "" : "s"}.`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Library-detail refresh jobs could not be queued.");
    } finally {
      setBusyAction(null);
    }
  }

  return (
    <SettingsShell
      title="Metadata & sidecars"
      description="Choose the language, ratings region, artwork, and optional files Deluno keeps with your media."
    >
      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.45fr)_minmax(19rem,0.75fr)]">
        <Card>
          <CardHeader>
            <CardTitle className="flex flex-wrap items-center justify-between gap-3">
              What Deluno saves
              <SaveStatus state={save.state} message={save.message} />
            </CardTitle>
            <CardDescription>
              Deluno manages title matching, posters, descriptions, and ratings in the background. There are no provider keys to set here.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[var(--grid-gap)]" onSubmit={handleSave}>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="Ratings region" description="Use this country when Deluno displays certification ratings and applies rating filters.">
                  <PresetField
                    value={formState.metadataCertificationCountry}
                    onChange={(value) => setFormState((current) => ({ ...current, metadataCertificationCountry: value }))}
                    options={[
                      { label: "Australia (AU)", value: "AU" },
                      { label: "United States (US)", value: "US" },
                      { label: "United Kingdom (GB)", value: "GB" },
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
                <Field label="Details language" description="Deluno will prefer this language for title names, descriptions, and release information.">
                  <PresetField
                    value={formState.metadataLanguage}
                    onChange={(value) => setFormState((current) => ({ ...current, metadataLanguage: value }))}
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

              <div>
                <p className="mb-3 text-sm font-semibold text-foreground">Files beside your media</p>
                <div className="grid gap-3 sm:grid-cols-2">
                  <ToggleField
                    label="Save NFO files"
                    description="Keep a portable .nfo record in each media folder for compatible media players and tools."
                    checked={formState.metadataNfoEnabled}
                    onChange={(checked) => setFormState((current) => ({ ...current, metadataNfoEnabled: checked }))}
                  />
                  <ToggleField
                    label="Save poster and backdrop files"
                    description="Keep artwork next to your media so it remains available outside Deluno."
                    checked={formState.metadataArtworkEnabled}
                    onChange={(checked) => setFormState((current) => ({ ...current, metadataArtworkEnabled: checked }))}
                  />
                </div>
              </div>

              <Button type="submit" disabled={isSaving}>
                {isSaving ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Save metadata settings
              </Button>
            </form>
          </CardContent>
        </Card>

        <div className="space-y-[var(--grid-gap)]">
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                {metadataReady ? <CheckCircle2 className="h-5 w-5 text-success" /> : <CircleAlert className="h-5 w-5 text-warning" />}
                Title matching
              </CardTitle>
              <CardDescription>
                {metadataReady
                  ? "Ready. Deluno can match titles and collect their library details."
                  : "Unavailable. You can still add a movie or show manually while this Deluno installation is being checked."}
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              <p className="text-sm leading-relaxed text-muted-foreground">
                Deluno manages title matching for this installation. Provider credentials are never needed when setting up your library.
              </p>
              <Button type="button" variant="outline" size="sm" onClick={() => void handleTestProvider()} disabled={busyAction !== null}>
                {busyAction === "test" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <SearchCheck className="h-4 w-4" />}
                Check title matching
              </Button>
              {testResult ? (
                <div className="rounded-xl border border-hairline bg-surface-1 p-3 text-sm">
                  <p className="font-medium text-foreground">
                    {testResult.isConfigured
                      ? `${testResult.resultCount} match${testResult.resultCount === 1 ? "" : "es"} found for The Matrix`
                      : "Title matching is not ready"}
                  </p>
                  <p className="mt-1 text-muted-foreground">
                    {testResult.isConfigured
                      ? testResult.message
                      : "You can add media manually while the Deluno server's metadata connection is restored."}
                  </p>
                </div>
              ) : null}
            </CardContent>
          </Card>

        </div>
      </div>

      {libraries.length > 0 ? (
        <details className="rounded-2xl border border-hairline bg-card p-4">
          <summary className="cursor-pointer font-semibold text-foreground">Maintenance: refresh library details</summary>
          <p className="mt-2 max-w-3xl text-sm leading-relaxed text-muted-foreground">
            Use this only after changing your language or artwork preferences, or when you want Deluno to fill missing details for titles already in your library.
          </p>
          <div className="mt-4 grid gap-2 sm:grid-cols-3">
            <RefreshButton label="Fill missing details" busy={busyAction === "refresh-all-missing"} disabled={busyAction !== null} onClick={() => void handleQueueRefresh("all", false)} />
            <RefreshButton label="Refresh all movies" busy={busyAction === "refresh-movies-all"} disabled={busyAction !== null} onClick={() => void handleQueueRefresh("movies", true)} />
            <RefreshButton label="Refresh all TV" busy={busyAction === "refresh-tv-all"} disabled={busyAction !== null} onClick={() => void handleQueueRefresh("tv", true)} />
          </div>
        </details>
      ) : null}
    </SettingsShell>
  );
}

function RefreshButton({ busy, disabled, label, onClick }: { busy: boolean; disabled: boolean; label: string; onClick: () => void }) {
  return (
    <Button type="button" variant="outline" size="sm" className="justify-start" disabled={disabled} onClick={onClick}>
      {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
      {label}
    </Button>
  );
}

function Field({ children, description, label }: { children: ReactNode; description: string; label: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
      <InputDescription>{description}</InputDescription>
    </div>
  );
}

function ToggleField({ checked, description, label, onChange }: { checked: boolean; description: string; label: string; onChange: (checked: boolean) => void }) {
  return (
    <label className="density-field density-control-text flex items-start gap-3 rounded-xl border border-hairline bg-surface-1 text-foreground">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} className="mt-1 flex-shrink-0" />
      <span className="flex-1">
        <span className="block font-semibold">{label}</span>
        <InputDescription>{description}</InputDescription>
      </span>
    </label>
  );
}
