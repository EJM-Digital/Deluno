/**
 * General — a page-level form on the shared grammar.
 *
 *   PageToolbar (System settings tabs)
 *   ListCard  instance and host (page form)
 *   PageFooter (pinned: status · Save)
 *
 * Contracts: PATCH /api/settings.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData } from "react-router-dom";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { HowThisWorks } from "../components/app/how-this-works";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import type { DrawerSaveState } from "../components/ui/drawer";
import { settingsOverviewLoader } from "./settings-overview-page";
import type { LibraryItem, PlatformSettingsSnapshot, QualityProfileItem } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsGeneralLoader = settingsOverviewLoader;

interface GeneralForm {
  appInstanceName: string;
  hostBindAddress: string;
  hostPort: string;
  urlBase: string;
}

export function SettingsGeneralPage() {
  const { settings } = useLoaderData() as LoaderData;
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");

  const [savedForm, setSavedForm] = useState<GeneralForm>(() => formFrom(settings));
  const [form, setForm] = useState<GeneralForm>(savedForm);
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

  /** Address and port only take effect on restart, so say so rather than imply a live change. */
  const hostChanged = form.hostBindAddress !== savedForm.hostBindAddress || form.hostPort !== savedForm.hostPort;

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (state === "saving") return;
    setSaveState("saving");
    try {
      await settingsMutation.mutate({
        appInstanceName: form.appInstanceName.trim(),
        hostBindAddress: form.hostBindAddress.trim(),
        hostPort: Number(form.hostPort || 5099),
        urlBase: form.urlBase.trim()
      });
      setSavedForm(form);
      setSaveState("saved");
      setMessage(hostChanged ? "Saved — restart Deluno to move to the new address" : "Saved just now");
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={systemSettingsNavItems} />

      <HowThisWorks
        id="preferences"
        lead="How this installation behaves, rather than what it manages. Nothing on these tabs changes a library or a title — they change Deluno itself: what it calls itself and where it listens, how it looks, who it tells when something happens, and how to bring settings across from the app you used before."
        steps={[]}
      />

      <ListCard title="Instance and host" count="How this installation names and serves itself">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Instance name" help="Shown in the topbar and in notifications, so you can tell two installations apart." error={settingsMutation.fieldErrors.appInstanceName}>
            <Input
              value={form.appInstanceName}
              onChange={(event) => setForm((current) => ({ ...current, appInstanceName: event.target.value }))}
              placeholder="Home server"
              className="max-w-[24rem]"
            />
          </Field>
          <FieldRow>
            <Field label="Bind address" help="Use the local address unless you need to reach Deluno from another machine." error={settingsMutation.fieldErrors.hostBindAddress}>
              <PresetField
                value={form.hostBindAddress}
                onChange={(value) => setForm((current) => ({ ...current, hostBindAddress: value }))}
                options={[
                  { label: "This machine only (127.0.0.1)", value: "127.0.0.1" },
                  { label: "Every network interface (0.0.0.0)", value: "0.0.0.0" },
                  { label: "IPv6 localhost (::1)", value: "::1" }
                ]}
                customLabel="Custom bind address"
                customPlaceholder="IP address or hostname"
              />
            </Field>
            <Field label="Port" help="Must not clash with another service on this machine." error={settingsMutation.fieldErrors.hostPort}>
              <PresetField
                inputType="number"
                value={form.hostPort}
                onChange={(value) => setForm((current) => ({ ...current, hostPort: value }))}
                options={[
                  { label: "Deluno default (5099)", value: "5099" },
                  { label: "Radarr-style (7878)", value: "7878" },
                  { label: "Sonarr-style (8989)", value: "8989" },
                  { label: "9696", value: "9696" }
                ]}
                customLabel="Custom port"
                customPlaceholder="Port number"
              />
            </Field>
          </FieldRow>
          <Field label="URL base" optional help="Path prefix when Deluno sits behind a reverse proxy. Leave empty when it serves from the root." error={settingsMutation.fieldErrors.urlBase}>
            <PresetField
              value={form.urlBase}
              onChange={(value) => setForm((current) => ({ ...current, urlBase: value }))}
              options={[
                { label: "None — serve at /", value: "" },
                { label: "/deluno", value: "/deluno" },
                { label: "/media", value: "/media" }
              ]}
              customLabel="Custom URL base"
              customPlaceholder="/my-deluno"
            />
          </Field>
          {hostChanged ? (
            <p className="text-[length:var(--type-caption)] text-warning">
              Changing the address or port takes effect the next time Deluno starts. The page you are on now stays on the old one until then.
            </p>
          ) : null}
        </div>
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save general settings" />
    </form>
  );
}

/* ---------------------------------------------------------------- bits */

function same<T>(a: T, b: T) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function formFrom(settings: PlatformSettingsSnapshot): GeneralForm {
  return {
    appInstanceName: settings.appInstanceName,
    hostBindAddress: settings.hostBindAddress,
    hostPort: String(settings.hostPort),
    urlBase: settings.urlBase
  };
}
