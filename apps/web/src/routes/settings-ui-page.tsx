/**
 * Interface — a page-level form on the shared grammar.
 *
 *   PageToolbar (System settings tabs)
 *   SummaryStrip (theme · density · movies view · TV view)
 *   ListCard  appearance    (theme, density — density applies live)
 *   ListCard  default views (movies, TV)
 *   PageFooter (pinned: status · Discard · Save)
 *
 * Contracts: PUT /api/settings.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Field } from "../components/ui/field";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { SummaryStrip } from "../components/ui/summary-strip";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { densityDisplayName, isDensity, useDensity, type Density } from "../lib/use-density";
import type { DrawerSaveState } from "../components/ui/drawer";
import { settingsOverviewLoader } from "./settings-overview-page";
import type { LibraryItem, PlatformSettingsSnapshot, QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/** One line each — the four presets differ by degree, so a paragraph apiece said nothing. */
const DENSITY_OPTIONS: { value: Density; label: string; help: string }[] = [
  { value: "compact", label: "Compact", help: "Tighter rows and smaller type — the most information per screen. Suits laptops." },
  { value: "comfortable", label: "Standard", help: "Readable and efficient. The right choice on most displays." },
  { value: "spacious", label: "Spacious", help: "Larger type and controls, wider canvas. Start here if Deluno feels small on a 27-inch 1440p monitor." },
  { value: "expanded", label: "Expanded", help: "The most screen-filling preset, for ultrawide displays and long sessions." }
];

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsUiLoader = settingsOverviewLoader;

interface UiForm {
  theme: string;
  density: string;
  movieView: string;
  showView: string;
}

export function SettingsUiPage() {
  const { settings } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const { density, setDensity } = useDensity();

  const [savedForm, setSavedForm] = useState<UiForm>(() => formFrom(settings));
  const [form, setForm] = useState<UiForm>(savedForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

  // Density applies as you pick it, so you can see whether it suits the screen
  // before committing. Discard puts it back.
  useEffect(() => {
    if (isDensity(form.density) && density !== form.density) setDensity(form.density as Density);
  }, [form.density, density, setDensity]);

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
    if (state === "saving") return;
    setSaveState("saving");
    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          ...settings,
          uiTheme: form.theme,
          uiDensity: form.density,
          defaultMovieView: form.movieView,
          defaultShowView: form.showView
        })
      });
      if (!response.ok) throw new Error("Interface settings could not be saved.");
      setSavedForm(form);
      setSaveState("saved");
      setMessage("Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  const densityHelp = DENSITY_OPTIONS.find((option) => option.value === form.density)?.help;

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={systemSettingsNavItems} />

      <SummaryStrip
        cells={[
          { label: "Theme", value: titleCase(settings.uiTheme), help: settings.uiTheme === "system" ? "follows your device" : "always this way" },
          { label: "Density", value: densityDisplayName(settings.uiDensity), help: "spacing, type and control scale" },
          { label: "Movies open as", value: titleCase(settings.defaultMovieView), help: "for new library views" },
          { label: "TV opens as", value: titleCase(settings.defaultShowView), help: "for new library views" }
        ]}
      />

      <ListCard title="Appearance" count="Density applies as you pick it, so you can see it before you save">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Theme" help="Follow the device, or pin Deluno to dark or light." className="max-w-[24rem]">
            <SegmentedControl<string>
              aria-label="Theme"
              value={form.theme}
              onValueChange={(value) => setForm((current) => ({ ...current, theme: value }))}
              options={[
                { value: "system", label: "System" },
                { value: "dark", label: "Dark" },
                { value: "light", label: "Light" }
              ]}
            />
          </Field>
          <Field label="Density" help={densityHelp}>
            <SegmentedControl<string>
              aria-label="Density"
              value={form.density}
              onValueChange={(value) => setForm((current) => ({ ...current, density: value }))}
              options={DENSITY_OPTIONS.map((option) => ({ value: option.value, label: option.label }))}
            />
          </Field>
        </div>
      </ListCard>

      <ListCard title="Default views" count="How a library opens before you change its view">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Movies" help="Grid leads with poster art; List leads with details." className="max-w-[24rem]">
            <SegmentedControl<string>
              aria-label="Default movies view"
              value={form.movieView}
              onValueChange={(value) => setForm((current) => ({ ...current, movieView: value }))}
              options={[
                { value: "grid", label: "Grid" },
                { value: "list", label: "List" }
              ]}
            />
          </Field>
          <Field label="TV shows" help="Applies to new library views, not ones you have already switched." className="max-w-[24rem]">
            <SegmentedControl<string>
              aria-label="Default TV view"
              value={form.showView}
              onValueChange={(value) => setForm((current) => ({ ...current, showView: value }))}
              options={[
                { value: "grid", label: "Grid" },
                { value: "list", label: "List" }
              ]}
            />
          </Field>
        </div>
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save interface settings" onDiscard={() => setForm(savedForm)} />
    </form>
  );
}

/* ---------------------------------------------------------------- bits */

function same<T>(a: T, b: T) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function formFrom(settings: PlatformSettingsSnapshot): UiForm {
  return {
    theme: settings.uiTheme,
    density: settings.uiDensity,
    movieView: settings.defaultMovieView,
    showView: settings.defaultShowView
  };
}

function titleCase(value: string) {
  return value ? value.charAt(0).toUpperCase() + value.slice(1) : "—";
}
