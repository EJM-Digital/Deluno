import { useMemo, useState } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { FolderCog, HardDrive, LoaderCircle, Settings2, SlidersHorizontal } from "lucide-react";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  readValidationProblem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { KpiCard } from "../components/app/kpi-card";
import { SettingsShell } from "../components/app/settings-shell";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { PathInput } from "../components/ui/path-input";
import { PresetField } from "../components/ui/preset-field";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

interface LibraryFormState {
  name: string;
  mediaType: string;
  purpose: string;
  rootPath: string;
  downloadsPath: string;
  qualityProfileId: string;
  autoSearchEnabled: boolean;
  missingSearchEnabled: boolean;
  upgradeSearchEnabled: boolean;
  searchIntervalHours: number;
  retryDelayHours: number;
  maxItemsPerRun: number;
}

interface QualityProfileFormState {
  name: string;
  mediaType: string;
  cutoffQuality: string;
  allowedQualities: string;
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
}

const INTERVAL_OPTIONS = [
  { label: "Off / manual only", value: "0" },
  { label: "Every hour", value: "1" },
  { label: "Every 3 hours", value: "3" },
  { label: "Every 6 hours", value: "6" },
  { label: "Every 12 hours", value: "12" },
  { label: "Daily", value: "24" }
];

const RETRY_OPTIONS = [
  { label: "No delay", value: "0" },
  { label: "1 hour", value: "1" },
  { label: "3 hours", value: "3" },
  { label: "6 hours", value: "6" },
  { label: "12 hours", value: "12" },
  { label: "Daily", value: "24" }
];

const MAX_PER_RUN_OPTIONS = [
  { label: "Conservative (5)", value: "5" },
  { label: "Balanced (10)", value: "10" },
  { label: "Heavy (25)", value: "25" },
  { label: "Aggressive (50)", value: "50" }
];

const QUALITY_OPTIONS = [
  { label: "WEB-DL 2160p", value: "WEB-DL 2160p" },
  { label: "Bluray-2160p", value: "Bluray-2160p" },
  { label: "WEB-DL 1080p", value: "WEB-DL 1080p" },
  { label: "Bluray-1080p", value: "Bluray-1080p" },
  { label: "WEB-DL 720p", value: "WEB-DL 720p" },
  { label: "HDTV-720p", value: "HDTV-720p" }
];

export async function settingsOverviewLoader(): Promise<SettingsOverviewLoaderData> {
  const [settings, libraries, qualityProfiles] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles")
  ]);

  return { libraries, qualityProfiles, settings };
}

