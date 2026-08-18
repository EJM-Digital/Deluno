/**
 * Metadata & sidecars — a page-level form on the shared grammar.
 *
 *   PageToolbar (Library setup tabs)
 *   ListCard  what Deluno saves   (page form: language, region, sidecar files)
 *   ListCard  title matching      (status row · check runs and reports in place)
 *   ListCard  refresh jobs        (one row per maintenance command)
 *   PageFooter (pinned: status · Discard · Save)
 *
 * Contracts: PUT /api/settings, POST /api/metadata/test,
 * POST /api/movies/metadata/jobs, POST /api/series/metadata/jobs.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Loader2, RefreshCw, SearchCheck } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { Field, FieldRow } from "../components/ui/field";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { SwitchRow } from "../components/ui/switch";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import type { DrawerSaveState } from "../components/ui/drawer";
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

const COUNTRY_OPTIONS = [
  { label: "Australia (AU)", value: "AU" },
  { label: "United States (US)", value: "US" },
  { label: "United Kingdom (GB)", value: "GB" },
  { label: "Canada (CA)", value: "CA" },
  { label: "New Zealand (NZ)", value: "NZ" },
  { label: "Germany (DE)", value: "DE" },
  { label: "France (FR)", value: "FR" },
  { label: "Japan (JP)", value: "JP" }
];

const LANGUAGE_OPTIONS = [
  { label: "English (en)", value: "en" },
  { label: "English — Australia (en-AU)", value: "en-AU" },
  { label: "English — United Kingdom (en-GB)", value: "en-GB" },
  { label: "German (de)", value: "de" },
  { label: "French (fr)", value: "fr" },
  { label: "Spanish (es)", value: "es" },
  { label: "Japanese (ja)", value: "ja" }
];

const REFRESH_JOBS = [
  {
    key: "missing",
    name: "Fill in missing details",
    sub: "Movies and TV",
    description: "Only touches titles that are missing artwork, a description or a rating.",
    mediaType: "all" as const,
    forceAll: false
  },
  {
    key: "movies",
    name: "Refresh every movie",
    sub: "Movies",
    description: "Re-fetches details for the whole movie library. Use after changing language or region.",
    mediaType: "movies" as const,
    forceAll: true
  },
  {
    key: "tv",
    name: "Refresh every show",
    sub: "TV shows",
    description: "Re-fetches details for the whole TV library. Use after changing language or region.",
    mediaType: "tv" as const,
    forceAll: true
  }
];

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  metadataStatus: MetadataProviderStatus | null;
  settings: PlatformSettingsSnapshot;
}

export async function settingsMetadataLoader(): Promise<LoaderData> {
  const [overview, metadataStatus] = await Promise.all([
    import("./settings-overview-page").then((module) => module.settingsOverviewLoader()),
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null)
  ]);
  return { ...overview, metadataStatus };
}

interface MetadataForm {
  certificationCountry: string;
  language: string;
  nfoEnabled: boolean;
  artworkEnabled: boolean;
}

export function SettingsMetadataPage() {
  const { libraries, metadataStatus, settings } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();

  const [savedForm, setSavedForm] = useState<MetadataForm>(() => formFrom(settings));
  const [form, setForm] = useState<MetadataForm>(savedForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<MetadataTestResponse | null>(null);
  const [jobResult, setJobResult] = useState<Record<string, string>>({});

  const dirty = !same(form, savedForm);
  const settingsForm = useMemo(() => formFrom(settings), [settings]);
  useEffect(() => {
    if (dirty || same(savedForm, settingsForm)) return;
    setSavedForm(settingsForm);
    setForm(settingsForm);
  }, [dirty, savedForm, settingsForm]);

  const state: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saveState === "saving") return;
    setSaveState("saving");
    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          ...settings,
          metadataCertificationCountry: form.certificationCountry,
          metadataLanguage: form.language,
          metadataNfoEnabled: form.nfoEnabled,
          metadataArtworkEnabled: form.artworkEnabled
        })
      });
      if (!response.ok) throw new Error("Metadata settings could not be saved.");
      setSavedForm(form);
      setSaveState("saved");
      setMessage("Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  /** The result lands in the row that started it — a check is not an outcome elsewhere. */
  async function checkTitleMatching() {
    setBusy("test");
    setTestResult(null);
    try {
      setTestResult(
        await fetchJson<MetadataTestResponse>("/api/metadata/test", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ query: "The Matrix", mediaType: "movies", year: 1999 })
        })
      );
    } catch (error) {
      setTestResult({ isConfigured: false, resultCount: 0, message: error instanceof Error ? error.message : "The check could not be run." } as MetadataTestResponse);
    } finally {
      setBusy(null);
    }
  }

  async function queueRefresh(job: (typeof REFRESH_JOBS)[number]) {
    setBusy(`job:${job.key}`);
    try {
      const targets =
        job.mediaType === "all"
          ? ["/api/movies/metadata/jobs", "/api/series/metadata/jobs"]
          : [job.mediaType === "movies" ? "/api/movies/metadata/jobs" : "/api/series/metadata/jobs"];
      const results = await Promise.all(
        targets.map((path) =>
          fetchJson<MetadataRefreshJobsResponse>(path, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ forceAll: job.forceAll, take: 500 })
          })
        )
      );
      const enqueued = results.reduce((total, item) => total + item.enqueuedCount, 0);
      setJobResult((current) => ({ ...current, [job.key]: enqueued ? `Queued ${enqueued} ${enqueued === 1 ? "title" : "titles"}` : "Nothing needed refreshing" }));
      revalidator.revalidate();
    } catch (error) {
      setJobResult((current) => ({ ...current, [job.key]: error instanceof Error ? error.message : "Could not queue" }));
    } finally {
      setBusy(null);
    }
  }

  const ready = Boolean(metadataStatus?.isConfigured);

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={librarySetupNavItems} />

      <ListCard title="What Deluno saves" count="Language, region, and the files kept beside your media">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <FieldRow>
            <Field label="Details language" help="Preferred language for titles, descriptions, and release information.">
              <PresetField
                value={form.language}
                onChange={(value) => setForm((current) => ({ ...current, language: value }))}
                options={LANGUAGE_OPTIONS}
                customLabel="Other language code"
                customPlaceholder="IETF language tag, e.g. pt-BR"
              />
            </Field>
            <Field label="Ratings region" help="Country used for certification ratings and rating filters.">
              <PresetField
                value={form.certificationCountry}
                onChange={(value) => setForm((current) => ({ ...current, certificationCountry: value }))}
                options={COUNTRY_OPTIONS}
                customLabel="Other country code"
                customPlaceholder="ISO country code, e.g. NL"
              />
            </Field>
          </FieldRow>
          <SwitchRow
            label="Save NFO files"
            description="Keep a portable .nfo record in each media folder for other players and tools."
            checked={form.nfoEnabled}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, nfoEnabled: checked }))}
          />
          <SwitchRow
            label="Save poster and backdrop files"
            description="Keep artwork next to your media so it stays available outside Deluno."
            checked={form.artworkEnabled}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, artworkEnabled: checked }))}
          />
        </div>
      </ListCard>

      <ListCard
        title="Title matching"
        count="Deluno runs this for you — there are no provider keys to set"
        actions={
          <Button type="button" variant="outline" size="sm" onClick={() => void checkTitleMatching()} disabled={busy !== null}>
            {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <SearchCheck className="h-3.5 w-3.5" />}
            Check now
          </Button>
        }
      >
        <ListTable columns={[{ label: "Service" }, { label: "Last check", width: "minmax(0,1.6fr)" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]} chevron={false}>
          <ListRow>
            <ListNameCell name="Matching and library details" sub="Posters, descriptions, ratings" />
            <ListCell
              primary={testResult ? (testResult.isConfigured ? `${testResult.resultCount} ${testResult.resultCount === 1 ? "match" : "matches"} for “The Matrix”` : "The check could not reach the service") : "Not checked this session"}
              secondary={testResult?.message ?? (ready ? "Deluno can match titles and collect their details." : "You can still add a movie or show by hand.")}
            />
            <ListCell mobile>
              <Chip tone={testResult ? (testResult.isConfigured ? "ok" : "bad") : ready ? "ok" : "warn"}>
                {testResult ? (testResult.isConfigured ? "Working" : "Failed") : ready ? "Ready" : "Unavailable"}
              </Chip>
            </ListCell>
          </ListRow>
        </ListTable>
      </ListCard>

      {libraries.length > 0 ? (
        <ListCard title="Refresh library details" count="Run one of these after changing the language or region above">
          <ListTable columns={[{ label: "Job" }, { label: "What it does", width: "minmax(0,1.8fr)" }, { label: "Run", width: "120px", mobile: true, srOnly: true }]} chevron={false}>
            {REFRESH_JOBS.map((job) => (
              <ListRow key={job.key}>
                <ListNameCell name={job.name} sub={job.sub} />
                <ListCell primary={job.description} secondary={jobResult[job.key]} />
                <ListCell mobile align="end">
                  <Button type="button" variant="outline" size="sm" onClick={() => void queueRefresh(job)} disabled={busy !== null}>
                    {busy === `job:${job.key}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
                    Run
                  </Button>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      <PageFooter state={state} message={message} saveLabel="Save metadata settings" onDiscard={() => setForm(savedForm)} />
    </form>
  );
}

/* ---------------------------------------------------------------- bits */

function same<T>(a: T, b: T) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function formFrom(settings: PlatformSettingsSnapshot): MetadataForm {
  return {
    certificationCountry: settings.metadataCertificationCountry,
    language: settings.metadataLanguage,
    nfoEnabled: settings.metadataNfoEnabled,
    artworkEnabled: settings.metadataArtworkEnabled
  };
}
