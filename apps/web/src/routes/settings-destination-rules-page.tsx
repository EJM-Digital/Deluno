import { useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { ArrowRight, FileSearch, FolderTree, HardDrive, Languages, LoaderCircle, Route, Sparkles, Tag } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { KpiCard } from "../components/app/kpi-card";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { PresetField } from "../components/ui/preset-field";
import { PathInput } from "../components/ui/path-input";
import { Badge } from "../components/ui/badge";
import { NamingFormatField } from "../components/app/naming-format-field";
import { toast } from "../components/shell/toaster";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type DestinationRuleItem,
  type ImportPreviewResponse,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type TagItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";

interface SettingsDestinationRulesLoaderData {
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  destinationRules: DestinationRuleItem[];
  tags: TagItem[];
}

interface DestinationRuleFormState {
  name: string;
  mediaType: string;
  matchKind: string;
  matchValue: string;
  rootPath: string;
  folderTemplate: string;
  priority: number;
  isEnabled: boolean;
}

interface ImportPreviewFormState {
  mediaType: string;
  title: string;
  year: string;
  sourcePath: string;
  fileName: string;
  genres: string;
  tags: string;
  studio: string;
  originalLanguage: string;
}

export async function settingsDestinationRulesLoader(): Promise<SettingsDestinationRulesLoaderData> {
  const [overview, destinationRules, tags] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<DestinationRuleItem[]>("/api/destination-rules"),
    fetchJson<TagItem[]>("/api/tags")
  ]);

  return {
    libraries: overview.libraries,
    settings: overview.settings,
    destinationRules,
    tags
  };
}

const MATCH_KIND_LABELS: Record<string, string> = {
  genre: "Genre",
  tag: "Tag",
  language: "Language",
  quality: "Quality",
  anime: "Anime",
  certification: "Certification",
  library: "Library"
};

const MATCH_VALUE_OPTIONS: Record<string, { label: string; value: string }[]> = {
  genre: [
    { label: "Action", value: "Action" },
    { label: "Animation", value: "Animation" },
    { label: "Anime", value: "Anime" },
    { label: "Comedy", value: "Comedy" },
    { label: "Documentary", value: "Documentary" },
    { label: "Drama", value: "Drama" },
    { label: "Family", value: "Family" },
    { label: "Horror", value: "Horror" },
    { label: "Sci-Fi", value: "Sci-Fi" }
  ],
  language: [
    { label: "English", value: "en" },
    { label: "Japanese", value: "ja" },
    { label: "Korean", value: "ko" },
    { label: "French", value: "fr" },
    { label: "German", value: "de" },
    { label: "Spanish", value: "es" }
  ],
  quality: [
    { label: "4K / UHD", value: "4K" },
    { label: "1080p", value: "1080p" },
    { label: "720p", value: "720p" },
    { label: "WEB-DL", value: "WEB-DL" },
    { label: "Bluray", value: "Bluray" }
  ],
  certification: [
    { label: "G", value: "G" },
    { label: "PG", value: "PG" },
    { label: "PG-13", value: "PG-13" },
    { label: "R", value: "R" },
    { label: "TV-MA", value: "TV-MA" }
  ],
  anime: [
    { label: "Anime", value: "true" },
    { label: "Not anime", value: "false" }
  ],
  library: [
    { label: "Movies", value: "movies" },
    { label: "TV", value: "tv" }
  ]
};

