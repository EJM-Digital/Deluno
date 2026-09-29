/**
 * Metadata & files — a page-level form on the shared grammar.
 *
 *   PageToolbar (Media Management tabs)
 *   ListCard  metadata files      (page form: language, region, optional files)
 *   PageFooter (pinned: status · Save)
 *
 * Global metadata checks and refresh jobs live under System. This page only
 * stores the language and ratings region used for title details. Both reach
 * TMDb on the direct path only; Deluno's managed metadata service takes
 * neither (docs/metadata-broker-contract.md), so the page says so when that
 * service is in use rather than implying they apply (#736).
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Field, FieldRow } from "../components/ui/field";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import type { DrawerSaveState } from "../components/ui/drawer";
import {
  fetchJson,
  type PlatformSettingsSnapshot
} from "../lib/api";
import type { MetadataProviderStatus } from "../lib/api/types/metadata";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

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
  metadataStatus: MetadataProviderStatus | null;
}

export async function settingsMetadataLoader(): Promise<LoaderData> {
  const [settings, metadataStatus] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null)
  ]);
  return { settings, metadataStatus };
}

/** Status modes in which lookups go through Deluno's managed service, which takes no language or region. */
const MANAGED_METADATA_MODES = new Set(["broker", "hybrid"]);

/** True only when the status positively says the managed service answers lookups; an unknown status claims nothing. */
export function metadataPreferencesIgnored(status: MetadataProviderStatus | null): boolean {
  return status !== null && MANAGED_METADATA_MODES.has(status.mode);
}

interface MetadataForm {
  certificationCountry: string;
  language: string;
}

export function SettingsMetadataPage() {
  const { settings, metadataStatus } = useLoaderData() as LoaderData;
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

  const state: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saveState === "saving") return;
    setSaveState("saving");
    try {
      await settingsMutation.mutate({
        metadataCertificationCountry: form.certificationCountry,
        metadataLanguage: form.language
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
      <PageToolbar tabs={librarySetupNavItems} />

      {/* No .nfo or artwork switches here: Deluno writes neither file (#735). Offer them again only with a writer behind them. */}
      <ListCard title="Title details" count="Language and ratings region">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <FieldRow>
            <Field label="Details language" help="Titles, descriptions and taglines are shown in this language when a translation exists. The original title is always kept, and searches still match every name a title is known by. English leaves everything as it is." error={settingsMutation.fieldErrors.metadataLanguage}>
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
          {metadataPreferencesIgnored(metadataStatus) && (
            <p className="text-[length:var(--type-body-sm)] text-muted-foreground">
              The ratings region applies when Deluno looks titles up on TMDb directly. This install uses Deluno's managed
              metadata service, which does not take a region, so changing it has no effect here. The language does apply.
            </p>
          )}
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
    language: settings.metadataLanguage
  };
}
