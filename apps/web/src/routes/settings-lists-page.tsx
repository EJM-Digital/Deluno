import { useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { LoaderCircle, PencilLine, RefreshCcw, Trash2 } from "lucide-react";
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
  type IntakeSourceItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

const INTAKE_PROVIDER_OPTIONS = [
  { label: "Trakt", value: "trakt" },
  { label: "IMDb", value: "imdb" },
  { label: "TMDb", value: "tmdb" },
  { label: "Letterboxd", value: "letterboxd" },
  { label: "RSS feed", value: "rss" },
  { label: "Plain URL list", value: "url-list" }
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
    provider: "trakt",
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
        throw new Error("Intake source could not be created.");
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
        throw new Error("Intake source could not be updated.");
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

  return (
    <SettingsShell
      title="Intake Sources"
      description="Configure watchlists, discovery feeds, and automatic title sources without needing to understand provider internals."
    >
      {message ? (
        <div className="density-help rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-muted-foreground">
          {message}
        </div>
      ) : null}

      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Add intake source</CardTitle>
            <CardDescription>Define how Deluno should ingest titles from external watchlists and discovery feeds.</CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-3" onSubmit={handleCreate}>
              <Field label="Name" description="A friendly label to identify this intake source in your configuration.">
                <Input
                  value={createForm.name}
                  onChange={(event) => setCreateForm((state) => ({ ...state, name: event.target.value }))}
                />
              </Field>
              <Field label="Provider" description="The service you're importing from: Trakt, IMDb, TMDb, Letterboxd, RSS feed, or a custom list.">
                <PresetField
                  value={createForm.provider}
                  onChange={(value) => setCreateForm((state) => ({ ...state, provider: value }))}
                  options={INTAKE_PROVIDER_OPTIONS}
                  customLabel="Custom provider"
                  customPlaceholder="Provider key"
                />
              </Field>
              <Field label="Feed URL / identifier" description="The URL (for RSS/feeds) or identifier (Trakt username, IMDb list ID, etc.) for this intake source.">
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
                  <InputDescription>Automatically search for and add matching titles when new items are discovered in this intake source.</InputDescription>
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
                Add intake source
              </Button>
            </form>
          </CardContent>
        </Card>

        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Configured sources</CardTitle>
            <CardDescription>Saved watchlists and feed definitions Deluno can manage today.</CardDescription>
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
                      <Field label="Provider">
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
                  </div>
                );
              })
            ) : (
              <EmptyState
                size="sm"
                variant="custom"
                title="No intake sources yet"
                description="Add an intake source such as IMDb, TMDB, or Trakt to auto-populate your libraries."
              />
            )}
          </CardContent>
        </Card>
      </div>
    </SettingsShell>
  );
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
