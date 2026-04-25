import { useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { FolderOpen, Link2, LoaderCircle, Sparkles, Trash2 } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { PathInput } from "../components/ui/path-input";
import { NamingFormatField } from "../components/app/naming-format-field";
import { settingsOverviewLoader } from "./settings-overview-page";
import { emptyPlatformSettingsSnapshot, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsMediaManagementLoader = settingsOverviewLoader;

export function SettingsMediaManagementPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  const { libraries, settings } = loaderData ?? {
    libraries: [],
    qualityProfiles: [],
    settings: emptyPlatformSettingsSnapshot
  };
  const revalidator = useRevalidator();
  const [formState, setFormState] = useState(settings);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setMessage(null);

    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formState)
      });

      if (!response.ok) {
        throw new Error("Media-management settings could not be saved.");
      }

      setMessage("Media-management settings saved.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Media-management settings could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <SettingsShell
      title="Media Management"
      description="Naming, import, and file-handling policy should live here instead of being scattered across libraries and download clients."
    >
      {message ? (
        <div className="density-help rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-muted-foreground">
          {message}
        </div>
      ) : null}

      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Import and file handling</CardTitle>
            <CardDescription>
              Choose how Deluno names folders and what it should do after a download finishes. Presets are safe defaults; advanced patterns are optional.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handleSave}>
              <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
                <SectionIntro
                  icon={Sparkles}
                  title="Folder naming"
                  copy="Pick the naming style Deluno should use when it creates or renames media. The recommended preset is the safest choice for most libraries."
                />
                <div className="mt-5 grid gap-5">
                  <Field label="Movie folders" description="Used when Deluno creates or renames a movie folder.">
                  <NamingFormatField
                    kind="movie-folder"
                    value={formState.movieFolderFormat}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, movieFolderFormat: value }))
                    }
                    placeholder="{Movie Title} ({Release Year})"
                  />
                  </Field>
                  <Field label="Series folders" description="Used when Deluno creates or renames a TV show folder.">
                  <NamingFormatField
                    kind="series-folder"
                    value={formState.seriesFolderFormat}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, seriesFolderFormat: value }))
                    }
                    placeholder="{Series Title} ({Series Year})"
                  />
                  </Field>
                  <Field label="Episode files" description="Used when Deluno renames imported episode files.">
                  <NamingFormatField
                    kind="episode-file"
                    value={formState.episodeFileFormat}
                    onChange={(value) =>
                      setFormState((current) => ({ ...current, episodeFileFormat: value }))
                    }
                    placeholder="{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}"
                  />
                  </Field>
                </div>
              </div>

              <div className="grid gap-[var(--grid-gap)] lg:grid-cols-[minmax(0,0.95fr)_minmax(0,1.05fr)]">
                <Field
                  label="Completed downloads folder"
                  description="Where your external download client leaves finished files before Deluno imports them."
                >
                  <PathInput
                    value={formState.downloadsPath ?? ""}
                    onChange={(nextValue) =>
                      setFormState((current) => ({ ...current, downloadsPath: nextValue }))
                    }
                    browseTitle="Choose downloads folder"
                  />
                </Field>

                <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
                  <SectionIntro
                    icon={FolderOpen}
                    title="What happens on import"
                    copy="Choose how Deluno should handle files after a download finishes."
                  />
                  <div className="mt-4 grid gap-3 sm:grid-cols-2">
                <ToggleField
                  label="Rename on import"
                      description="Rename files and folders using the presets above."
                  checked={formState.renameOnImport}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, renameOnImport: checked }))
                  }
                />
                <ToggleField
                  label="Use hardlinks"
                      description="Keep seeding while avoiding a second full copy when the filesystem supports it."
                  checked={formState.useHardlinks}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, useHardlinks: checked }))
                  }
                />
                <ToggleField
                  label="Cleanup empty folders"
                      description="Remove leftover empty folders after import."
                  checked={formState.cleanupEmptyFolders}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, cleanupEmptyFolders: checked }))
                  }
                />
                <ToggleField
                  label="Remove completed downloads"
                      description="Ask Deluno to clear completed items from the download client after import."
                  checked={formState.removeCompletedDownloads}
                  onChange={(checked) =>
                    setFormState((current) => ({ ...current, removeCompletedDownloads: checked }))
                  }
                />
                  </div>
                </div>
              </div>

              <Button type="submit" disabled={busy}>
                {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Save media management
              </Button>
            </form>
          </CardContent>
        </Card>

        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Library impact</CardTitle>
            <CardDescription>Active libraries that will inherit these rules.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            {libraries.map((library) => (
              <div key={library.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
                <div className="flex items-center justify-between gap-3">
                  <p className="font-display text-base font-semibold text-foreground">{library.name}</p>
                  <span className="rounded-full border border-hairline px-2.5 py-1 text-xs text-muted-foreground">
                    {library.mediaType === "tv" ? "TV" : "Movies"}
                  </span>
                </div>
                <p className="density-help mt-2 text-muted-foreground">{library.rootPath}</p>
              </div>
            ))}
          </CardContent>
        </Card>
      </div>
    </SettingsShell>
  );
}

function Field({
  children,
  description,
  label
}: {
  children: ReactNode;
  description?: string;
  label: string;
}) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-background/30">
      <p className="density-label font-semibold uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      {description ? <p className="density-help mt-1 text-muted-foreground">{description}</p> : null}
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
    </div>
  );
}

function ToggleField({
  checked,
  description,
  label,
  onChange
}: {
  checked: boolean;
  description: string;
  label: string;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="density-field density-control-text flex min-h-[84px] cursor-pointer items-start gap-3 rounded-xl border border-hairline bg-background/30 text-foreground transition hover:border-primary/25 hover:bg-surface-2">
      <input className="mt-1" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span>
        <span className="flex items-center gap-2 font-semibold">
          {label === "Use hardlinks" ? <Link2 className="h-4 w-4 text-primary" /> : null}
          {label === "Remove completed downloads" ? <Trash2 className="h-4 w-4 text-muted-foreground" /> : null}
          {label}
        </span>
        <span className="mt-1 block leading-relaxed text-muted-foreground">{description}</span>
      </span>
    </label>
  );
}

function SectionIntro({
  icon: Icon,
  title,
  copy
}: {
  icon: typeof Sparkles;
  title: string;
  copy: string;
}) {
  return (
    <div className="flex gap-3">
      <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
        <Icon className="h-5 w-5" />
      </div>
      <div>
        <h2 className="font-display text-lg font-semibold tracking-tight text-foreground">{title}</h2>
        <p className="density-help mt-1 max-w-3xl text-muted-foreground">{copy}</p>
      </div>
    </div>
  );
}