export function SettingsDestinationRulesPage() {
  const loaderData = useLoaderData() as SettingsDestinationRulesLoaderData | undefined;
  const { destinationRules, libraries, tags, settings } = loaderData ?? {
    destinationRules: [],
    libraries: [],
    tags: [],
    settings: emptyPlatformSettingsSnapshot
  };
  const revalidator = useRevalidator();
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [preview, setPreview] = useState<ImportPreviewResponse | null>(null);
  const [previewForm, setPreviewForm] = useState<ImportPreviewFormState>(() => createPreviewForm(settings.downloadsPath ?? ""));
  const [formState, setFormState] = useState<DestinationRuleFormState>(() =>
    createDestinationRuleForm(settings.movieRootPath ?? "")
  );

  const enabledRules = destinationRules.filter((rule) => rule.isEnabled).length;
  const movieRules = destinationRules.filter((rule) => rule.mediaType === "movies").length;
  const tvRules = destinationRules.filter((rule) => rule.mediaType === "tv").length;
  const tagNames = useMemo(() => tags.map((tag) => tag.name), [tags]);

  function startCreate() {
    setEditingId(null);
    setFormState(createDestinationRuleForm(settings.movieRootPath ?? ""));
  }

  function startEdit(rule: DestinationRuleItem) {
    setEditingId(rule.id);
    setFormState({
      name: rule.name,
      mediaType: rule.mediaType,
      matchKind: rule.matchKind,
      matchValue: rule.matchValue,
      rootPath: rule.rootPath,
      folderTemplate: rule.folderTemplate ?? "",
      priority: rule.priority,
      isEnabled: rule.isEnabled
    });
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const isEditing = editingId !== null;
    setBusyKey(isEditing ? `save:${editingId}` : "create");

    try {
      const response = await authedFetch(isEditing ? `/api/destination-rules/${editingId}` : "/api/destination-rules", {
        method: isEditing ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formState)
      });

      if (!response.ok) {
        throw new Error(isEditing ? "Destination rule could not be updated." : "Destination rule could not be created.");
      }

      toast.success(isEditing ? "Destination rule updated" : "Destination rule created");
      startCreate();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Destination rule action failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDelete(ruleId: string) {
    setBusyKey(`delete:${ruleId}`);
    try {
      const response = await authedFetch(`/api/destination-rules/${ruleId}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) {
        throw new Error("Destination rule could not be removed.");
      }

      toast.success("Destination rule removed");
      if (editingId === ruleId) {
        startCreate();
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Destination rule could not be removed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handlePreview(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusyKey("preview");

    try {
      const result = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          mediaType: previewForm.mediaType,
          title: previewForm.title,
          year: previewForm.year ? Number(previewForm.year) : null,
          sourcePath: previewForm.sourcePath,
          fileName: previewForm.fileName,
          genres: splitValues(previewForm.genres),
          tags: splitValues(previewForm.tags),
          studio: previewForm.studio,
          originalLanguage: previewForm.originalLanguage
        })
      });
      setPreview(result);
      toast.success("Import route preview generated");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Import route preview failed.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title="Destination Rules"
      description="Route titles into the right root folders without splitting Deluno into multiple installs. Rules are reusable and explainable."
    >
      <div className="grid gap-[var(--grid-gap)] md:grid-cols-2 xl:grid-cols-4">
        <KpiCard
          label="Rules"
          value={String(destinationRules.length)}
          icon={Route}
          meta="Total routing rules Deluno can use when deciding where media should land."
          sparkline={[1, 1, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6, 6]}
        />
        <KpiCard
          label="Enabled"
          value={String(enabledRules)}
          icon={Sparkles}
          meta="Rules currently active for routing decisions."
          sparkline={[0, 1, 1, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 5]}
        />
        <KpiCard
          label="Movie routes"
          value={String(movieRules)}
          icon={FolderTree}
          meta="Rules scoped to movie routing."
          sparkline={[0, 0, 1, 1, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4]}
        />
        <KpiCard
          label="TV routes"
          value={String(tvRules)}
          icon={Languages}
          meta="Rules scoped to television routing."
          sparkline={[0, 0, 1, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4]}
        />
      </div>

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.18fr)_minmax(380px,0.82fr)] 2xl:grid-cols-[minmax(0,1.35fr)_minmax(440px,0.65fr)]">
        <Card>
          <CardHeader>
            <CardTitle>{editingId ? "Edit destination rule" : "Create destination rule"}</CardTitle>
            <CardDescription>
              Each rule matches a content trait and sends matching titles into a chosen root. Deluno should use these to avoid multiple Arr installs.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handleSubmit}>
              <div className="grid gap-[var(--grid-gap)] md:grid-cols-2">
                <Field label="Rule name">
                  <Input value={formState.name} onChange={(event) => setFormState((current) => ({ ...current, name: event.target.value }))} />
                </Field>
                <Field label="Media type">
                  <select
                    value={formState.mediaType}
                    onChange={(event) => setFormState((current) => ({ ...current, mediaType: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="movies">Movies</option>
                    <option value="tv">TV</option>
                  </select>
                </Field>
                <Field label="Match by">
                  <select
                    value={formState.matchKind}
                    onChange={(event) => setFormState((current) => ({ ...current, matchKind: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    {Object.entries(MATCH_KIND_LABELS).map(([value, label]) => (
                      <option key={value} value={value}>
                        {label}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Match value">
                  <PresetField
                    value={formState.matchValue}
                    onChange={(value) => setFormState((current) => ({ ...current, matchValue: value }))}
                    options={
                      formState.matchKind === "tag"
                        ? tagNames.map((tag) => ({ label: tag, value: tag }))
                        : MATCH_VALUE_OPTIONS[formState.matchKind] ?? []
                    }
                    allowCustom={formState.matchKind !== "tag" || tagNames.length === 0}
                    customLabel="Custom match value"
                    customPlaceholder={formState.matchKind === "genre" ? "Genre name" : "Match value"}
                  />
                </Field>
                <Field label="Root path">
                  <PathInput
                    value={formState.rootPath}
                    onChange={(nextValue) => setFormState((current) => ({ ...current, rootPath: nextValue }))}
                    browseTitle="Choose destination root"
                  />
                </Field>
                <Field label="Folder template">
                  <NamingFormatField
                    kind={formState.mediaType === "tv" ? "destination-series" : "destination-movie"}
                    value={formState.folderTemplate}
                    onChange={(value) => setFormState((current) => ({ ...current, folderTemplate: value }))}
                    placeholder={formState.mediaType === "tv" ? "{Series Title} ({Series Year})" : "{Movie Title} ({Release Year})"}
                  />
                </Field>
                <Field label="Priority">
                  <PresetField
                    inputType="number"
                    value={String(formState.priority)}
                    onChange={(value) => setFormState((current) => ({ ...current, priority: Number(value || 100) }))}
                    options={[
                      { label: "Highest priority (10)", value: "10" },
                      { label: "High priority (50)", value: "50" },
                      { label: "Normal priority (100)", value: "100" },
                      { label: "Low priority (200)", value: "200" }
                    ]}
                    customLabel="Custom priority"
                    customPlaceholder="Priority number"
                  />
                </Field>
                <ToggleField
                  label="Enabled"
                  checked={formState.isEnabled}
                  onChange={(checked) => setFormState((current) => ({ ...current, isEnabled: checked }))}
                />
              </div>

              <datalist id="destination-rule-tags">
                {tagNames.map((tag) => (
                  <option key={tag} value={tag} />
                ))}
              </datalist>

              <div className="flex flex-wrap gap-2">
                <Button type="submit" disabled={busyKey === "create" || (editingId !== null && busyKey === `save:${editingId}`)}>
                  {busyKey === "create" || (editingId !== null && busyKey === `save:${editingId}`) ? (
                    <LoaderCircle className="h-4 w-4 animate-spin" />
                  ) : null}
                  {editingId ? "Save rule" : "Create rule"}
                </Button>
                {editingId ? (
                  <Button type="button" variant="outline" onClick={startCreate}>
                    Cancel editing
                  </Button>
                ) : null}
              </div>
            </form>
          </CardContent>
        </Card>

        <div className="space-y-[var(--page-gap)]">
          <Card>
            <CardHeader>
              <CardTitle>Preview an import route</CardTitle>
              <CardDescription>
                Test a real download path before import. Deluno will show the selected rule, final folder, and whether hardlinking is likely.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handlePreview}>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label="Media type">
                    <select
                      value={previewForm.mediaType}
                      onChange={(event) => setPreviewForm((current) => ({ ...current, mediaType: event.target.value }))}
                      className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                    >
                      <option value="movies">Movies</option>
                      <option value="tv">TV</option>
                    </select>
                  </Field>
                  <Field label="Release year">
                    <PresetField
                      inputType="number"
                      value={previewForm.year}
                      onChange={(value) => setPreviewForm((current) => ({ ...current, year: value }))}
                      options={[
                        { label: "2024", value: "2024" },
                        { label: "2023", value: "2023" },
                        { label: "2022", value: "2022" }
                      ]}
                      customLabel="Custom year"
                      customPlaceholder="Release year"
                    />
                  </Field>
                </div>
                <Field label="Title">
                  <Input
                    value={previewForm.title}
                    onChange={(event) => setPreviewForm((current) => ({ ...current, title: event.target.value }))}
                    placeholder="Dune Part Two"
                  />
                </Field>
                <Field label="Source path">
                  <PathInput
                    value={previewForm.sourcePath}
                    onChange={(value) => setPreviewForm((current) => ({ ...current, sourcePath: value }))}
                    browseTitle="Choose completed download source"
                  />
                </Field>
                <Field label="Downloaded filename">
                  <Input
                    value={previewForm.fileName}
                    onChange={(event) => setPreviewForm((current) => ({ ...current, fileName: event.target.value }))}
                    placeholder="Dune.Part.Two.2024.2160p.WEB-DL.mkv"
                  />
                </Field>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label="Genres">
                    <PresetField
                      value={previewForm.genres}
                      onChange={(value) => setPreviewForm((current) => ({ ...current, genres: value }))}
                      options={(MATCH_VALUE_OPTIONS.genre ?? []).map((item) => ({ label: item.label, value: item.value }))}
                      allowCustom
                      customLabel="Custom genres"
                      customPlaceholder="Drama, Sci-Fi"
                    />
                  </Field>
                  <Field label="Tags">
                    <PresetField
                      value={previewForm.tags}
                      onChange={(value) => setPreviewForm((current) => ({ ...current, tags: value }))}
                      options={tagNames.map((tag) => ({ label: tag, value: tag }))}
                      allowCustom
                      customLabel="Custom tags"
                      customPlaceholder="4k, family"
                    />
                  </Field>
                  <Field label="Studio">
                    <Input
                      value={previewForm.studio}
                      onChange={(event) => setPreviewForm((current) => ({ ...current, studio: event.target.value }))}
                      placeholder="A24"
                    />
                  </Field>
                  <Field label="Language">
                    <PresetField
                      value={previewForm.originalLanguage}
                      onChange={(value) => setPreviewForm((current) => ({ ...current, originalLanguage: value }))}
                      options={MATCH_VALUE_OPTIONS.language ?? []}
                      allowCustom
                      customLabel="Custom language"
                      customPlaceholder="en"
                    />
                  </Field>
                </div>
                <Button type="submit" disabled={busyKey === "preview"}>
                  {busyKey === "preview" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <FileSearch className="h-4 w-4" />}
                  Preview decision
                </Button>
              </form>

              <div className="mt-4 rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.9)]">
                {preview ? (
                  <div className="space-y-[calc(var(--field-group-pad)*0.9)]">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge variant={preview.matchedRuleName ? "success" : "default"}>
                        {preview.matchedRuleName ? `Rule: ${preview.matchedRuleName}` : "Default root"}
                      </Badge>
                      <Badge variant={preview.preferredTransferMode === "hardlink" ? "info" : "default"}>
                        {preview.preferredTransferMode}
                      </Badge>
                      <Badge variant={preview.hardlinkAvailable ? "success" : "warning"}>
                        {preview.hardlinkAvailable ? "Hardlink available" : "Copy required"}
                      </Badge>
                    </div>
                    <p className="text-sm text-muted-foreground">{preview.explanation}</p>
                    <DecisionPath label="Source" value={preview.sourcePath} icon={HardDrive} />
                    <DecisionPath label="Destination" value={preview.destinationPath} icon={ArrowRight} />
                    {preview.decisionSteps.length ? (
                      <div className="rounded-xl border border-hairline bg-background/40 p-3">
                        <p className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Decision path</p>
                        <ol className="mt-2 space-y-1.5">
                          {preview.decisionSteps.map((step, index) => (
                            <li key={`${index}-${step}`} className="grid grid-cols-[22px_minmax(0,1fr)] gap-2 text-sm text-muted-foreground">
                              <span className="font-mono text-primary">{index + 1}</span>
                              <span>{step}</span>
                            </li>
                          ))}
                        </ol>
                      </div>
                    ) : null}
                  </div>
                ) : (
                  <div className="space-y-2 text-sm text-muted-foreground">
                    <p className="font-medium text-foreground">No preview generated yet.</p>
                    <p>Use this before importing or changing rules so the destination decision is explainable, not guessed.</p>
                  </div>
                )}
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>How routing should feel</CardTitle>
              <CardDescription>
                Users should be able to express library intent without adding more app instances.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 text-sm text-muted-foreground">
              <GuidanceRow icon={Tag} title="Tag based">
                Send titles tagged <strong className="text-foreground">kids</strong> or <strong className="text-foreground">anime</strong> into dedicated roots.
              </GuidanceRow>
              <GuidanceRow icon={Languages} title="Language based">
                Route dubbed or original-language content into different destinations when needed.
              </GuidanceRow>
              <GuidanceRow icon={FolderTree} title="Library intent">
                Keep 4K, family, anime, or archival content separate without more Deluno installs.
              </GuidanceRow>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Current rules</CardTitle>
              <CardDescription>Review, edit, and remove active routing rules.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {destinationRules.map((rule) => (
                <div key={rule.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="font-display text-base font-semibold text-foreground">{rule.name}</p>
                        <Badge variant={rule.isEnabled ? "success" : "default"}>
                          {rule.isEnabled ? "Enabled" : "Paused"}
                        </Badge>
                        <Badge variant="info">{rule.mediaType === "tv" ? "TV" : "Movies"}</Badge>
                      </div>
                      <p className="text-sm text-muted-foreground">
                        Match <span className="text-foreground">{MATCH_KIND_LABELS[rule.matchKind] ?? rule.matchKind}</span> ={" "}
                        <span className="text-foreground">{rule.matchValue}</span>
                      </p>
                      <p className="text-sm text-muted-foreground">{rule.rootPath}</p>
                      {rule.folderTemplate ? (
                        <p className="text-xs text-muted-foreground">Template: {rule.folderTemplate}</p>
                      ) : null}
                    </div>
                    <div className="flex gap-2">
                      <Button size="sm" variant="outline" onClick={() => startEdit(rule)}>
                        Edit
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => void handleDelete(rule.id)} disabled={busyKey === `delete:${rule.id}`}>
                        {busyKey === `delete:${rule.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                        Remove
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
              {destinationRules.length === 0 ? (
                <div className="rounded-xl border border-dashed border-hairline bg-surface-1 p-6 text-sm text-muted-foreground">
                  No destination rules yet. Start with one rule for kids, anime, 4K, or language-based separation.
                </div>
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Library roots today</CardTitle>
              <CardDescription>Current library roots that destination rules can build on top of.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {libraries.map((library) => (
                <div key={library.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
                  <p className="font-medium text-foreground">{library.name}</p>
                  <p className="mt-1 text-sm text-muted-foreground">{library.rootPath}</p>
                </div>
              ))}
            </CardContent>
          </Card>
        </div>
      </div>
    </SettingsShell>
  );
}

function createDestinationRuleForm(defaultRootPath: string): DestinationRuleFormState {
  return {
    name: "",
    mediaType: "movies",
    matchKind: "genre",
    matchValue: "",
    rootPath: defaultRootPath,
    folderTemplate: "",
    priority: 100,
    isEnabled: true
  };
}

function createPreviewForm(downloadsPath: string): ImportPreviewFormState {
  return {
    mediaType: "movies",
    title: "Dune Part Two",
    year: "2024",
    sourcePath: downloadsPath || "D:\\Downloads\\complete\\Dune.Part.Two.2024.2160p.WEB-DL.mkv",
    fileName: "Dune.Part.Two.2024.2160p.WEB-DL.mkv",
    genres: "Sci-Fi, Drama",
    tags: "4k",
    studio: "",
    originalLanguage: "en"
  };
}

function splitValues(value: string): string[] {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
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

function GuidanceRow({
  icon: Icon,
  title,
  children
}: {
  icon: typeof Tag;
  title: string;
  children: ReactNode;
}) {
  return (
    <div className="flex gap-3 rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
      <div className="mt-0.5 rounded-lg bg-primary/10 p-2 text-primary">
        <Icon className="h-4 w-4" />
      </div>
      <div>
        <p className="font-medium text-foreground">{title}</p>
        <p className="mt-1">{children}</p>
      </div>
    </div>
  );
}

function DecisionPath({ icon: Icon, label, value }: { icon: typeof HardDrive; label: string; value: string }) {
  return (
    <div className="rounded-xl border border-hairline bg-surface-2 p-3">
      <div className="flex items-center gap-2 density-label uppercase tracking-[0.18em] text-muted-foreground">
        <Icon className="h-3.5 w-3.5" />
        {label}
      </div>
      <p className="mt-2 break-all font-mono text-xs text-foreground">{value}</p>
    </div>
  );
}
