import { useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Eye, LoaderCircle, PencilLine, RefreshCcw, Trash2 } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { InputDescription } from "../components/ui/input-description";
import { PresetField } from "../components/ui/preset-field";
import { EmptyState } from "../components/shell/empty-state";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type IntakeListPreviewResult,
  type IntakeListPreviewItem,
  type IntakeListApprovalResult,
  type IntakeSourceItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

const INTAKE_PROVIDER_OPTIONS = [
  { label: "Custom list URL", value: "url-list" },
  { label: "Trakt", value: "trakt" },
  { label: "IMDb", value: "imdb" },
  { label: "TMDb", value: "tmdb" },
  { label: "Letterboxd", value: "letterboxd" },
  { label: "RSS feed", value: "rss" }
];

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

interface SettingsListsLoaderData extends SettingsOverviewLoaderData {
  intakeSources: IntakeSourceItem[];
}

export async function settingsListsLoader(): Promise<SettingsListsLoaderData> {
  const [overview, intakeSources] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<IntakeSourceItem[]>("/api/intake-sources")
  ]);

  return { ...overview, intakeSources };
}

export function SettingsListsPage() {
  const loaderData = useLoaderData() as SettingsListsLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { intakeSources, libraries, qualityProfiles } = loaderData;
  const revalidator = useRevalidator();
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formState, setFormState] = useState<Record<string, IntakeSourceItem>>(
    Object.fromEntries(intakeSources.map((item) => [item.id, item]))
  );
  const [createForm, setCreateForm] = useState({
    name: "",
    provider: "url-list",
    feedUrl: "",
    mediaType: "movies",
    libraryId: libraries[0]?.id ?? "",
    qualityProfileId: qualityProfiles[0]?.id ?? "",
    requiredGenres: "",
    minimumRating: "",
    minimumYear: "",
    maximumAgeDays: "",
    allowedCertifications: "",
    audience: "any",
    syncIntervalHours: "24",
    searchOnAdd: true,
    isEnabled: true
  });
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [previews, setPreviews] = useState<Record<string, IntakeListPreviewResult>>({});
  const [selectedPreviewEntries, setSelectedPreviewEntries] = useState<Record<string, string[]>>({});

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusyKey("create");
    setMessage(null);

    try {
      const payload = {
        ...createForm,
        minimumRating: createForm.minimumRating.trim() ? Number(createForm.minimumRating) : null,
        minimumYear: createForm.minimumYear.trim() ? Number(createForm.minimumYear) : null,
        maximumAgeDays: createForm.maximumAgeDays.trim() ? Number(createForm.maximumAgeDays) : null,
        syncIntervalHours: createForm.syncIntervalHours.trim() ? Number(createForm.syncIntervalHours) : 24
      };

      const response = await authedFetch("/api/intake-sources", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });

      if (!response.ok) {
        throw new Error(await readIntakeSourceError(response, "Intake source could not be created."));
      }

      setCreateForm((current) => ({
        ...current,
        name: "",
        feedUrl: ""
      }));
      setMessage("Intake source created.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Intake source could not be created.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleSave(id: string) {
    const item = formState[id];
    if (!item) {
      return;
    }

    setBusyKey(`save:${id}`);
    setMessage(null);

    try {
      const response = await authedFetch(`/api/intake-sources/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: item.name,
          provider: item.provider,
          feedUrl: item.feedUrl,
          mediaType: item.mediaType,
          libraryId: item.libraryId,
          qualityProfileId: item.qualityProfileId,
          requiredGenres: item.requiredGenres,
          minimumRating: item.minimumRating,
          minimumYear: item.minimumYear,
          maximumAgeDays: item.maximumAgeDays,
          allowedCertifications: item.allowedCertifications,
          audience: item.audience,
          syncIntervalHours: item.syncIntervalHours,
          searchOnAdd: item.searchOnAdd,
          isEnabled: item.isEnabled
        })
      });

      if (!response.ok) {
        throw new Error(await readIntakeSourceError(response, "Intake source could not be updated."));
      }

      setEditingId(null);
      setMessage("Intake source updated.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Intake source could not be updated.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDelete(id: string) {
    setBusyKey(`delete:${id}`);
    setMessage(null);

    try {
      const response = await authedFetch(`/api/intake-sources/${id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) {
        throw new Error("Intake source could not be removed.");
      }

      setMessage("Intake source removed.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Intake source could not be removed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleSync(id: string) {
    setBusyKey(`sync:${id}`);
    setMessage(null);

    try {
      const response = await authedFetch(`/api/intake-sources/${id}/sync`, { method: "POST" });
      if (!response.ok) {
        throw new Error("Sync could not be queued.");
      }

      setMessage("Sync queued.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Sync could not be queued.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handlePreview(id: string) {
    setBusyKey(`preview:${id}`);
    setMessage(null);
    try {
      const response = await authedFetch(`/api/intake-sources/${id}/preview`, { method: "POST" });
      if (!response.ok) {
        const body = await response.json().catch(() => null) as { message?: string } | null;
        throw new Error(body?.message ?? "Preview could not be loaded.");
      }
      const preview = await response.json() as IntakeListPreviewResult;
      setPreviews((current) => ({ ...current, [id]: preview }));
      setSelectedPreviewEntries((current) => ({
        ...current,
        [id]: preview.items
          .filter((item) => item.action === "would add")
          .map(previewEntryKey)
      }));
      setMessage("Preview ready. Nothing was added or searched.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Preview could not be loaded.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleApprovePreview(id: string, searchAfterAdd: boolean) {
    const preview = previews[id];
    if (!preview) return;
    const keys = new Set(selectedPreviewEntries[id] ?? []);
    const entries = preview.items
      .filter((item) => keys.has(previewEntryKey(item)) && item.action === "would add")
      .map((item) => ({ title: item.title, year: item.year, imdbId: item.imdbId }));
    if (entries.length === 0) {
      setMessage("Choose at least one eligible preview entry first.");
      return;
    }

    setBusyKey(`approve:${id}:${searchAfterAdd ? "search" : "add"}`);
    setMessage(null);
    try {
      const response = await authedFetch(`/api/intake-sources/${id}/approve-preview`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ entries, searchAfterAdd })
      });
      if (!response.ok) {
        const body = await response.json().catch(() => null) as { message?: string } | null;
        throw new Error(body?.message ?? "Selected entries could not be added.");
      }
      const result = await response.json() as IntakeListApprovalResult;
      setMessage(`${result.addedCount} title${result.addedCount === 1 ? "" : "s"} added from ${result.selectedCount} approved preview entry${result.selectedCount === 1 ? "" : "ies"}.${result.searchRequested ? " Deluno will search them using normal automation rules." : ""}`);
      setPreviews((current) => {
        const next = { ...current };
        delete next[id];
        return next;
      });
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Selected entries could not be added.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleExcludePreview(id: string, entry: IntakeListPreviewItem, durationDays: number | null) {
    setBusyKey(`exclude:${id}:${previewEntryKey(entry)}`);
    setMessage(null);
    try {
      const response = await authedFetch(`/api/intake-sources/${id}/exclude-preview`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title: entry.title, year: entry.year, imdbId: entry.imdbId, durationDays })
      });
      if (!response.ok) throw new Error("This entry could not be excluded.");
      const exclusion = await response.json() as { id: string };
      setPreviews((current) => ({
        ...current,
        [id]: {
          ...current[id],
          items: current[id].items.map((item) => previewEntryKey(item) === previewEntryKey(entry)
            ? { ...item, action: "excluded", reason: durationDays ? `Ignored for ${durationDays} days.` : "Excluded from this list.", exclusionId: exclusion.id }
            : item)
        }
      }));
      setSelectedPreviewEntries((current) => ({ ...current, [id]: (current[id] ?? []).filter((key) => key !== previewEntryKey(entry)) }));
      setMessage(durationDays ? `${entry.title} will be ignored for ${durationDays} days.` : `${entry.title} will not be added from this list again.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "This entry could not be excluded.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleRestorePreviewEntry(id: string, entry: IntakeListPreviewItem) {
    if (!entry.exclusionId) return;
    setBusyKey(`restore:${id}:${entry.exclusionId}`);
    setMessage(null);
    try {
      const response = await authedFetch(`/api/intake-sources/${id}/exclusions/${entry.exclusionId}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("This entry could not be restored.");
      setPreviews((current) => ({
        ...current,
        [id]: {
          ...current[id],
          items: current[id].items.map((item) => previewEntryKey(item) === previewEntryKey(entry)
            ? { ...item, action: "would add", reason: "Eligible again. Choose it when you are ready to add it.", exclusionId: null }
            : item)
        }
      }));
      setMessage(`${entry.title} is eligible for this list again.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "This entry could not be restored.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title="Import lists"
      description="Follow the watchlists and curated lists you trust. Deluno checks them on a schedule, adds only matching titles, and can start a search when they arrive."
    >
      {message ? (
        <div className="density-help rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-muted-foreground">
          {message}
        </div>
      ) : null}

      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Add an import list</CardTitle>
            <CardDescription>Start with a watchlist or curated list. The advanced filters below are optional; leave them empty to follow the whole list.</CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-3" onSubmit={handleCreate}>
              <Field label="Name" description="A friendly label for this list in Deluno, such as “Weekend movies” or “My TV watchlist”.">
                <Input
                  value={createForm.name}
                  onChange={(event) => setCreateForm((state) => ({ ...state, name: event.target.value }))}
                />
              </Field>
              <Field label="List type" description="Start with Custom list URL for a public list. Deluno recognises compatible list sites automatically.">
                <PresetField
                  value={createForm.provider}
                  onChange={(value) => setCreateForm((state) => ({ ...state, provider: value }))}
                  options={INTAKE_PROVIDER_OPTIONS}
                  customLabel="Custom provider"
                  customPlaceholder="Provider key"
                />
              </Field>
              <Field label="List URL" description={listAddressHelp(createForm.provider)}>
                <Input
                  value={createForm.feedUrl}
                  onChange={(event) => setCreateForm((state) => ({ ...state, feedUrl: event.target.value }))}
                />
              </Field>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="Media type" description="What type of content this source provides: Movies only or TV series only.">
                  <Select
                    value={createForm.mediaType}
                    onChange={(value) => setCreateForm((state) => ({ ...state, mediaType: value }))}
                    options={[
                      { label: "Movies", value: "movies" },
                      { label: "TV", value: "tv" }
                    ]}
                  />
                </Field>
                <Field label="Library default" description="The library to add titles to when importing from this source without explicit routing.">
                  <Select
                    value={createForm.libraryId ?? ""}
                    onChange={(value) => setCreateForm((state) => ({ ...state, libraryId: value }))}
                    options={[
                      { label: "No default library", value: "" },
                      ...libraries.map((library) => ({ label: library.name, value: library.id }))
                    ]}
                  />
                </Field>
              </div>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="Required genres" description="Only import entries that match at least one of these genres (comma-separated).">
                  <Input
                    value={createForm.requiredGenres}
                    onChange={(event) => setCreateForm((state) => ({ ...state, requiredGenres: event.target.value }))}
                  />
                </Field>
                <Field label="Allowed certifications" description="Optional certification allow-list, for example PG-13, TV-14, TV-MA.">
                  <Input
                    value={createForm.allowedCertifications}
                    onChange={(event) => setCreateForm((state) => ({ ...state, allowedCertifications: event.target.value }))}
                  />
                </Field>
              </div>
              <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                <Field label="Minimum rating">
                  <Input
                    value={createForm.minimumRating}
                    onChange={(event) => setCreateForm((state) => ({ ...state, minimumRating: event.target.value }))}
                    placeholder="0-10"
                  />
                </Field>
                <Field label="Minimum year">
                  <Input
                    value={createForm.minimumYear}
                    onChange={(event) => setCreateForm((state) => ({ ...state, minimumYear: event.target.value }))}
                    placeholder="e.g. 2020"
                  />
                </Field>
                <Field label="Max age days">
                  <Input
                    value={createForm.maximumAgeDays}
                    onChange={(event) => setCreateForm((state) => ({ ...state, maximumAgeDays: event.target.value }))}
                    placeholder="e.g. 365"
                  />
                </Field>
                <Field label="Sync hours">
                  <Input
                    value={createForm.syncIntervalHours}
                    onChange={(event) => setCreateForm((state) => ({ ...state, syncIntervalHours: event.target.value }))}
                    placeholder="24"
                  />
                </Field>
              </div>
              <Field label="Audience" description="Restrict to general, kids, or adult-oriented entries when provider metadata supports it.">
                <Select
                  value={createForm.audience}
                  onChange={(value) => setCreateForm((state) => ({ ...state, audience: value }))}
                  options={[
                    { label: "Any", value: "any" },
                    { label: "Kids", value: "kids" },
                    { label: "Adult", value: "adult" }
                  ]}
                />
              </Field>
              <div className="grid gap-3 sm:grid-cols-2">
                <div className="rounded-xl border border-hairline bg-surface-1 p-4">
                  <label className="flex items-center gap-3 text-foreground cursor-pointer">
                    <input type="checkbox" checked={createForm.searchOnAdd} onChange={(event) => setCreateForm((state) => ({ ...state, searchOnAdd: event.target.checked }))} />
                    <span className="font-medium">Search on add</span>
                  </label>
                  <InputDescription>When a new matching title is found, add it to the chosen library and ask Deluno to search for it. Turn this off to add without downloading.</InputDescription>
                </div>
                <div className="rounded-xl border border-hairline bg-surface-1 p-4">
                  <label className="flex items-center gap-3 text-foreground cursor-pointer">
                    <input type="checkbox" checked={createForm.isEnabled} onChange={(event) => setCreateForm((state) => ({ ...state, isEnabled: event.target.checked }))} />
                    <span className="font-medium">Enabled</span>
                  </label>
                  <InputDescription>Whether this intake source is active and will be checked during scheduled list refreshes.</InputDescription>
                </div>
              </div>
              <Button type="submit" disabled={busyKey === "create"}>
                {busyKey === "create" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Add import list
              </Button>
            </form>
          </CardContent>
        </Card>

        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Your import lists</CardTitle>
            <CardDescription>Each list has its own destination, filters, schedule, and last-sync result. Syncing never removes titles already in your library.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            {intakeSources.length ? (
              intakeSources.map((item) => {
                const current = formState[item.id] ?? item;
                const editing = editingId === item.id;

                return (
                  <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                    <div className="flex items-center justify-between gap-3">
                      <div className="min-w-0">
                        {editing ? (
                          <Input
                            value={current.name}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, name: event.target.value }
                              }))
                            }
                          />
                        ) : (
                          <p className="font-display text-base font-semibold text-foreground">{current.name}</p>
                        )}
                      </div>
                      <div className="flex items-center gap-2">
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => void handlePreview(item.id)}
                          disabled={busyKey === `preview:${item.id}`}
                          title="Preview without adding titles"
                        >
                          {busyKey === `preview:${item.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Eye className="h-4 w-4" />}
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => void handleSync(item.id)}
                          disabled={busyKey === `sync:${item.id}`}
                          title="Sync now"
                        >
                          {busyKey === `sync:${item.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCcw className="h-4 w-4" />}
                        </Button>
                        <Button variant="ghost" size="icon" onClick={() => setEditingId(editing ? null : item.id)}>
                          <PencilLine className="h-4 w-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => void handleDelete(item.id)}
                          disabled={busyKey === `delete:${item.id}`}
                        >
                          {busyKey === `delete:${item.id}` ? (
                            <LoaderCircle className="h-4 w-4 animate-spin" />
                          ) : (
                            <Trash2 className="h-4 w-4" />
                          )}
                        </Button>
                      </div>
                    </div>

                    <div className="mt-3 grid gap-3 sm:grid-cols-2">
                      <Field label="List type">
                        {editing ? (
                          <PresetField
                            value={current.provider}
                            onChange={(value) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, provider: value }
                              }))
                            }
                            options={INTAKE_PROVIDER_OPTIONS}
                            customLabel="Custom provider"
                            customPlaceholder="Provider key"
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.provider}</p>
                        )}
                      </Field>
                      <Field label="Feed URL / identifier">
                        {editing ? (
                          <Input
                            value={current.feedUrl}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, feedUrl: event.target.value }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.feedUrl}</p>
                        )}
                      </Field>
                      <Field label="Media type">
                        {editing ? (
                          <Select
                            value={current.mediaType}
                            onChange={(value) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, mediaType: value }
                              }))
                            }
                            options={[
                              { label: "Movies", value: "movies" },
                              { label: "TV", value: "tv" }
                            ]}
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.mediaType}</p>
                        )}
                      </Field>
                      <Field label="Library default">
                        {editing ? (
                          <Select
                            value={current.libraryId ?? ""}
                            onChange={(value) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, libraryId: value || null }
                              }))
                            }
                            options={[
                              { label: "No default library", value: "" },
                              ...libraries.map((library) => ({ label: library.name, value: library.id }))
                            ]}
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.libraryName ?? "No default library"}</p>
                        )}
                      </Field>
                      <Field label="Sync status">
                        <p className="text-sm text-muted-foreground">
                          {(current.lastSyncStatus ?? "never").toUpperCase()}
                          {current.lastSyncUtc ? ` • ${new Date(current.lastSyncUtc).toLocaleString()}` : ""}
                        </p>
                        {current.lastSyncSummary ? (
                          <p className="mt-1 text-xs text-muted-foreground">{current.lastSyncSummary}</p>
                        ) : null}
                      </Field>
                    </div>

                    <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                      <Field label="Required genres">
                        {editing ? (
                          <Input
                            value={current.requiredGenres ?? ""}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, requiredGenres: event.target.value }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.requiredGenres || "Any"}</p>
                        )}
                      </Field>
                      <Field label="Min rating">
                        {editing ? (
                          <Input
                            value={current.minimumRating?.toString() ?? ""}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: {
                                  ...current,
                                  minimumRating: event.target.value.trim() ? Number(event.target.value) : null
                                }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.minimumRating ?? "Any"}</p>
                        )}
                      </Field>
                      <Field label="Min year">
                        {editing ? (
                          <Input
                            value={current.minimumYear?.toString() ?? ""}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: {
                                  ...current,
                                  minimumYear: event.target.value.trim() ? Number(event.target.value) : null
                                }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.minimumYear ?? "Any"}</p>
                        )}
                      </Field>
                      <Field label="Max age days">
                        {editing ? (
                          <Input
                            value={current.maximumAgeDays?.toString() ?? ""}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: {
                                  ...current,
                                  maximumAgeDays: event.target.value.trim() ? Number(event.target.value) : null
                                }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.maximumAgeDays ?? "Any"}</p>
                        )}
                      </Field>
                    </div>

                    <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                      <Field label="Allowed certifications">
                        {editing ? (
                          <Input
                            value={current.allowedCertifications ?? ""}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, allowedCertifications: event.target.value }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.allowedCertifications || "Any"}</p>
                        )}
                      </Field>
                      <Field label="Audience">
                        {editing ? (
                          <Select
                            value={current.audience ?? "any"}
                            onChange={(value) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: { ...current, audience: value }
                              }))
                            }
                            options={[
                              { label: "Any", value: "any" },
                              { label: "Kids", value: "kids" },
                              { label: "Adult", value: "adult" }
                            ]}
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.audience ?? "any"}</p>
                        )}
                      </Field>
                      <Field label="Sync hours">
                        {editing ? (
                          <Input
                            value={current.syncIntervalHours?.toString() ?? "24"}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [item.id]: {
                                  ...current,
                                  syncIntervalHours: event.target.value.trim() ? Number(event.target.value) : 24
                                }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.syncIntervalHours ?? 24}</p>
                        )}
                      </Field>
                    </div>

                    <div className="mt-3 grid gap-3 sm:grid-cols-2">
                      <ToggleField
                        label="Search on add"
                        checked={editing ? current.searchOnAdd : item.searchOnAdd}
                        onChange={(checked) =>
                          setFormState((state) => ({
                            ...state,
                            [item.id]: { ...current, searchOnAdd: checked }
                          }))
                        }
                        disabled={!editing}
                      />
                      <ToggleField
                        label="Enabled"
                        checked={editing ? current.isEnabled : item.isEnabled}
                        onChange={(checked) =>
                          setFormState((state) => ({
                            ...state,
                            [item.id]: { ...current, isEnabled: checked }
                          }))
                        }
                        disabled={!editing}
                      />
                    </div>

                    {editing ? (
                      <div className="mt-4">
                        <Button onClick={() => void handleSave(item.id)} disabled={busyKey === `save:${item.id}`}>
                          {busyKey === `save:${item.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                          Save intake source
                        </Button>
                      </div>
                    ) : null}
                    {previews[item.id] ? (
                      <ImportListPreview
                        preview={previews[item.id]}
                        selectedKeys={selectedPreviewEntries[item.id] ?? []}
                        busy={busyKey?.startsWith(`approve:${item.id}:`) ?? false}
                        onSelectionChange={(key, selected) => setSelectedPreviewEntries((current) => {
                          const next = new Set(current[item.id] ?? []);
                          if (selected) next.add(key); else next.delete(key);
                          return { ...current, [item.id]: [...next] };
                        })}
                        onApprove={(searchAfterAdd) => void handleApprovePreview(item.id, searchAfterAdd)}
                        onExclude={(entry, durationDays) => void handleExcludePreview(item.id, entry, durationDays)}
                        onRestore={(entry) => void handleRestorePreviewEntry(item.id, entry)}
                      />
                    ) : null}
                  </div>
                );
              })
            ) : (
              <EmptyState
                size="sm"
                variant="custom"
                title="No import lists yet"
                description="Add a custom list URL, a supported list provider, or an RSS feed to bring the titles you follow into Deluno."
              />
            )}
          </CardContent>
        </Card>
      </div>
    </SettingsShell>
  );
}

