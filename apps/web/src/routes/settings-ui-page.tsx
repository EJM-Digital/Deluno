/**
 * Interface — a page-level form on the shared grammar.
 *
 *   PageToolbar (System settings tabs)
 *   ListCard  appearance    (theme, density — density applies live)
 *   ListCard  default views (movies, TV)
 *   PageFooter (pinned: status · Save)
 *
 * Contracts: PATCH /api/settings.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData } from "react-router-dom";
import { Field } from "../components/ui/field";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { isDensity, useDensity, type Density } from "../lib/use-density";
import type { DrawerSaveState } from "../components/ui/drawer";
import { settingsOverviewLoader } from "./settings-overview-page";
import type { LibraryItem, PlatformSettingsSnapshot, QualityProfileItem } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";

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
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const { density, setDensity } = useDensity();

  const [savedForm, setSavedForm] = useState<UiForm>(() => formFrom(settings));
  const [form, setForm] = useState<UiForm>(savedForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

  // Density applies as you pick it, so you can see whether it suits the screen
  // before committing. Navigation discard is handled by the shared modal.
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
      await settingsMutation.mutate({
        uiTheme: form.theme,
        uiDensity: form.density,
        defaultMovieView: form.movieView,
        defaultShowView: form.showView
      });
      setSavedForm(form);
      setSaveState("saved");
      setMessage("Saved just now");
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  const densityHelp = DENSITY_OPTIONS.find((option) => option.value === form.density)?.help;

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={systemSettingsNavItems} accent="blue" />

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

      <PageFooter state={state} message={message} saveLabel="Save interface settings" />
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