export function SettingsOverviewPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { libraries, qualityProfiles, settings } = loaderData;
  const revalidator = useRevalidator();
  const [formState, setFormState] = useState(settings);
  const [libraryForm, setLibraryForm] = useState<LibraryFormState>(() =>
    createEmptyLibraryForm(qualityProfiles[0]?.id ?? "")
  );
  const [profileForm, setProfileForm] = useState<QualityProfileFormState>(createEmptyQualityProfileForm);
  const [isSaving, setIsSaving] = useState(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);

  const autoLibraries = libraries.filter((library) => library.autoSearchEnabled).length;
  const movieLibraries = libraries.filter((library) => library.mediaType === "movies").length;
  const tvLibraries = libraries.filter((library) => library.mediaType === "tv").length;
  const profileOptions = useMemo(
    () =>
      qualityProfiles.map((profile) => ({
        label: `${profile.name} (${profile.mediaType})`,
        value: profile.id
      })),
    [qualityProfiles]
  );

  async function handleSaveSettings(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsSaving(true);
    setActionMessage(null);

    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formState)
      });

      if (!response.ok) {
        const problem = await readValidationProblem(response);
        throw new Error(problem?.title ?? "settings-save-failed");
      }

      setActionMessage("Settings saved.");
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Settings could not be saved.");
    } finally {
      setIsSaving(false);
    }
  }

  async function handleLibraryAction(libraryId: string, intent: "search-now" | "import-existing") {
    setBusyKey(`${intent}:${libraryId}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(
        `/api/libraries/${libraryId}/${intent === "search-now" ? "search-now" : "import-existing"}`,
        { method: "POST" }
      );

      if (!response.ok) {
        throw new Error("library-action-failed");
      }

      setActionMessage(
        intent === "search-now" ? "Library search queued." : "Existing library import started."
      );
      revalidator.revalidate();
    } catch {
      setActionMessage(
        intent === "search-now"
          ? "Library search could not be queued."
          : "Library import could not be started."
      );
    } finally {
      setBusyKey(null);
    }
  }

  async function handleCreateLibrary(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusyKey("create-library");
    setActionMessage(null);

    try {
      const response = await authedFetch("/api/libraries", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(libraryForm)
      });

      if (!response.ok) {
        const problem = await readValidationProblem(response);
        throw new Error(problem?.title ?? "Library could not be created.");
      }

      setLibraryForm(createEmptyLibraryForm(qualityProfiles[0]?.id ?? ""));
      setActionMessage("Library created.");
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Library could not be created.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleCreateProfile(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusyKey("create-profile");
    setActionMessage(null);

    try {
      const response = await authedFetch("/api/quality-profiles", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(profileForm)
      });

      if (!response.ok) {
        const problem = await readValidationProblem(response);
        throw new Error(problem?.title ?? "Profile could not be created.");
      }

      setProfileForm(createEmptyQualityProfileForm());
      setActionMessage("Quality profile created.");
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Profile could not be created.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleUpdateLibraryAutomation(
    library: LibraryItem,
    patch: Partial<Pick<LibraryItem, "autoSearchEnabled" | "missingSearchEnabled" | "upgradeSearchEnabled" | "searchIntervalHours" | "retryDelayHours" | "maxItemsPerRun">>
  ) {
    setBusyKey(`automation:${library.id}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/libraries/${library.id}/automation`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          autoSearchEnabled: patch.autoSearchEnabled ?? library.autoSearchEnabled,
          missingSearchEnabled: patch.missingSearchEnabled ?? library.missingSearchEnabled,
          upgradeSearchEnabled: patch.upgradeSearchEnabled ?? library.upgradeSearchEnabled,
          searchIntervalHours: patch.searchIntervalHours ?? library.searchIntervalHours,
          retryDelayHours: patch.retryDelayHours ?? library.retryDelayHours,
          maxItemsPerRun: patch.maxItemsPerRun ?? library.maxItemsPerRun
        })
      });

      if (!response.ok) {
        throw new Error("Library automation could not be updated.");
      }

      setActionMessage(`Automation updated for ${library.name}.`);
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Library automation could not be updated.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleUpdateLibraryProfile(libraryId: string, qualityProfileId: string) {
    setBusyKey(`profile:${libraryId}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/libraries/${libraryId}/quality-profile`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ qualityProfileId })
      });

      if (!response.ok) {
        throw new Error("Library profile could not be updated.");
      }

      setActionMessage("Library quality profile updated.");
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Library profile could not be updated.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDeleteLibrary(libraryId: string) {
    setBusyKey(`delete:${libraryId}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/libraries/${libraryId}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) {
        throw new Error("Library could not be removed.");
      }
      setActionMessage("Library removed.");
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Library could not be removed.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title="Settings overview"
      description="Deluno identity, libraries, automation, and quality policy in one workspace while the deeper settings areas expand into their own routes."
    >
      <Card className="settings-panel border-primary/25 bg-primary/5">
        <CardHeader>
          <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <div>
              <CardTitle>Beginner setup or advanced control</CardTitle>
              <CardDescription>
                Use guided setup when you want Deluno to create the sensible baseline. Use the sections below when you want to tune the generated profile, routing, formats, and automation yourself.
              </CardDescription>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button asChild>
                <Link to="/setup-guide">Open guided setup</Link>
              </Button>
              <Button variant="secondary" asChild>
                <Link to="/settings/profiles">Tune advanced quality</Link>
              </Button>
            </div>
          </div>
        </CardHeader>
      </Card>

      <div className="fluid-kpi-grid">
        <KpiCard
          label="Libraries"
          value={String(libraries.length)}
          icon={FolderCog}
          meta="Movie and TV library containers Deluno is managing."
          sparkline={[4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5]}
        />
        <KpiCard
          label="Profiles"
          value={String(qualityProfiles.length)}
          icon={SlidersHorizontal}
          meta="Quality profiles available across Movies and TV."
          sparkline={[2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4]}
        />
        <KpiCard
          label="Auto libraries"
          value={String(autoLibraries)}
          icon={Settings2}
          meta="Libraries actively running on Deluno automation."
          sparkline={[2, 2, 2, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4]}
        />
        <KpiCard
          label="Storage roots"
          value={String([settings.movieRootPath, settings.seriesRootPath].filter(Boolean).length)}
          icon={HardDrive}
          meta="Configured media roots across movies and television."
          sparkline={[1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2]}
        />
      </div>

      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Control plane</CardTitle>
            <CardDescription>
              Settings are grouped by decision, not by a long flat list of technical pages.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid gap-3 md:grid-cols-2">
            <OverviewLinkCard
              to="/settings/media-management"
              title="Library"
              description="Roots, naming, metadata, and tags that decide where media lands and how it is organised."
            />
            <OverviewLinkCard
              to="/settings/destination-rules"
              title="Destination rules"
              description="Route movies and shows into different roots based on genre, tag, language, quality, or library intent."
            />
            <OverviewLinkCard
              to="/settings/profiles"
              title="Quality"
              description="Profiles, size boundaries, and custom formats that determine what Deluno prefers."
            />
            <OverviewLinkCard
              to="/settings/policy-sets"
              title="Policy sets"
              description="Combine quality profiles and destination rules into reusable single-install acquisition policies."
            />
            <OverviewLinkCard
              to="/settings/lists"
              title="Automation"
              description="List sources and recurring behaviours that decide what enters Deluno automatically."
            />
            <OverviewLinkCard
              to="/settings/general"
              title="System"
              description="Instance identity, runtime posture, notifications, and interface behaviour."
            />
          </CardContent>
        </Card>

        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Single-install direction</CardTitle>
            <CardDescription>
              Deluno should replace multiple Arr instances with policy-driven routing inside one install.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-3 text-sm text-muted-foreground">
            <p>
              The next settings depth should support destination rules and policy sets, so users can separate
              4K, anime, kids, language, or genre libraries without running extra instances.
            </p>
            <div className="flex flex-wrap gap-2">
              <Badge variant="info">Destination rules</Badge>
              <Badge variant="info">Policy sets</Badge>
              <Badge variant="info">Multi-version targets</Badge>
            </div>
          </CardContent>
        </Card>
      </div>

      {actionMessage ? (
        <div className="rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-sm text-muted-foreground">
          {actionMessage}
        </div>
      ) : null}

      <div className="settings-split settings-split-config-heavy">
        <div className="settings-side-stack">
          <Card>
            <CardHeader>
              <CardTitle>Platform</CardTitle>
              <CardDescription>
                Core runtime identity and storage paths for this Deluno instance.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <form className="grid gap-[var(--grid-gap)] sm:grid-cols-2" onSubmit={handleSaveSettings}>
                <Field label="Instance">
                  <Input
                    value={formState.appInstanceName}
                    onChange={(event) =>
                      setFormState((current) => ({ ...current, appInstanceName: event.target.value }))
                    }
                  />
                </Field>
                <SettingsStat label="Updated" value={formatWhen(settings.updatedUtc)} />
                <Field label="Movies root">
                  <PathInput
                    value={formState.movieRootPath ?? ""}
                    onChange={(nextValue) =>
                      setFormState((current) => ({ ...current, movieRootPath: nextValue }))
                    }
                    browseTitle="Choose movies root"
                  />
                </Field>
                <Field label="TV root">
                  <PathInput
                    value={formState.seriesRootPath ?? ""}
                    onChange={(nextValue) =>
                      setFormState((current) => ({ ...current, seriesRootPath: nextValue }))
                    }
                    browseTitle="Choose TV root"
                  />
                </Field>
                <Field label="Downloads">
                  <PathInput
                    value={formState.downloadsPath ?? ""}
                    onChange={(nextValue) =>
                      setFormState((current) => ({ ...current, downloadsPath: nextValue }))
                    }
                    browseTitle="Choose downloads folder"
                  />
                </Field>
                <Field label="Incomplete">
                  <PathInput
                    value={formState.incompleteDownloadsPath ?? ""}
                    onChange={(nextValue) =>
                      setFormState((current) => ({
                        ...current,
                        incompleteDownloadsPath: nextValue
                      }))
                    }
                    browseTitle="Choose incomplete downloads folder"
                  />
                </Field>
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
                <div className="sm:col-span-2">
                  <Button type="submit" disabled={isSaving}>
                    {isSaving ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                    Save settings
                  </Button>
                </div>
              </form>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Create library</CardTitle>
              <CardDescription>Add a new movie or TV container to Deluno.</CardDescription>
            </CardHeader>
            <CardContent>
              <form className="grid gap-[var(--grid-gap)] sm:grid-cols-2" onSubmit={handleCreateLibrary}>
                <Field label="Name">
                  <Input value={libraryForm.name} onChange={(event) => setLibraryForm((current) => ({ ...current, name: event.target.value }))} />
                </Field>
                <Field label="Media type">
                  <select
                    value={libraryForm.mediaType}
                    onChange={(event) => setLibraryForm((current) => ({ ...current, mediaType: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="movies">Movies</option>
                    <option value="tv">TV</option>
                  </select>
                </Field>
                <Field label="Purpose">
                  <PresetField
                    value={libraryForm.purpose}
                    onChange={(value) => setLibraryForm((current) => ({ ...current, purpose: value }))}
                    options={[
                      { label: "General library", value: "General library" },
                      { label: "4K / UHD library", value: "4K library" },
                      { label: "Kids and family", value: "Kids and family" },
                      { label: "Anime", value: "Anime" },
                      { label: "Documentaries", value: "Documentaries" },
                      { label: "Archive", value: "Archive" }
                    ]}
                    customLabel="Custom purpose"
                    customPlaceholder="Describe this library"
                  />
                </Field>
                <Field label="Quality profile">
                  <select
                    value={libraryForm.qualityProfileId}
                    onChange={(event) => setLibraryForm((current) => ({ ...current, qualityProfileId: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="">No profile</option>
                    {profileOptions.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Root path">
                  <PathInput
                    value={libraryForm.rootPath}
                    onChange={(nextValue) => setLibraryForm((current) => ({ ...current, rootPath: nextValue }))}
                    browseTitle="Choose library root"
                  />
                </Field>
                <Field label="Downloads path">
                  <PathInput
                    value={libraryForm.downloadsPath}
                    onChange={(nextValue) => setLibraryForm((current) => ({ ...current, downloadsPath: nextValue }))}
                    browseTitle="Choose library downloads folder"
                  />
                </Field>
                <Field label="Search interval (hours)">
                  <PresetField inputType="number" value={String(libraryForm.searchIntervalHours)} onChange={(value) => setLibraryForm((current) => ({ ...current, searchIntervalHours: Number(value || 0) }))} options={INTERVAL_OPTIONS} customLabel="Custom interval" customPlaceholder="Hours" />
                </Field>
                <Field label="Retry delay (hours)">
                  <PresetField inputType="number" value={String(libraryForm.retryDelayHours)} onChange={(value) => setLibraryForm((current) => ({ ...current, retryDelayHours: Number(value || 0) }))} options={RETRY_OPTIONS} customLabel="Custom retry delay" customPlaceholder="Hours" />
                </Field>
                <Field label="Max items per run">
                  <PresetField inputType="number" value={String(libraryForm.maxItemsPerRun)} onChange={(value) => setLibraryForm((current) => ({ ...current, maxItemsPerRun: Number(value || 10) }))} options={MAX_PER_RUN_OPTIONS} customLabel="Custom max" customPlaceholder="Items per run" />
                </Field>
                <div className="grid gap-3 sm:col-span-2 sm:grid-cols-3">
                  <ToggleField label="Auto search" checked={libraryForm.autoSearchEnabled} onChange={(checked) => setLibraryForm((current) => ({ ...current, autoSearchEnabled: checked }))} />
                  <ToggleField label="Missing search" checked={libraryForm.missingSearchEnabled} onChange={(checked) => setLibraryForm((current) => ({ ...current, missingSearchEnabled: checked }))} />
                  <ToggleField label="Upgrade search" checked={libraryForm.upgradeSearchEnabled} onChange={(checked) => setLibraryForm((current) => ({ ...current, upgradeSearchEnabled: checked }))} />
                </div>
                <div className="sm:col-span-2">
                  <Button type="submit" disabled={busyKey === "create-library"}>
                    {busyKey === "create-library" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                    Create library
                  </Button>
                </div>
              </form>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Libraries</CardTitle>
              <CardDescription>
                Current library containers, automation posture, and live operations.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              {libraries.map((library) => (
                <div key={library.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <p className="font-display text-base font-semibold text-foreground">{library.name}</p>
                      <p className="text-sm text-muted-foreground">{library.purpose}</p>
                    </div>
                    <Badge variant={library.autoSearchEnabled ? "success" : "default"}>
                      {library.mediaType === "tv" ? "TV" : "Movies"}
                    </Badge>
                  </div>
                  <div className="fluid-field-grid mt-3">
                    <Field label="Profile">
                      <select
                        value={library.qualityProfileId ?? ""}
                        onChange={(event) => void handleUpdateLibraryProfile(library.id, event.target.value)}
                        className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                      >
                        <option value="">No profile</option>
                        {profileOptions.map((option) => (
                          <option key={option.value} value={option.value}>
                            {option.label}
                          </option>
                        ))}
                      </select>
                    </Field>
                    <Field label="Interval (h)">
                      <PresetField
                        inputType="number"
                        value={String(library.searchIntervalHours)}
                        onChange={(value) => void handleUpdateLibraryAutomation(library, { searchIntervalHours: Number(value || 0) })}
                        options={INTERVAL_OPTIONS}
                        customLabel="Custom interval"
                        customPlaceholder="Hours"
                      />
                    </Field>
                    <Field label="Retry (h)">
                      <PresetField
                        inputType="number"
                        value={String(library.retryDelayHours)}
                        onChange={(value) => void handleUpdateLibraryAutomation(library, { retryDelayHours: Number(value || 0) })}
                        options={RETRY_OPTIONS}
                        customLabel="Custom retry delay"
                        customPlaceholder="Hours"
                      />
                    </Field>
                    <Field label="Max per run">
                      <PresetField
                        inputType="number"
                        value={String(library.maxItemsPerRun)}
                        onChange={(value) => void handleUpdateLibraryAutomation(library, { maxItemsPerRun: Number(value || 10) })}
                        options={MAX_PER_RUN_OPTIONS}
                        customLabel="Custom max"
                        customPlaceholder="Items per run"
                      />
                    </Field>
                  </div>
                  <div className="mt-3 grid gap-3 md:grid-cols-3">
                    <ToggleField label="Automation" checked={library.autoSearchEnabled} onChange={(checked) => void handleUpdateLibraryAutomation(library, { autoSearchEnabled: checked })} />
                    <ToggleField label="Missing runs" checked={library.missingSearchEnabled} onChange={(checked) => void handleUpdateLibraryAutomation(library, { missingSearchEnabled: checked })} />
                    <ToggleField label="Upgrade runs" checked={library.upgradeSearchEnabled} onChange={(checked) => void handleUpdateLibraryAutomation(library, { upgradeSearchEnabled: checked })} />
                  </div>
                  <div className="mt-4 flex flex-wrap gap-2">
                    <Button
                      size="sm"
                      onClick={() => void handleLibraryAction(library.id, "search-now")}
                      disabled={busyKey === `search-now:${library.id}`}
                    >
                      {busyKey === `search-now:${library.id}` ? (
                        <LoaderCircle className="h-4 w-4 animate-spin" />
                      ) : null}
                      Search now
                    </Button>
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => void handleLibraryAction(library.id, "import-existing")}
                      disabled={busyKey === `import-existing:${library.id}`}
                    >
                      {busyKey === `import-existing:${library.id}` ? (
                        <LoaderCircle className="h-4 w-4 animate-spin" />
                      ) : null}
                      Import existing
                    </Button>
                    <Button
                      size="sm"
                      variant="ghost"
                      onClick={() => void handleDeleteLibrary(library.id)}
                      disabled={busyKey === `delete:${library.id}`}
                    >
                      {busyKey === `delete:${library.id}` ? (
                        <LoaderCircle className="h-4 w-4 animate-spin" />
                      ) : null}
                      Remove library
                    </Button>
                  </div>
                </div>
              ))}
            </CardContent>
          </Card>
        </div>

        <div className="settings-side-stack">
          <Card>
            <CardHeader>
              <CardTitle>Create quality profile</CardTitle>
              <CardDescription>Define cutoff and allowed quality policy.</CardDescription>
            </CardHeader>
            <CardContent>
              <form className="grid gap-[var(--grid-gap)]" onSubmit={handleCreateProfile}>
                <Field label="Name">
                  <Input value={profileForm.name} onChange={(event) => setProfileForm((current) => ({ ...current, name: event.target.value }))} />
                </Field>
                <Field label="Media type">
                  <select
                    value={profileForm.mediaType}
                    onChange={(event) => setProfileForm((current) => ({ ...current, mediaType: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="movies">Movies</option>
                    <option value="tv">TV</option>
                  </select>
                </Field>
                <Field label="Cutoff quality">
                  <PresetField value={profileForm.cutoffQuality} onChange={(value) => setProfileForm((current) => ({ ...current, cutoffQuality: value }))} options={QUALITY_OPTIONS} customLabel="Custom quality" customPlaceholder="Quality name" />
                </Field>
                <Field label="Allowed qualities">
                  <PresetField
                    value={profileForm.allowedQualities}
                    onChange={(value) => setProfileForm((current) => ({ ...current, allowedQualities: value }))}
                    options={[
                      { label: "All common HD/UHD qualities", value: QUALITY_OPTIONS.map((quality) => quality.value).join(",") },
                      { label: "4K only", value: "WEB-DL 2160p,Bluray-2160p" },
                      { label: "1080p only", value: "WEB-DL 1080p,Bluray-1080p" },
                      { label: "720p and 1080p", value: "WEB-DL 720p,HDTV-720p,WEB-DL 1080p,Bluray-1080p" }
                    ]}
                    customLabel="Custom quality list"
                    customPlaceholder="Comma-separated quality names"
                  />
                </Field>
                <ToggleField label="Upgrade until cutoff" checked={profileForm.upgradeUntilCutoff} onChange={(checked) => setProfileForm((current) => ({ ...current, upgradeUntilCutoff: checked }))} />
                <ToggleField label="Upgrade unknown items" checked={profileForm.upgradeUnknownItems} onChange={(checked) => setProfileForm((current) => ({ ...current, upgradeUnknownItems: checked }))} />
                <Button type="submit" disabled={busyKey === "create-profile"}>
                  {busyKey === "create-profile" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                  Create profile
                </Button>
              </form>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Quality profiles</CardTitle>
              <CardDescription>
                Cutoff and allowed quality policy currently available to libraries.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              {qualityProfiles.map((profile) => (
                <div key={profile.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
                  <div className="flex items-center justify-between gap-3">
                    <p className="font-display text-base font-semibold text-foreground">{profile.name}</p>
                    <Badge variant="info">{profile.mediaType === "tv" ? "TV" : "Movies"}</Badge>
                  </div>
                  <p className="mt-2 text-sm text-muted-foreground">Cutoff {profile.cutoffQuality}</p>
                  <p className="mt-1 text-xs text-muted-foreground">{profile.allowedQualities}</p>
                </div>
              ))}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Operational split</CardTitle>
              <CardDescription>
                How Deluno is currently divided between movie and TV management.
              </CardDescription>
            </CardHeader>
            <CardContent className="grid grid-cols-2 gap-4">
              <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)] text-center">
                <p className="tabular text-2xl font-semibold text-foreground">{movieLibraries}</p>
                <p className="mt-1 text-xs uppercase tracking-[0.18em] text-muted-foreground">
                  Movie libraries
                </p>
              </div>
              <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)] text-center">
                <p className="tabular text-2xl font-semibold text-foreground">{tvLibraries}</p>
                <p className="mt-1 text-xs uppercase tracking-[0.18em] text-muted-foreground">
                  TV libraries
                </p>
              </div>
            </CardContent>
          </Card>
        </div>
      </div>
    </SettingsShell>
  );
}

function createEmptyLibraryForm(defaultProfileId: string): LibraryFormState {
  return {
    name: "",
    mediaType: "movies",
    purpose: "",
    rootPath: "",
    downloadsPath: "",
    qualityProfileId: defaultProfileId,
    autoSearchEnabled: true,
    missingSearchEnabled: true,
    upgradeSearchEnabled: false,
    searchIntervalHours: 6,
    retryDelayHours: 12,
    maxItemsPerRun: 25
  };
}

function createEmptyQualityProfileForm(): QualityProfileFormState {
  return {
    name: "",
    mediaType: "movies",
    cutoffQuality: "WEB-DL 2160p",
    allowedQualities: "WEB-DL 1080p, WEB-DL 2160p, Bluray-1080p, Bluray-2160p",
    upgradeUntilCutoff: true,
    upgradeUnknownItems: true
  };
}

function Field({ children, label }: { children: React.ReactNode; label: string }) {
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

function SettingsStat({ label, value }: { label: string; value: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <p className="mt-2 break-words text-sm text-foreground">{value}</p>
    </div>
  );
}

function OverviewLinkCard({
  to,
  title,
  description
}: {
  to: string;
  title: string;
  description: string;
}) {
  return (
    <Link
      to={to}
      className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)] transition-colors hover:border-primary/30 hover:bg-surface-2"
    >
      <p className="font-display text-base font-semibold text-foreground">{title}</p>
      <p className="mt-2 text-sm text-muted-foreground">{description}</p>
    </Link>
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