function previewEntryKey(entry: { title: string; year: number | null; imdbId: string | null }) {
  return `${entry.imdbId ?? "title"}:${entry.title.toLocaleLowerCase()}:${entry.year ?? ""}`;
}

function ImportListPreview({
  preview,
  selectedKeys,
  busy,
  onSelectionChange,
  onApprove,
  onExclude,
  onRestore
}: {
  preview: IntakeListPreviewResult;
  selectedKeys: string[];
  busy: boolean;
  onSelectionChange: (key: string, selected: boolean) => void;
  onApprove: (searchAfterAdd: boolean) => void;
  onExclude: (entry: IntakeListPreviewItem, durationDays: number | null) => void;
  onRestore: (entry: IntakeListPreviewItem) => void;
}) {
  const wouldAdd = preview.items.filter((item) => item.action === "would add").length;
  const existing = preview.items.filter((item) => item.action === "already in library").length;
  const eligible = preview.items.filter((item) => item.action === "would add");
  return (
    <div className="mt-4 rounded-xl border border-primary/25 bg-primary/5 p-4">
      <p className="font-semibold text-foreground">Read-only preview</p>
      <p className="mt-1 text-sm text-muted-foreground">
        {preview.fetchedCount} found · {wouldAdd} would add · {existing} already in library
        {preview.targetLibraryName ? ` · destination: ${preview.targetLibraryName}` : " · no destination configured"}
      </p>
      {preview.warnings.map((warning) => <p key={warning} className="mt-2 text-xs text-warning">{warning}</p>)}
      <div className="mt-3 max-h-64 space-y-2 overflow-y-auto">
        {preview.items.map((entry, index) => {
          const key = previewEntryKey(entry);
          const selectable = entry.action === "would add";
          return (
          <div key={`${entry.title}-${entry.year ?? "unknown"}-${index}`} className="rounded-lg border border-hairline bg-surface-1 px-3 py-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <label className="flex items-center gap-2 font-medium text-foreground">
                {selectable ? <input type="checkbox" checked={selectedKeys.includes(key)} onChange={(event) => onSelectionChange(key, event.target.checked)} /> : null}
                {entry.title}{entry.year ? ` (${entry.year})` : ""}
              </label>
              <span className="font-mono text-[10px] uppercase text-muted-foreground">{entry.action} · {entry.matchConfidence} confidence</span>
            </div>
            <p className="mt-1 text-xs text-muted-foreground">{entry.reason}</p>
            {selectable ? (
              <div className="mt-2 flex flex-wrap gap-2">
                <Button type="button" size="sm" variant="ghost" onClick={() => onExclude(entry, 7)}>Ignore 7 days</Button>
                <Button type="button" size="sm" variant="ghost" onClick={() => onExclude(entry, null)}>Exclude</Button>
              </div>
            ) : entry.action === "excluded" && entry.exclusionId ? (
              <Button type="button" size="sm" variant="ghost" className="mt-2" onClick={() => onRestore(entry)}>Allow again</Button>
            ) : null}
          </div>
          );
        })}
      </div>
      {eligible.length ? (
        <div className="mt-4 flex flex-wrap gap-2">
          <Button size="sm" variant="outline" disabled={busy || selectedKeys.length === 0} onClick={() => onApprove(false)}>
            {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
            Add selected
          </Button>
          <Button size="sm" disabled={busy || selectedKeys.length === 0} onClick={() => onApprove(true)}>
            {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
            Add selected and search
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function listAddressHelp(provider: string) {
  switch (provider) {
    case "trakt":
      return "Paste a Trakt list or watchlist URL. A Trakt username also follows that person's watchlist.";
    case "imdb":
      return "Paste an IMDb list URL, its ls… identifier, or an IMDb CSV export URL.";
    case "tmdb":
      return "Paste a TMDb list URL or list ID. Deluno uses the title-matching service configured for this installation.";
    case "mdblist":
      return "Existing MDbList source. For a public MDbList list, choose Custom list URL and paste https://mdblist.com/lists/owner/list-name.";
    case "letterboxd":
      return "Paste a public Letterboxd list URL or its RSS feed.";
    case "rss":
      return "Paste a public RSS or Atom feed URL.";
    case "url-list":
      return "Paste a public list URL. Deluno recognises compatible list sites automatically.";
    default:
      return "Paste the provider's public list URL or identifier.";
  }

}

async function readIntakeSourceError(response: Response, fallback: string) {
  const body = await response.json().catch(() => null) as {
    errors?: Record<string, string[] | undefined>;
    detail?: string;
    title?: string;
  } | null;
  const validationMessage = body?.errors
    ? Object.values(body.errors).flat().find((value): value is string => Boolean(value?.trim()))
    : null;
  return validationMessage ?? body?.detail ?? body?.title ?? fallback;
}

function Field({ children, description, label }: { children: ReactNode; description?: string; label: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
      {description && <InputDescription>{description}</InputDescription>}
    </div>
  );
}

function Select({
  value,
  onChange,
  options
}: {
  value: string;
  onChange: (value: string) => void;
  options: Array<{ label: string; value: string }>;
}) {
  return (
    <select
      value={value}
      onChange={(event) => onChange(event.target.value)}
      className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
    >
      {options.map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </select>
  );
}

function ToggleField({
  checked,
  label,
  onChange,
  disabled = false
}: {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <label className="density-field density-control-text flex items-center gap-3 rounded-xl border border-hairline bg-surface-1 text-foreground">
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} />
      {label}
    </label>
  );
}
