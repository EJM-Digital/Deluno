import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { LayoutGrid, LoaderCircle, Monitor, PanelTop, Rows3 } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { SaveStatus, useSaveStatus } from "../components/shell/save-status";
import { toast } from "../components/shell/toaster";
import { settingsOverviewLoader } from "./settings-overview-page";
import { densityDisplayName, isDensity, useDensity, type Density } from "../lib/use-density";
import {
  emptyPlatformSettingsSnapshot,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

const densityOptions: Array<{
  value: Density;
  label: string;
  description: string;
  icon: typeof Rows3;
}> = [
  {
    value: "compact",
    label: "Compact",
    description: "Tighter spacing and smaller type for laptops or dense power-user workflows.",
    icon: Rows3
  },
  {
    value: "comfortable",
    label: "Standard",
    description: "The default workspace posture: readable, efficient, and suitable for most screens.",
    icon: LayoutGrid
  },
  {
    value: "spacious",
    label: "Spacious",
    description: "Larger typography, controls, cards, and canvas for desktop monitors.",
    icon: PanelTop
  },
  {
    value: "expanded",
    label: "Expanded",
    description: "The most screen-filling workspace for 1440p, ultrawide, and long operator sessions.",
    icon: Monitor
  }
];

export function SettingsUiPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  const settings = loaderData?.settings ?? emptyPlatformSettingsSnapshot;
  const revalidator = useRevalidator();
  const [formState, setFormState] = useState(settings);
  const [busy, setBusy] = useState(false);
  const save = useSaveStatus();
  const { density, setDensity } = useDensity();

  useEffect(() => {
    if (isDensity(formState.uiDensity)) {
      if (density !== formState.uiDensity) setDensity(formState.uiDensity as Density);
    }
  }, [formState.uiDensity, density, setDensity]);

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    save.markSyncing("Saving UI preferences...");

    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formState)
      });

      if (!response.ok) {
        throw new Error("UI settings could not be saved.");
      }

      save.markSaved("Saved");
      toast.success("UI preferences saved");
      revalidator.revalidate();
    } catch (error) {
      const message = error instanceof Error ? error.message : "UI settings could not be saved.";
      save.markError(message);
      toast.error(message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <SettingsShell
      title="Interface"
      description="Choose how Deluno should feel on your hardware before you fine-tune the visual style."
    >
      <div className="settings-split settings-split-balanced">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle className="flex items-center justify-between gap-3">
              UI preferences
              <SaveStatus state={save.state} message={save.message} />
            </CardTitle>
            <CardDescription>
              Density applies live so you can immediately see whether the shell, typography, and controls feel right for your screen.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[var(--grid-gap)]" onSubmit={handleSave}>
              <Field label="Theme">
                <Select
                  value={formState.uiTheme}
                  onChange={(value) => setFormState((current) => ({ ...current, uiTheme: value }))}
                  options={[
                    { label: "System", value: "system" },
                    { label: "Dark", value: "dark" },
                    { label: "Light", value: "light" }
                  ]}
                />
              </Field>

              <Field label="Density">
                <div className="grid gap-[calc(var(--grid-gap)*0.75)] md:grid-cols-2">
                  {densityOptions.map((option) => {
                    const Icon = option.icon;
                    const active = formState.uiDensity === option.value;
                    return (
                      <button
                        key={option.value}
                        type="button"
                        onClick={() => setFormState((current) => ({ ...current, uiDensity: option.value }))}
                        className={`density-field min-h-[calc(var(--control-height-lg)*2.6)] rounded-xl border text-left transition-colors ${
                          active
                            ? "border-primary/40 bg-primary/10 text-foreground"
                            : "border-hairline bg-surface-1 text-foreground hover:border-primary/25 hover:bg-surface-2"
                        }`}
                      >
                        <div className="flex items-start gap-3">
                          <div className={`rounded-lg p-2 ${active ? "bg-primary/15 text-primary" : "bg-surface-2 text-muted-foreground"}`}>
                            <Icon className="h-4 w-4" />
                          </div>
                          <div className="min-w-0">
                            <p className="font-medium">{option.label}</p>
                            <p className="density-help mt-1 text-muted-foreground">{option.description}</p>
                          </div>
                        </div>
                      </button>
                    );
                  })}
                </div>
              </Field>

              <Field label="Default Movies view">
                <Select
                  value={formState.defaultMovieView}
                  onChange={(value) => setFormState((current) => ({ ...current, defaultMovieView: value }))}
                  options={[
                    { label: "Grid", value: "grid" },
                    { label: "List", value: "list" }
                  ]}
                />
              </Field>
              <Field label="Default TV view">
                <Select
                  value={formState.defaultShowView}
                  onChange={(value) => setFormState((current) => ({ ...current, defaultShowView: value }))}
                  options={[
                    { label: "Grid", value: "grid" },
                    { label: "List", value: "list" }
                  ]}
                />
              </Field>

              <Button type="submit" disabled={busy}>
                {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Save interface settings
              </Button>
            </form>
          </CardContent>
        </Card>

        <div className="settings-side-stack">
          <Card>
            <CardHeader>
              <CardTitle>Current defaults</CardTitle>
              <CardDescription>
                What Deluno will treat as the preferred interface posture for this install.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.7)]">
              <StatRow label="Theme" value={settings.uiTheme} />
              <StatRow label="Density" value={densityDisplayName(settings.uiDensity)} />
              <StatRow label="Movies view" value={settings.defaultMovieView} />
              <StatRow label="TV view" value={settings.defaultShowView} />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>What changes with density</CardTitle>
            <CardDescription>
                Density now affects canvas width, typography scale, card scale, controls, and table rhythm, not just row height.
            </CardDescription>
          </CardHeader>
          <CardContent className="density-help space-y-[calc(var(--field-group-pad)*0.7)] text-muted-foreground">
              <PreviewRow title="Compact">
                Tighter tables, smaller controls, and less padding for maximum information on-screen.
              </PreviewRow>
              <PreviewRow title="Standard">
                Default spacing for general use across mixed monitor sizes.
              </PreviewRow>
              <PreviewRow title="Spacious">
                More breathing room, larger controls, and a wider workspace for bigger monitors where the interface can feel cramped.
              </PreviewRow>
              <PreviewRow title="Expanded">
                The most screen-filling preset. Best when you want Deluno to feel larger and more operational on 1440p or ultrawide displays.
              </PreviewRow>
              <div className="density-field rounded-xl border border-hairline bg-surface-1">
                <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">Recommended</p>
                <p className="density-help mt-2 text-foreground">
                  If Deluno feels too small on a 27-inch 1440p monitor, start with <span className="font-medium">Spacious</span>.
                  If you still want it to claim more of the screen, use <span className="font-medium">Expanded</span>.
                </p>
              </div>
          </CardContent>
        </Card>
        </div>
      </div>
    </SettingsShell>
  );
}

export const settingsUiLoader = settingsOverviewLoader;

function Field({ children, label }: { children: ReactNode; label: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
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

function StatRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <p className="density-control-text mt-2 text-foreground">{value}</p>
    </div>
  );
}

function PreviewRow({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="font-medium text-foreground">{title}</p>
      <p className="mt-1">{children}</p>
    </div>
  );
}
