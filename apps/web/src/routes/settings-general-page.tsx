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
import { Field } from "../components/ui/field";
import { Loader2, Wifi } from "lucide-react";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import type { DrawerSaveState } from "../components/ui/drawer";
import { settingsOverviewLoader } from "./settings-overview-page";
import { fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import type { LanAccessStatus } from "../components/app/lan-access-notice";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { toast } from "../components/shell/toaster";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsGeneralLoader = settingsOverviewLoader;

interface GeneralForm {
  appInstanceName: string;
  cloudflareHelperUrl: string;
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

  const state: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const [helperResult, setHelperResult] = useState<{ healthy: boolean; message: string } | null>(null);
  const [helperBusy, setHelperBusy] = useState(false);

  /** Whether the helper answers at the address typed, before it is saved (#628). */
  async function testHelper() {
    setHelperBusy(true);
    try {
      setHelperResult(await fetchJson<{ healthy: boolean; message: string }>("/api/trackers/cloudflare-helper/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url: form.cloudflareHelperUrl.trim() })
      }));
    } catch (error) {
      setHelperResult({ healthy: false, message: error instanceof Error ? error.message : "The helper could not be checked." });
    } finally {
      setHelperBusy(false);
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (state === "saving") return;
    setSaveState("saving");
    try {
      await settingsMutation.mutate({
        appInstanceName: form.appInstanceName.trim(),
        cloudflareHelperUrl: form.cloudflareHelperUrl.trim()
      });
      setSavedForm(form);
      setSaveState("saved");
      setMessage("Saved just now");
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar tabs={systemSettingsNavItems} />


      <ListCard title="Instance" count="How this installation names itself">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Instance name" help="Shown in the topbar and in notifications, so you can tell two installations apart." error={settingsMutation.fieldErrors.appInstanceName}>
            <Input
              value={form.appInstanceName}
              onChange={(event) => setForm((current) => ({ ...current, appInstanceName: event.target.value }))}
              placeholder="Home server"
              className="max-w-[24rem]"
            />
          </Field>
        </div>
      </ListCard>

      <ReachabilityCard />

      <LogLevelCard />

      <ListCard title="Cloudflare helper" count="For trackers behind a Cloudflare check">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field label="Helper address" optional help="Some trackers, such as 1337x, put a Cloudflare check in front of every page. Deluno does not solve those checks itself: run FlareSolverr or Byparr and give its address here, and Deluno asks it for a page only when a tracker is behind a check." error={settingsMutation.fieldErrors.cloudflareHelperUrl}>
            <Input value={form.cloudflareHelperUrl} onChange={(event) => { setHelperResult(null); setForm((current) => ({ ...current, cloudflareHelperUrl: event.target.value })); }} placeholder="http://127.0.0.1:8191" className="font-mono" autoComplete="off" spellCheck={false} />
          </Field>
          <div className="flex flex-wrap items-center gap-3">
            <Button type="button" variant="outline" size="sm" onClick={() => void testHelper()} disabled={helperBusy || !form.cloudflareHelperUrl.trim()}>
              {helperBusy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
              Test helper
            </Button>
            {helperResult ? <span role="status" className={helperResult.healthy ? "text-success" : "text-destructive"}>{helperResult.message}</span> : null}
          </div>
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
    cloudflareHelperUrl: settings.cloudflareHelperUrl ?? ""
  };
}

/**
 * Bind address, port and URL base used to sit here as editable fields that
 * PATCHed host.bindAddress / host.port / host.urlBase, and nothing ever read
 * them back: Kestrel binds to `Server:Port` / `Server:AllowLan` from
 * configuration in Deluno.Host/Program.cs, and there is no `UsePathBase` for
 * a URL base anywhere (#736). Editing them changed a value nobody consulted
 * while implying the opposite.
 *
 * This reports the same two facts honestly instead: the effective port and
 * whether other devices on the network can reach Deluno, both read from
 * `/api/system/lan-access` — the same source System's own reachability
 * notice uses — and says in plain words that changing either happens outside
 * Deluno, not on this page.
 */
function ReachabilityCard() {
  const [status, setStatus] = useState<LanAccessStatus | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);

  useEffect(() => {
    let active = true;
    void fetchJson<LanAccessStatus>("/api/system/lan-access")
      .then((next) => { if (active) setStatus(next); })
      .catch(() => { if (active) setLoadFailed(true); });
    return () => { active = false; };
  }, []);

  const reachabilityLabel =
    status?.state === "open" ? "Yes" : status?.state === "blocked" ? "No" : "Unknown from here";

  return (
    <ListCard title="How Deluno is reached" count="Read-only — set outside the app">
      <ListTable columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,1.4fr)", align: "end" }]} chevron={false}>
        <ListRow>
          <ListNameCell name="Port" />
          <ListCell numeric align="end" primary={status ? String(status.port ?? "—") : loadFailed ? "Unavailable" : "Loading…"} />
        </ListRow>
        <ListRow>
          <ListNameCell name="Other devices on your network can connect" sub={status?.message} />
          <ListCell numeric align="end" primary={status ? reachabilityLabel : loadFailed ? "Unavailable" : "Loading…"} />
        </ListRow>
      </ListTable>
      <p className="p-[var(--card-pad-x)] pt-0 text-[length:var(--type-caption)] text-muted-foreground">
        The port and LAN access are set where Deluno starts, not in this page: the <code>Server:Port</code> and{" "}
        <code>Server:AllowLan</code> environment variables (<code>Server__Port</code> / <code>Server__AllowLan</code> in
        Docker Compose), or the equivalent command-line arguments for a manual run. Change either there and restart
        Deluno.
      </p>
    </ListCard>
  );
}

const LOG_LEVEL_OPTIONS = [
  { value: "Trace", label: "Trace" },
  { value: "Debug", label: "Debug" },
  { value: "Information", label: "Info" },
  { value: "Warning", label: "Warning" },
  { value: "Error", label: "Error" }
];

/**
 * A runtime-configurable Log Level (#866): Deluno's own log file used to
 * take its level only from appsettings.json on disk, so changing it meant
 * stopping the app, editing a file, and restarting. This reads and writes
 * /api/system/log-level, which DelunoFileLogger consults for every new log
 * line without a restart.
 */
function LogLevelCard() {
  const [level, setLevel] = useState<string | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let active = true;
    void fetchJson<{ level: string }>("/api/system/log-level")
      .then((next) => { if (active) setLevel(next.level); })
      .catch(() => { if (active) setLoadFailed(true); });
    return () => { active = false; };
  }, []);

  async function changeLevel(next: string) {
    const previous = level;
    setLevel(next);
    setBusy(true);
    try {
      const response = await authedFetch("/api/system/log-level", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ level: next })
      });
      if (!response.ok) throw new Error("The log level could not be changed.");
      toast.success(`Log level set to ${next} — takes effect immediately.`);
    } catch (error) {
      setLevel(previous);
      toast.error(error instanceof Error ? error.message : "The log level could not be changed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <ListCard title="Log level" count="Changes what Deluno's own log file records, right away">
      <ListTable columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,1.4fr)", align: "end" }]} chevron={false}>
        <ListRow>
          <ListNameCell name="Current effective level" sub="Takes effect for new log lines immediately, and persists across restarts." />
          <ListCell numeric align="end" primary={
            level ? (
              <Select
                value={level}
                onChange={(event) => void changeLevel(event.target.value)}
                options={LOG_LEVEL_OPTIONS}
                disabled={busy}
                aria-label="Log level"
              />
            ) : loadFailed ? "Unavailable" : "Loading…"
          } />
        </ListRow>
      </ListTable>
    </ListCard>
  );
}
