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
import { Field, FieldRow } from "../components/ui/field";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Switch } from "../components/ui/switch";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { isDensity, useDensity, type Density } from "../lib/use-density";
import type { DrawerSaveState } from "../components/ui/drawer";
import { settingsOverviewLoader } from "./settings-overview-page";
import type { LibraryItem, PlatformSettingsSnapshot, QualityProfileItem } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { isColorMode, useColorMode, type ColorMode } from "../lib/use-color-mode";
import { useDisplayPreferences } from "../lib/display-preferences";

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
  uiLanguage: string;
  calendarFirstDayOfWeek: string;
  calendarWeekHeaderFormat: string;
  runtimeFormat: string;
  shortDateFormat: string;
  longDateFormat: string;
  timeFormat: string;
  showRelativeDates: boolean;
}

export function SettingsUiPage() {
  const { settings } = useLoaderData() as LoaderData;
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const { density, setDensity } = useDensity();
  const { setColorMode } = useColorMode();
  const { replaceFromSettings } = useDisplayPreferences();
  const initialColorMode: ColorMode = isColorMode(settings.uiColorMode) ? settings.uiColorMode : "standard";

  const [savedForm, setSavedForm] = useState<UiForm>(() => formFrom(settings));
  const [form, setForm] = useState<UiForm>(savedForm);
  const [savedColorMode, setSavedColorMode] = useState<ColorMode>(initialColorMode);
  const [colorModeDraft, setColorModeDraft] = useState<ColorMode>(initialColorMode);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

  // Density applies as you pick it, so you can see whether it suits the screen
  // before committing. Navigation discard is handled by the shared modal.
  useEffect(() => {
    if (isDensity(form.density) && density !== form.density) setDensity(form.density as Density);
  }, [form.density, density, setDensity]);

  const dirty = !same(form, savedForm) || colorModeDraft !== savedColorMode;
  const settingsForm = useMemo(() => formFrom(settings), [settings]);
  const settingsColorMode: ColorMode = isColorMode(settings.uiColorMode) ? settings.uiColorMode : "standard";
  useEffect(() => {
    if (dirty || (same(savedForm, settingsForm) && savedColorMode === settingsColorMode)) return;
    setSavedForm(settingsForm);
    setForm(settingsForm);
    setSavedColorMode(settingsColorMode);
    setColorModeDraft(settingsColorMode);
    setColorMode(settingsColorMode);
  }, [dirty, savedColorMode, settingsColorMode, savedForm, settingsForm, setColorMode]);

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
      const updated = await settingsMutation.mutate({
        uiTheme: form.theme,
        uiDensity: form.density,
        defaultMovieView: form.movieView,
        defaultShowView: form.showView,
        uiColorMode: colorModeDraft,
        uiLanguage: form.uiLanguage,
        calendarFirstDayOfWeek: form.calendarFirstDayOfWeek,
        calendarWeekHeaderFormat: form.calendarWeekHeaderFormat,
        runtimeFormat: form.runtimeFormat,
        shortDateFormat: form.shortDateFormat,
        longDateFormat: form.longDateFormat,
        timeFormat: form.timeFormat,
        showRelativeDates: form.showRelativeDates
      });
      setSavedForm(form);
      setSavedColorMode(colorModeDraft);
      setColorMode(colorModeDraft);
      replaceFromSettings(updated);
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
      <PageToolbar tabs={systemSettingsNavItems} />

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
          <Field label="Colour-impaired mode" help="Keeps the mark glyphs and uses a palette with stronger lightness separation. The glyphs remain on in every mode.">
            <SegmentedControl<string>
              aria-label="Colour-impaired mode"
              value={colorModeDraft}
              onValueChange={(value) => {
                if (!isColorMode(value)) return;
                setColorModeDraft(value);
                setColorMode(value);
              }}
              options={[
                { value: "standard", label: "Standard" },
                { value: "impaired", label: "Colour-impaired" }
              ]}
            />
          </Field>
          <Field label="Interface language" help="Controls Deluno's labels and display formatting. Metadata language is configured separately under Media management." className="max-w-[30rem]">
            <SegmentedControl<string>
              aria-label="Interface language"
              value={form.uiLanguage}
              onValueChange={(value) => setForm((current) => ({ ...current, uiLanguage: value }))}
              options={[
                { value: "en-AU", label: "English (Australia)" },
                { value: "en-US", label: "English (United States)" }
              ]}
            />
          </Field>
        </div>
      </ListCard>

      <ListCard title="Calendar" count="Choose how dates and week columns read across Deluno">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <FieldRow>
            <Field label="First day of week" help="Changes calendar column order and week grouping.">
              <SegmentedControl<string>
                aria-label="First day of week"
                value={form.calendarFirstDayOfWeek}
                onValueChange={(value) => setForm((current) => ({ ...current, calendarFirstDayOfWeek: value }))}
                options={[
                  { value: "monday", label: "Monday" },
                  { value: "sunday", label: "Sunday" }
                ]}
              />
            </Field>
            <Field label="Week column headers" help="The compact date format used above calendar columns.">
              <SegmentedControl<string>
                aria-label="Week column headers"
                value={form.calendarWeekHeaderFormat}
                onValueChange={(value) => setForm((current) => ({ ...current, calendarWeekHeaderFormat: value }))}
                options={[
                  { value: "ddd d/M", label: "Tue 25/03" },
                  { value: "ddd m/d", label: "Tue 03/25" },
                  { value: "ddd d mmm", label: "Tue 25 Mar" }
                ]}
              />
            </Field>
          </FieldRow>
        </div>
      </ListCard>

      <ListCard title="Formats" count="The same preferences are used in lists, details, activity and the dashboard">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <FieldRow>
            <Field label="Runtime" help="How movie and episode runtimes are written.">
              <SegmentedControl<string>
                aria-label="Runtime format"
                value={form.runtimeFormat}
                onValueChange={(value) => setForm((current) => ({ ...current, runtimeFormat: value }))}
                options={[
                  { value: "hoursMinutes", label: "1h 15m" },
                  { value: "minutes", label: "75 minutes" }
                ]}
              />
            </Field>
            <Field label="Time" help="The clock style used with dates and activity timestamps.">
              <SegmentedControl<string>
                aria-label="Time format"
                value={form.timeFormat}
                onValueChange={(value) => setForm((current) => ({ ...current, timeFormat: value }))}
                options={[
                  { value: "12", label: "12-hour" },
                  { value: "24", label: "24-hour" }
                ]}
              />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Short date" help="Used for compact list and activity dates.">
              <SegmentedControl<string>
                aria-label="Short date format"
                value={form.shortDateFormat}
                onValueChange={(value) => setForm((current) => ({ ...current, shortDateFormat: value }))}
                options={[
                  { value: "dmy", label: "25/03/2026" },
                  { value: "mdy", label: "03/25/2026" },
                  { value: "iso", label: "2026-03-25" }
                ]}
              />
            </Field>
            <Field label="Long date" help="Used where a date needs more context.">
              <SegmentedControl<string>
                aria-label="Long date format"
                value={form.longDateFormat}
                onValueChange={(value) => setForm((current) => ({ ...current, longDateFormat: value }))}
                options={[
                  { value: "full", label: "Wednesday, 25 March" },
                  { value: "mdy", label: "Wednesday, March 25" }
                ]}
              />
            </Field>
          </FieldRow>
          <Field label="Relative dates" help="Show Today, Yesterday and Tomorrow when the exact date is not needed.">
            <div className="flex min-h-[var(--control-height)] items-center gap-3">
              <Switch
                checked={form.showRelativeDates}
                onCheckedChange={(checked) => setForm((current) => ({ ...current, showRelativeDates: checked }))}
              />
              <span className="text-[length:var(--type-body-sm)] text-muted-foreground">Use relative labels where they improve scanning</span>
            </div>
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
    showView: settings.defaultShowView,
    uiLanguage: settings.uiLanguage,
    calendarFirstDayOfWeek: settings.calendarFirstDayOfWeek,
    calendarWeekHeaderFormat: settings.calendarWeekHeaderFormat,
    runtimeFormat: settings.runtimeFormat,
    shortDateFormat: settings.shortDateFormat,
    longDateFormat: settings.longDateFormat,
    timeFormat: settings.timeFormat,
    showRelativeDates: settings.showRelativeDates
  };
}
