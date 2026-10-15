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
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { localizeResetTimes } from "../lib/reset-times";
import { Field } from "../components/ui/field";
import { Loader2, RefreshCw, Wifi } from "lucide-react";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { Switch } from "../components/ui/switch";
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
import { formatDateTime, useDisplayPreferences } from "../lib/display-preferences";

import { SignedApiLink } from "../components/app/signed-api-link";
interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsGeneralLoader = settingsOverviewLoader;

interface GeneralForm {
  cloudflareHelperUrl: string;
  cloudflareHelperTimeoutSeconds: string;
  cloudflareHelperKind: string;
}

/** #1029: Byparr answers FlareSolverr's own API but silently ignores sessions, a POST body and a per-request proxy; "auto" has Deluno tell them apart itself. */
const cloudflareHelperKindOptions = [
  { value: "auto", label: "Auto-detect" },
  { value: "flaresolverr", label: "FlareSolverr" },
  { value: "byparr", label: "Byparr" }
];

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["SettingsChanged"] };

export function SettingsGeneralPage() {
  useLiveRefresh(SETTINGS_REFRESH);
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
        cloudflareHelperUrl: form.cloudflareHelperUrl.trim(),
        cloudflareHelperTimeoutSeconds: form.cloudflareHelperTimeoutSeconds.trim() ? Number(form.cloudflareHelperTimeoutSeconds) : 60,
        cloudflareHelperKind: form.cloudflareHelperKind
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
    <form onSubmit={handleSubmit} className="flex flex-col gap-(--page-gap)" noValidate>
      <PageToolbar tabs={systemSettingsNavItems} />


      <ReachabilityCard />

      <LogLevelCard />

      <ConnectionChecksCard settings={settings} />

      <ListCard title="Cloudflare helper" count="For trackers behind a Cloudflare check">
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <Field label="Helper address" optional help="Some trackers, such as 1337x, put a Cloudflare check in front of every page. Deluno does not solve those checks itself: run FlareSolverr or Byparr and give its address here, and Deluno asks it for a page only when a tracker is behind a check. This one helper covers every tracker. A single indexer can have its own under Find & Download, Proxies; that one wins for that indexer." error={settingsMutation.fieldErrors.cloudflareHelperUrl}>
            <Input value={form.cloudflareHelperUrl} onChange={(event) => { setHelperResult(null); setForm((current) => ({ ...current, cloudflareHelperUrl: event.target.value })); }} placeholder="http://127.0.0.1:8191" className="font-mono" autoComplete="off" spellCheck={false} />
          </Field>
          <Field label="Helper timeout" optional help="How long Deluno waits for the helper to solve a check before giving up, in seconds (1-180)." error={settingsMutation.fieldErrors.cloudflareHelperTimeoutSeconds}>
            <div className="flex items-center gap-2">
              <Input type="number" min="1" max="180" step="1" value={form.cloudflareHelperTimeoutSeconds} onChange={(event) => setForm((current) => ({ ...current, cloudflareHelperTimeoutSeconds: event.target.value }))} placeholder="60" className="max-w-32" />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">seconds</span>
            </div>
          </Field>
          <Field label="Helper kind" optional help="FlareSolverr and Byparr answer almost the same way, but Byparr cannot send a POST or route through a proxy -- it stays quiet about it instead of refusing. Leave this on Auto-detect unless Deluno guesses wrong." error={settingsMutation.fieldErrors.cloudflareHelperKind}>
            <Select value={form.cloudflareHelperKind} onChange={(event) => setForm((current) => ({ ...current, cloudflareHelperKind: event.target.value }))} options={cloudflareHelperKindOptions} className="max-w-48" />
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

      <TrackerDefinitionsCard />

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
    cloudflareHelperUrl: settings.cloudflareHelperUrl ?? "",
    cloudflareHelperTimeoutSeconds: String(settings.cloudflareHelperTimeoutSeconds ?? 60),
    cloudflareHelperKind: settings.cloudflareHelperKind ?? "auto"
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
      <p className="p-(--card-pad-x) pt-0 text-(length:--type-caption) text-muted-foreground">
        The port and LAN access are set where Deluno starts, not in this page: the <code>Server:Port</code> and{" "}
        <code>Server:AllowLan</code> environment variables (<code>Server__Port</code> / <code>Server__AllowLan</code> in
        Docker Compose), or the equivalent command-line arguments for a manual run. Change either there and restart
        Deluno.
      </p>
    </ListCard>
  );
}

