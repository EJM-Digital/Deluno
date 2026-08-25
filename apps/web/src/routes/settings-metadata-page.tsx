/**
 * Metadata & files — a page-level form on the shared grammar.
 *
 *   PageToolbar (Media Management tabs)
 *   ListCard  metadata files      (page form: language, region, optional files)
 *   PageFooter (pinned: status · Save)
 *
 * Global metadata checks and refresh jobs live under System. This page only
 * stores the metadata files and regional preferences used by the library.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Field, FieldRow } from "../components/ui/field";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { SwitchRow } from "../components/ui/switch";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import type { DrawerSaveState } from "../components/ui/drawer";
import {
  fetchJson,
  type PlatformSettingsSnapshot
} from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";

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

interface LoaderData {
  settings: PlatformSettingsSnapshot;
}

export async function settingsMetadataLoader(): Promise<LoaderData> {
  return { settings: await fetchJson<PlatformSettingsSnapshot>("/api/settings") };
}

interface MetadataForm {
  certificationCountry: string;
  language: string;
  nfoEnabled: boolean;
  artworkEnabled: boolean;
}

export function SettingsMetadataPage() {
  const { settings } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");

  const [savedForm, setSavedForm] = useState<MetadataForm>(() => formFrom(settings));
  const [form, setForm] = useState<MetadataForm>(savedForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

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
      await settingsMutation.mutate({
        metadataCertificationCountry: form.certificationCountry,
        metadataLanguage: form.language,
        metadataNfoEnabled: form.nfoEnabled,
        metadataArtworkEnabled: form.artworkEnabled
      });
      setSavedForm(form);
      setSaveState("saved");
      setMessage("Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={librarySetupNavItems} accent="yellow" />

      <ListCard title="Metadata files" count="Optional information stored beside your media">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <FieldRow>
            <Field label="Details language" help="Preferred language for titles, descriptions, and release information." error={settingsMutation.fieldErrors.metadataLanguage}>
              <PresetField
                value={form.language}
                onChange={(value) => setForm((current) => ({ ...current, language: value }))}
                options={LANGUAGE_OPTIONS}
                customLabel="Other language code"
                customPlaceholder="IETF language tag, e.g. pt-BR"
              />
            </Field>
            <Field label="Ratings region" help="Country used for certification ratings and rating filters." error={settingsMutation.fieldErrors.metadataCertificationCountry}>
              <PresetField
                value={form.certificationCountry}
                onChange={(value) => setForm((current) => ({ ...current, certificationCountry: value }))}
                options={COUNTRY_OPTIONS}
                customLabel="Other country code"
                customPlaceholder="ISO country code, e.g. NL"
              />
            </Field>
          </FieldRow>
          <FieldRow>
            <SwitchRow
              label="Save portable metadata files (.nfo)"
              description="Keep a small .nfo record in each media folder so other players and tools can read the details."
              checked={form.nfoEnabled}
              onCheckedChange={(checked) => setForm((current) => ({ ...current, nfoEnabled: checked }))}
            />
            <SwitchRow
              label="Save artwork files"
              description="Keep poster and backdrop images next to your media so they remain available outside Deluno."
              checked={form.artworkEnabled}
              onCheckedChange={(checked) => setForm((current) => ({ ...current, artworkEnabled: checked }))}
              className="sm:border-l sm:border-hairline sm:pl-[var(--grid-gap)]"
            />
          </FieldRow>
        </div>
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save metadata settings" />
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