interface TrackerDefinitionUpdateState {
  isEnabled: boolean;
  intervalHours: number;
  lastCheckedUtc: string | null;
  lastUpdatedUtc: string | null;
  status: string;
  error: string | null;
  updatedTrackerIds: string[];
}

/**
 * #756: the scheduled tracker-definition update -- off by default (#936).
 * Turning it on lets Deluno pull updated Cardigann-style tracker definitions
 * from its remote source on its own schedule, the same override
 * TrackerDefinitionCatalog already treats a hand-dropped YAML file as.
 */
function TrackerDefinitionsCard() {
  const { preferences } = useDisplayPreferences();
  const [state, setState] = useState<TrackerDefinitionUpdateState | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [intervalInput, setIntervalInput] = useState("24");
  const [busy, setBusy] = useState(false);
  const [running, setRunning] = useState(false);

  useEffect(() => {
    let active = true;
    void fetchJson<TrackerDefinitionUpdateState>("/api/trackers/definitions/update-check")
      .then((next) => {
        if (!active) return;
        setState(next);
        setIntervalInput(String(next.intervalHours));
      })
      .catch(() => { if (active) setLoadFailed(true); });
    return () => { active = false; };
  }, []);

  async function saveSettings(isEnabled: boolean, intervalHours: number) {
    setBusy(true);
    try {
      const next = await fetchJson<TrackerDefinitionUpdateState>("/api/trackers/definitions/update-check", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ isEnabled, intervalHours })
      });
      setState(next);
      setIntervalInput(String(next.intervalHours));
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The tracker-definition update setting could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  async function updateNow() {
    setRunning(true);
    try {
      const next = await fetchJson<TrackerDefinitionUpdateState>("/api/trackers/definitions/update-check/run", { method: "POST" });
      setState(next);
      if (next.status === "failed") {
        toast.error(localizeResetTimes(next.error ?? "The tracker definitions could not be updated."));
      } else if (next.updatedTrackerIds.length > 0) {
        toast.success(`Updated ${next.updatedTrackerIds.length} tracker definition${next.updatedTrackerIds.length === 1 ? "" : "s"}.`);
      } else {
        toast.success("Tracker definitions are already up to date.");
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The tracker definitions could not be updated.");
    } finally {
      setRunning(false);
    }
  }

  const lastUpdatedLabel = state?.lastUpdatedUtc
    ? formatDateTime(state.lastUpdatedUtc, preferences)
    : state?.lastCheckedUtc
      ? "No changes found yet"
      : "Never";

  return (
    <ListCard title="Tracker definitions" count="Keeps native tracker definitions current on their own">
      <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
        <p className="text-(length:--type-body-sm) text-muted-foreground">
          Deluno comes with hundreds of tracker sites, taken from the public Prowlarr repository on GitHub. Turning this
          on checks that repository for newer site files on the schedule below and writes them to the data folder's
          trackers directory. Off by default; "Check for newer site files" when adding an indexer does the same once,
          on request.{" "}
          <SignedApiLink className="font-medium text-primary hover:underline" path="/api/trackers/definitions/notice" mode="open" fallbackName="tracker-definitions-notice.md">Where the definitions come from and under what terms</SignedApiLink>.
        </p>
        <div className="flex items-center justify-between gap-3">
          <div className="grid gap-0.5">
            <span className="text-(length:--type-body-sm) font-medium text-foreground">Update definitions automatically</span>
            <span className="text-(length:--type-caption) text-muted-foreground">Every change is written to Activity, naming what was updated.</span>
          </div>
          <Switch
            checked={state?.isEnabled ?? false}
            disabled={!state || busy}
            onCheckedChange={(checked) => void saveSettings(checked, Number(intervalInput) || 24)}
            aria-label="Update tracker definitions automatically"
          />
        </div>
        {state?.isEnabled ? (
          <Field label="Check every" help="How often Deluno checks the remote source for an updated definition.">
            <div className="flex items-center gap-2">
              <Input
                type="number"
                min="1"
                max="168"
                step="1"
                value={intervalInput}
                onChange={(event) => setIntervalInput(event.target.value)}
                onBlur={() => void saveSettings(true, Number(intervalInput) || 24)}
                disabled={busy}
                className="max-w-32"
              />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">hours</span>
            </div>
          </Field>
        ) : null}
        <ListTable columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,1.4fr)", align: "end" }]} chevron={false}>
          <ListRow>
            <ListNameCell name="Definitions last updated" />
            <ListCell numeric align="end" primary={state ? lastUpdatedLabel : loadFailed ? "Unavailable" : "Loading…"} />
          </ListRow>
        </ListTable>
        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" variant="outline" size="sm" onClick={() => void updateNow()} disabled={running || !state}>
            {running ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
            Update now
          </Button>
          {state?.status === "failed" && state.error ? <span role="status" className="text-destructive">{localizeResetTimes(state.error)}</span> : null}
        </div>
      </div>
    </ListCard>
  );
}

/**
 * #1098: how Deluno keeps its connections honest, and the one thing the computer can do to stop Deluno
 * working. Checking connections regularly only reads, so it is on for every install; Keep awake is off until
 * it is switched on here or from the Machine problem that offers it. Both save the moment they are switched.
 */
function ConnectionChecksCard({ settings }: { settings: PlatformSettingsSnapshot }) {
  const [checkRegularly, setCheckRegularly] = useState(settings.checkConnectionsRegularly ?? true);
  const [keepAwake, setKeepAwake] = useState(settings.keepComputerAwake ?? false);
  const [busy, setBusy] = useState(false);

  async function save(patch: PlatformSettingsPatch, apply: () => void, undo: () => void, said: string) {
    apply();
    setBusy(true);
    try {
      const response = await authedFetch("/api/settings", { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
      if (!response.ok) throw new Error("That could not be saved.");
      toast.success(said);
    } catch (error) {
      undo();
      toast.error(error instanceof Error ? error.message : "That could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <ListCard title="Connection checks" count="Keeps what System says about your connections true">
      <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
        <div className="flex items-center justify-between gap-3">
          <div className="grid gap-0.5">
            <span className="text-(length:--type-body-sm) font-medium text-foreground">Check connections regularly</span>
            <span className="text-(length:--type-caption) text-muted-foreground">Every 15 minutes, Deluno tests any indexer, download client, processor, media server, Seerr, metadata service or list that nothing has used in that time, so the time shown as checked is true. It only reads; nothing is changed. A change in the answer is written to Activity.</span>
          </div>
          <Switch
            checked={checkRegularly}
            disabled={busy}
            onCheckedChange={(checked) => void save({ checkConnectionsRegularly: checked }, () => setCheckRegularly(checked), () => setCheckRegularly(!checked), checked ? "Deluno will check connections regularly." : "Deluno will only test a connection when you ask.")}
            aria-label="Check connections regularly"
          />
        </div>
        <div className="flex items-center justify-between gap-3">
          <div className="grid gap-0.5">
            <span className="text-(length:--type-body-sm) font-medium text-foreground">Keep this computer awake</span>
            <span className="text-(length:--type-caption) text-muted-foreground">While Deluno is running, it asks Windows not to go to sleep, so searches, downloads and imports are not paused. Your Windows power settings are not changed. Off until you switch it on.</span>
          </div>
          <Switch
            checked={keepAwake}
            disabled={busy}
            onCheckedChange={(checked) => void save({ keepComputerAwake: checked }, () => setKeepAwake(checked), () => setKeepAwake(!checked), checked ? "Deluno is keeping this computer awake." : "Windows will sleep by its own timers again.")}
            aria-label="Keep this computer awake"
          />
        </div>
      </div>
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
