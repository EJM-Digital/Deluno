/**
 * Settings -> Download health (#905).
 *
 * Its own page, replacing the flat per-kind toggles that used to sit on
 * Search & Cycles -> Failed Downloads: each rule kind here carries its own
 * strikes, its own completion range/protocol scope, and its own action -
 * "Replace it", "Retry elsewhere, keep files", or "Just tell me" - and every
 * rule is checked on one shared "Check every" interval, a strike per check
 * (#1194). A fresh install has no rule on: setup asks a Yes or No, and an
 * install that existed before keeps exactly what it had.
 *
 * "Dangerous or junk file" (#816/#879) is offered here too, once a client
 * adapter can list what is actually inside a download - it checks that
 * listing against the content blocklist configured on Search & Cycles ->
 * Failed downloads, rather than reading a threshold of its own.
 *
 * #951 adds the last two of #905's original wishlist: "Client-reported
 * failure" gives the always-on "client-stalled" finding its own strikes,
 * timer and action, and "Magnet stuck fetching metadata" watches
 * qBittorrent's own metaDL state - the only client adapter that reports it
 * today, so this rule never trips for a download on another client. #951
 * also adds three options to "Too slow" (a maximum total time regardless of
 * speed, a size above which the rule never applies, and skipping while the
 * client's alternate speed limit is on) and a minimum-gain option to "Reset
 * strikes on progress". Also on this page: the ignore list (#817) and
 * purge-all-strikes (#826), both part of the same engine rather than
 * separate screens.
 */
import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from "react";
import { PageLink } from "../components/app/page-link";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { automationNavItems } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { authedFetch } from "../lib/use-auth";
import {
  fetchJson,
  fetchPageItems,
  type ActivityEventItem,
  type DownloadHealthIgnoreEntry,
  type DownloadHealthRule,
  type PlatformSettingsSnapshot
} from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import { formatEventAge } from "../lib/activity-format";
import { activityLinkForStatus } from "../lib/activity-history";
import { useDisplayPreferences } from "../lib/display-preferences";

const RULE_KIND_COPY: Record<string, { label: string; description: ReactNode; thresholdLabel?: string; thresholdUnit?: string; thresholdHelp?: string }> = {
  "no-progress": {
    label: "No progress",
    description: "Nothing has arrived since the last check, at any percent. Each check with no progress adds a strike; the first check that shows progress clears them."
  },
  "too-slow": {
    label: "Too slow",
    description: "Slower than a speed limit you choose, at any percent. Each check under the limit adds a strike; the first check back over it clears them. Off until a limit makes sense for your own connections.",
    thresholdLabel: "Slower than",
    thresholdUnit: "KB/s",
    thresholdHelp: "A check that finds the download under this speed adds a strike. A check over it clears the strikes."
  },
  "low-peers": {
    label: "Not enough peers",
    description: "Fewer peers or seeders than a floor you choose, at any percent. Each check under the floor adds a strike; the first check back over it clears them. Torrents only; Usenet has no peers.",
    thresholdLabel: "Fewer than",
    thresholdUnit: "peers",
    thresholdHelp: "A check that finds the download under this peer count adds a strike. A check over it clears the strikes."
  },
  "dead-torrent": {
    label: "Dead torrent",
    description: "Zero peers or seeders at a check, once the download is older than a grace period so a magnet that has not yet announced is not judged instantly. Each check with none adds a strike; the first check with a peer clears them. Torrents only.",
    thresholdLabel: "Grace period",
    thresholdUnit: "minutes",
    thresholdHelp: "How long a freshly added download is left alone before zero peers can trip this rule."
  },
  "dangerous-file": {
    label: "Dangerous or junk file",
    description: <>A file inside the download matches the content blocklist (<PageLink page="failedDownloads" label="Automation & Recovery, Failed Downloads, Content blocklist" />). One confirmed match is enough - always a single instant strike, whatever strike count is set below.</>
  },
  "client-failure": {
    label: "Client-reported failure",
    description: <>The download client itself reported this item stalled or errored. Deluno already shows this on every download, whatever rules are configured; turning this rule on gives that same signal its own strike count and action, and it then replaces the platform-wide default on <PageLink page="failedDownloads" />.</>
  },
  "magnet-metadata": {
    label: "Magnet stuck fetching metadata",
    description: "A magnet link that has not resolved its own name, file list or size yet. Only qBittorrent reports this today - this rule never trips for a download on another client."
  }
};

const ACTION_COPY: Record<string, { label: string; description: string }> = {
  "just-tell-me": { label: "Just tell me", description: "Surface the finding. Deluno takes no action of its own." },
  "retry-elsewhere": { label: "Retry elsewhere, keep files", description: "Blocklist the release and search again, but leave it in the client untouched." },
  replace: { label: "Replace it", description: "Remove it from the client, delete its files, blocklist the release, and search again." }
};

const APPLIES_TO_OPTIONS = [
  { value: "both", label: "Torrents and Usenet" },
  { value: "torrents", label: "Torrents only" },
  { value: "usenet", label: "Usenet only" }
];

/** #815: a private-tracker account can be permanently banned for early removal or a hit-and-run, so a rule can be scoped away from it entirely. */
const PRIVACY_SCOPE_OPTIONS = [
  { value: "both", label: "Public and private" },
  { value: "public", label: "Public only" },
  { value: "private", label: "Private only" }
];

interface LoaderData {
  rules: DownloadHealthRule[];
  ignoreEntries: DownloadHealthIgnoreEntry[];
  settings: PlatformSettingsSnapshot;
  connectivityCheckSkips: ActivityEventItem[];
}

/**
 * #964: every skip written by the remediation pass (see
 * DownloadClientTelemetryService.RunConfiguredHealthRemediationAsync) is
 * filed under this same related-entity pair, so the card below can read its
 * own history back rather than keeping a counter of its own.
 */
const CONNECTIVITY_CHECK_RELATED_ENTITY_TYPE = "download_client";
const CONNECTIVITY_CHECK_RELATED_ENTITY_ID = "connectivity-check";

export async function settingsDownloadHealthLoader(): Promise<LoaderData> {
  const [rules, ignoreEntries, settings, connectivityCheckSkips] = await Promise.all([
    fetchJson<DownloadHealthRule[]>("/api/download-health/rules"),
    fetchJson<DownloadHealthIgnoreEntry[]>("/api/download-health/ignore-list"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchPageItems<ActivityEventItem>(
      `/api/activity?pageSize=50&relatedEntityType=${CONNECTIVITY_CHECK_RELATED_ENTITY_TYPE}&relatedEntityId=${CONNECTIVITY_CHECK_RELATED_ENTITY_ID}`
    )
  ]);
  return { rules, ignoreEntries, settings, connectivityCheckSkips };
}

/**
 * #814: the single sandbox switch shared by the strike/blocklist executor
 * and the seeding-cleanup executor. Its own small card, saved on its own -
 * it is a pipeline-wide behaviour, not a per-rule one, so it does not belong
 * inside the rule drawer above.
 */
function DryRunCard({ settings }: { settings: PlatformSettingsSnapshot }) {
  const mutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [enabled, setEnabled] = useState(settings.downloadHealthDryRunEnabled);
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");

  async function onChange(checked: boolean) {
    setEnabled(checked);
    setSaveState("saving");
    try {
      const next = await mutation.mutate({ downloadHealthDryRunEnabled: checked });
      setEnabled(next.downloadHealthDryRunEnabled);
      setSaveState("saved");
    } catch {
      setEnabled(settings.downloadHealthDryRunEnabled);
      setSaveState("error");
    }
  }

  return (
    <ListCard title="Dry run" count="Sandbox mode for every rule above">
      <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
        <SwitchRow
          label="Dry run"
          description="Every rule still watches and still counts strikes, but nothing is removed, blocklisted, searched for, or reclaimed. Each action it would have taken is logged to Activity as a preview instead."
          checked={enabled}
          onCheckedChange={(checked) => void onChange(checked)}
        />
        {saveState === "error" ? <p className="text-(length:--type-caption) text-destructive">Could not save. Try again.</p> : null}
      </div>
    </ListCard>
  );
}

/**
 * #825: independent of any rule's own timer - a download can go this long
 * without a fresh strike before its count clears entirely, rather than the
 * spell continuing forever at whatever pace it last accrued at.
 */
function StrikeInactivityCard({ settings }: { settings: PlatformSettingsSnapshot }) {
  const mutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [hours, setHours] = useState(String(settings.downloadHealthStrikeInactivityHours));
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [error, setError] = useState<string | null>(null);
  const dirty = hours !== String(settings.downloadHealthStrikeInactivityHours);

  async function save() {
    const value = Number(hours);
    if (!Number.isFinite(value) || value < 1 || value > 720) {
      setError("Must be between 1 and 720 hours.");
      return;
    }
    setError(null);
    setSaveState("saving");
    try {
      const next = await mutation.mutate({ downloadHealthStrikeInactivityHours: value });
      setHours(String(next.downloadHealthStrikeInactivityHours));
      setSaveState("saved");
    } catch {
      setSaveState("error");
    }
  }

  return (
    <ListCard title="Strike inactivity window" count="When a quiet download's strikes clear entirely">
      <div className="grid gap-(--grid-gap) p-(--card-pad-x) sm:grid-cols-[1fr_auto] sm:items-end">
        <Field label="Clear strikes after this long without a new one" error={error ?? undefined} help="Strikes also clear the moment a check finds the problem gone. This only covers a download Deluno has not been able to check for this long: it starts from zero.">
          <div className="flex items-center gap-2">
            <Input type="number" min="1" max="720" step="1" value={hours} onChange={(event) => setHours(event.target.value)} className="max-w-32" />
            <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">hours</span>
          </div>
        </Field>
        <Button type="button" size="sm" onClick={() => void save()} disabled={!dirty || saveState === "saving"}>Save</Button>
      </div>
      {saveState === "error" ? <p className="px-(--card-pad-x) pb-(--card-pad-x) text-(length:--type-caption) text-destructive">Could not save. Try again.</p> : null}
    </ListCard>
  );
}

/**
 * #823: two cadences that used to be fixed constants. Every rule above sets
 * its own timer now (#905), so the pacing window here only paces a finding
 * outside the rule engine - client-stalled, and so on.
 */
function TimingCard({ settings }: { settings: PlatformSettingsSnapshot }) {
  const mutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [windowMinutes, setWindowMinutes] = useState(String(settings.downloadHealthDefaultStrikeWindowMinutes));
  const [samplerMinutes, setSamplerMinutes] = useState(String(settings.downloadThroughputSamplerIntervalMinutes));
  const [strikeLimit, setStrikeLimit] = useState(String(settings.downloadHealthStrikeThreshold));
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [errors, setErrors] = useState<{ window?: string; sampler?: string; strikes?: string }>({});
  const dirty = strikeLimit !== String(settings.downloadHealthStrikeThreshold) ||
    windowMinutes !== String(settings.downloadHealthDefaultStrikeWindowMinutes) ||
    samplerMinutes !== String(settings.downloadThroughputSamplerIntervalMinutes);

  async function save() {
    const window = Number(windowMinutes);
    const sampler = Number(samplerMinutes);
    const strikes = Number(strikeLimit);
    const nextErrors: { window?: string; sampler?: string; strikes?: string } = {};
    if (!Number.isInteger(strikes) || strikes < 1 || strikes > 100) nextErrors.strikes = "Must be between 1 and 100.";
    if (!Number.isFinite(window) || window < 1 || window > 1440) nextErrors.window = "Must be between 1 and 1440 minutes.";
    if (!Number.isFinite(sampler) || sampler < 1 || sampler > 60) nextErrors.sampler = "Must be between 1 and 60 minutes.";
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length > 0) return;

    setSaveState("saving");
    try {
      const next = await mutation.mutate({
        downloadHealthDefaultStrikeWindowMinutes: window,
        downloadHealthStrikeThreshold: strikes,
        downloadThroughputSamplerIntervalMinutes: sampler
      });
      setStrikeLimit(String(next.downloadHealthStrikeThreshold));
      setWindowMinutes(String(next.downloadHealthDefaultStrikeWindowMinutes));
      setSamplerMinutes(String(next.downloadThroughputSamplerIntervalMinutes));
      setSaveState("saved");
    } catch {
      setSaveState("error");
    }
  }

  return (
    <ListCard title="Timing" count="How often Deluno checks in">
      <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
        <FieldRow>
          <Field label="Check every" error={errors.window} help="How often Deluno checks every download for the problems above. Each check where a problem is found adds one strike, and the first check where it is gone clears them. 1 to 1440 minutes; 5 by default.">
            <div className="flex items-center gap-2">
              <Input type="number" min="1" max="1440" step="1" value={windowMinutes} onChange={(event) => setWindowMinutes(event.target.value)} />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">min</span>
            </div>
          </Field>
          <Field label="Strikes before a stalled download is acted on" error={errors.strikes} help="For a download the client itself reports stalled or errored, when no rule above covers it. Each check where it is still stuck adds a strike, the first check where it moves again clears them, and at this many it is removed and cleaned up as chosen under When strikes run out. 1 to 100.">
            <Input type="number" min="1" max="100" step="1" value={strikeLimit} onChange={(event) => setStrikeLimit(event.target.value)} />
          </Field>
          <Field label="Speed chart sampling" error={errors.sampler} help="How often Deluno records a throughput sample for the speed chart.">
            <div className="flex items-center gap-2">
              <Input type="number" min="1" max="60" step="1" value={samplerMinutes} onChange={(event) => setSamplerMinutes(event.target.value)} />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">min</span>
            </div>
          </Field>
        </FieldRow>
        <div>
          <Button type="button" size="sm" onClick={() => void save()} disabled={!dirty || saveState === "saving"}>Save timing</Button>
        </div>
        {saveState === "error" ? <p className="text-(length:--type-caption) text-destructive">Could not save. Try again.</p> : null}
      </div>
    </ListCard>
  );
}

/**
 * #828: how long Deluno keeps its own event/strike history - Activity rows
 * and download-health strike records - before a daily background job
 * prunes it. A different concept from a title's own retention, which
 * RetentionService governs; this never touches a title or its files.
 */
function HistoryRetentionCard({ settings }: { settings: PlatformSettingsSnapshot }) {
  const mutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [days, setDays] = useState(String(settings.historyRetentionDays));
  // A setting with no screen is a defect: importRecoveryRetentionDays
  // already existed and was patchable, but nothing anywhere let a person
  // see or change it. Wired in here, next to Deluno's other retention
  // setting, rather than a new card of its own.
  const [importRecoveryDays, setImportRecoveryDays] = useState(String(settings.importRecoveryRetentionDays));
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [errors, setErrors] = useState<{ history?: string; importRecovery?: string }>({});
  const dirty = days !== String(settings.historyRetentionDays) ||
    importRecoveryDays !== String(settings.importRecoveryRetentionDays);

  async function save() {
    const historyValue = Number(days);
    const importRecoveryValue = Number(importRecoveryDays);
    const nextErrors: { history?: string; importRecovery?: string } = {};
    if (!Number.isFinite(historyValue) || historyValue < 1 || historyValue > 3650) nextErrors.history = "Must be between 1 and 3650 days.";
    if (!Number.isFinite(importRecoveryValue) || importRecoveryValue < 1) nextErrors.importRecovery = "Must be 1 day or more.";
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length > 0) return;

    setSaveState("saving");
    try {
      const next = await mutation.mutate({ historyRetentionDays: historyValue, importRecoveryRetentionDays: importRecoveryValue });
      setDays(String(next.historyRetentionDays));
      setImportRecoveryDays(String(next.importRecoveryRetentionDays));
      setSaveState("saved");
    } catch {
      setSaveState("error");
    }
  }

  return (
    <ListCard title="History retention" count="How long Deluno keeps its own records">
      <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
        <FieldRow>
          <Field label="Keep Activity and strike history for" error={errors.history} help="Deluno's own record of what it did and when - never a title's files or its own retention, which stays under System -> Recycle bin.">
            <div className="flex items-center gap-2">
              <Input type="number" min="1" max="3650" step="1" value={days} onChange={(event) => setDays(event.target.value)} />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">days</span>
            </div>
          </Field>
          <Field label="Keep unresolved import-recovery cases for" error={errors.importRecovery} help="How long a manual-import case (a completed download Deluno could not place on its own) is kept before it is cleared away unresolved.">
            <div className="flex items-center gap-2">
              <Input type="number" min="1" step="1" value={importRecoveryDays} onChange={(event) => setImportRecoveryDays(event.target.value)} />
              <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">days</span>
            </div>
          </Field>
        </FieldRow>
        <div>
          <Button type="button" size="sm" onClick={() => void save()} disabled={!dirty || saveState === "saving"}>Save</Button>
        </div>
      </div>
      {saveState === "error" ? <p className="px-(--card-pad-x) pb-(--card-pad-x) text-(length:--type-caption) text-destructive">Could not save. Try again.</p> : null}
    </ListCard>
  );
}

/**
 * #824: a person's own reachability guard, separate from #901's built-in
 * outage detector - Deluno's own integrations can look fine while a site the
 * person actually relies on is down for them specifically. Off by default.
 */
const CONNECTIVITY_SKIP_RECENT_WINDOW_MS = 24 * 60 * 60 * 1000;

function ConnectivityCheckCard({
  settings,
  skips
}: {
  settings: PlatformSettingsSnapshot;
  skips: ActivityEventItem[];
}) {
  const mutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const { preferences } = useDisplayPreferences();
  const [enabled, setEnabled] = useState(settings.downloadHealthConnectivityCheckEnabled);
  const [urls, setUrls] = useState(settings.downloadHealthConnectivityCheckUrls);
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const dirty = urls !== settings.downloadHealthConnectivityCheckUrls;

  // Newest first, per GET /api/activity (ListActivityPageAsync orders by
  // created_utc DESC), so the first entry is the most recent skip.
  const lastSkip = skips[0];
  const recentSkipCount = skips.filter(
    (skip) => Date.now() - new Date(skip.createdUtc).getTime() <= CONNECTIVITY_SKIP_RECENT_WINDOW_MS
  ).length;

  async function onToggle(checked: boolean) {
    setEnabled(checked);
    setSaveState("saving");
    try {
      const next = await mutation.mutate({ downloadHealthConnectivityCheckEnabled: checked });
      setEnabled(next.downloadHealthConnectivityCheckEnabled);
      setSaveState("saved");
    } catch {
      setEnabled(settings.downloadHealthConnectivityCheckEnabled);
      setSaveState("error");
    }
  }

  async function saveUrls() {
    setSaveState("saving");
    try {
      const next = await mutation.mutate({ downloadHealthConnectivityCheckUrls: urls });
      setUrls(next.downloadHealthConnectivityCheckUrls);
      setSaveState("saved");
    } catch {
      setSaveState("error");
    }
  }

  return (
    <ListCard title="Connectivity check" count="A reachability guard before every pass">
      <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
        <SwitchRow
          label="Check before every pass"
          description="Before the strike/blocklist pass, the seeding-reclaim pass, and evaluating strikes at all, Deluno sends a GET to each URL below. If none answer, the pass is skipped entirely - nothing struck, blocklisted, or reclaimed - and it's said in Activity."
          checked={enabled}
          onCheckedChange={(checked) => void onToggle(checked)}
        />
        <Field label="URLs to check" help="Comma-separated. At least one must answer with a successful (2xx) response to proceed.">
          <div className="flex items-center gap-2">
            <Input value={urls} onChange={(event) => setUrls(event.target.value)} placeholder="https://1.1.1.1, https://8.8.8.8" disabled={!enabled} />
            <Button type="button" size="sm" onClick={() => void saveUrls()} disabled={!dirty || saveState === "saving"}>Save</Button>
          </div>
        </Field>
        {saveState === "error" ? <p className="text-(length:--type-caption) text-destructive">Could not save. Try again.</p> : null}
        <div className="flex flex-wrap items-center justify-between gap-2 text-(length:--type-body-sm) text-muted-foreground">
          <span>
            {lastSkip
              ? `Last skipped a pass ${formatEventAge(lastSkip.createdUtc, preferences)}, ${recentSkipCount} time${recentSkipCount === 1 ? "" : "s"} in the last 24 hours.`
              : "Has not skipped a pass."}
          </span>
          {lastSkip ? (
            <Link to={activityLinkForStatus("done")} className="shrink-0 text-primary underline-offset-2 hover:underline">
              See in Activity
            </Link>
          ) : null}
        </div>
      </div>
    </ListCard>
  );
}

interface RuleForm {
  kind: string;
  enabled: boolean;
  strikeLimit: string;
  timerMinutes: string;
  appliesTo: string;
  minCompletionPercent: string;
  maxCompletionPercent: string;
  resetStrikesOnProgress: boolean;
  actionPreset: string;
  threshold: string;
  /** "Too slow" only (#1194): the unit the speed limit is typed in. It is stored in KB/s either way. */
  thresholdUnit: "kb" | "mb";
  privacyScope: string;
  /** "Too slow" only (#951, G157). Empty string means "no ceiling". */
  tooSlowMaxTotalMinutes: string;
  /** "Too slow" only (#951, G157), in MB for the field - converted to bytes on save. Empty string means "no ceiling". */
  ignoreAboveSizeMb: string;
  /** "Too slow" only (#951). */
  skipWhileAlternateSpeedLimitOn: boolean;
  /** #951, in MB for the field - converted to bytes on save. Empty string means "any amount". */
  resetStrikesMinimumProgressMb: string;
  /** #1240: "remove" (every rule's value until changed) or "move-and-replace". */
  privateAction: string;
  /** #1240: with a move, whether the release is also blocked for the title. */
  blocklistWhenMoved: boolean;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

function emptyForm(kind: string): RuleForm {
  return {
    kind,
    enabled: true,
    strikeLimit: "3",
    timerMinutes: "30",
    appliesTo: "both",
    minCompletionPercent: "0",
    maxCompletionPercent: "100",
    resetStrikesOnProgress: true,
    actionPreset: "just-tell-me",
    threshold: "0",
    thresholdUnit: "kb",
    privacyScope: "both",
    tooSlowMaxTotalMinutes: "",
    ignoreAboveSizeMb: "",
    skipWhileAlternateSpeedLimitOn: false,
    resetStrikesMinimumProgressMb: "",
    privateAction: "remove",
    blocklistWhenMoved: true
  };
}

function formFrom(rule: DownloadHealthRule): RuleForm {
  return {
    kind: rule.kind,
    enabled: rule.enabled,
    strikeLimit: String(rule.strikeLimit),
    timerMinutes: String(rule.timerMinutes),
    appliesTo: rule.appliesTo,
    minCompletionPercent: String(rule.minCompletionPercent),
    maxCompletionPercent: String(rule.maxCompletionPercent),
    resetStrikesOnProgress: rule.resetStrikesOnProgress,
    actionPreset: rule.actionPreset,
    ...thresholdFields(rule),
    privacyScope: rule.privacyScope,
    tooSlowMaxTotalMinutes: rule.tooSlowMaxTotalMinutes ? String(rule.tooSlowMaxTotalMinutes) : "",
    ignoreAboveSizeMb: rule.ignoreAboveSizeBytes ? String(Math.round(rule.ignoreAboveSizeBytes / 1_000_000)) : "",
    skipWhileAlternateSpeedLimitOn: rule.skipWhileAlternateSpeedLimitOn,
    resetStrikesMinimumProgressMb: rule.resetStrikesMinimumProgressBytes ? String(Math.round(rule.resetStrikesMinimumProgressBytes / 1_000_000)) : "",
    privateAction: rule.privateAction ?? "remove",
    blocklistWhenMoved: rule.blocklistWhenMoved ?? true
  };
}

/** A speed limit is saved in KB/s. A whole number of MB/s shows in MB/s so 2 MB/s reads as 2, not 2000. */
function thresholdFields(rule: DownloadHealthRule): { threshold: string; thresholdUnit: "kb" | "mb" } {
  if (rule.kind === "too-slow" && rule.threshold >= 1000 && rule.threshold % 1000 === 0) {
    return { threshold: String(rule.threshold / 1000), thresholdUnit: "mb" };
  }
  return { threshold: String(rule.threshold), thresholdUnit: "kb" };
}

function thresholdToSave(form: RuleForm) {
  const value = Number(form.threshold || 0);
  return form.kind === "too-slow" && form.thresholdUnit === "mb" ? value * 1000 : value;
}

/** #1240: moving a torrent to keep seeding replaces removing it, so it only makes sense on a rule that removes torrents. */
function canKeepSeeding(form: Pick<RuleForm, "kind" | "actionPreset" | "appliesTo">) {
  return form.actionPreset === "replace" && form.appliesTo !== "usenet" && form.kind !== "dangerous-file";
}

const PRIVATE_ACTION_OPTIONS = [
  { value: "remove", label: "Remove it like any other" },
  { value: "move-and-replace", label: "Move it to the keep-seeding category and look for another copy" }
];

function kindLabel(kind: string) {
  return RULE_KIND_COPY[kind]?.label ?? kind;
}

function actionLabel(preset: string) {
  return ACTION_COPY[preset]?.label ?? preset;
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["SettingsChanged"], areas: [ConfigAreas.downloadHealth] };

export function SettingsDownloadHealthPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { rules, ignoreEntries, settings, connectivityCheckSkips } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<RuleForm>(emptyForm("no-progress"));
  const [initialForm, setInitialForm] = useState<RuleForm>(emptyForm("no-progress"));
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [confirmPurge, setConfirmPurge] = useState(false);
  const [busy, setBusy] = useState(false);
  const [ignoreDraft, setIgnoreDraft] = useState({ hash: "", category: "", trackerDomain: "", tag: "", note: "" });
  const [ignoreBusy, setIgnoreBusy] = useState<string | null>(null);

  const editing = mode.kind === "edit" ? rules.find((rule) => rule.id === mode.id) ?? null : null;
  const open = mode.kind !== "closed";
  const dirty = open && !sameForm(form, initialForm);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const sorted = useMemo(
    () => [...rules].sort((left, right) => kindLabel(left.kind).localeCompare(kindLabel(right.kind)) || left.minCompletionPercent - right.minCompletionPercent),
    [rules]
  );

  function openRule(rule: DownloadHealthRule | null) {
    const next = rule ? formFrom(rule) : emptyForm("no-progress");
    setMode(rule ? { kind: "edit", id: rule.id } : { kind: "create" });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
    setSaveMessage(null);
    setErrors({});
  }

  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }

  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saveState === "saving") return;
    const nextErrors = validate(form);
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length > 0) return;

    setSaveState("saving");
    setSaveMessage(null);
    try {
      const body = JSON.stringify({
        kind: form.kind,
        enabled: form.enabled,
        strikeLimit: Number(form.strikeLimit),
        timerMinutes: Number(form.timerMinutes),
        appliesTo: form.appliesTo,
        minCompletionPercent: Number(form.minCompletionPercent),
        maxCompletionPercent: Number(form.maxCompletionPercent),
        resetStrikesOnProgress: form.resetStrikesOnProgress,
        actionPreset: form.actionPreset,
        threshold: thresholdToSave(form),
        privacyScope: form.privacyScope,
        tooSlowMaxTotalMinutes: form.tooSlowMaxTotalMinutes.trim() ? Number(form.tooSlowMaxTotalMinutes) : null,
        ignoreAboveSizeBytes: form.ignoreAboveSizeMb.trim() ? Math.round(Number(form.ignoreAboveSizeMb) * 1_000_000) : null,
        skipWhileAlternateSpeedLimitOn: form.skipWhileAlternateSpeedLimitOn,
        resetStrikesMinimumProgressBytes: form.resetStrikesMinimumProgressMb.trim() ? Math.round(Number(form.resetStrikesMinimumProgressMb) * 1_000_000) : null,
        privateAction: canKeepSeeding(form) ? form.privateAction : "remove",
        blocklistWhenMoved: form.blocklistWhenMoved
      });
      const response = await authedFetch(
        `/api/download-health/rules/${mode.kind === "edit" ? mode.id : "new"}`,
        { method: "PUT", headers: { "Content-Type": "application/json" }, body }
      );
      if (!response.ok) {
        const problem = await response.json().catch(() => null) as { errors?: Record<string, string[]> } | null;
        const detail = problem?.errors ? Object.values(problem.errors).flat()[0] : null;
        throw new Error(detail ?? "Rule could not be saved.");
      }
      setSaveState("saved");
      setSaveMessage("Saved just now");
      setInitialForm(form);
      revalidator.revalidate();
      closeDrawer();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  async function remove() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/download-health/rules/${mode.id}`, { method: "DELETE" });
      if (!response.ok) throw new Error("Rule could not be removed.");
      toast.success(`${kindLabel(editing?.kind ?? form.kind)} rule removed`);
      setConfirmRemove(false);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Rule could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  async function addIgnoreEntry(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!ignoreDraft.hash.trim() && !ignoreDraft.category.trim() && !ignoreDraft.trackerDomain.trim() && !ignoreDraft.tag.trim()) {
      toast.error("Give at least a hash, a client category, a tag, or a tracker domain to ignore.");
      return;
    }
    setIgnoreBusy("add");
    try {
      const response = await authedFetch("/api/download-health/ignore-list", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          hash: ignoreDraft.hash.trim() || null,
          category: ignoreDraft.category.trim() || null,
          trackerDomain: ignoreDraft.trackerDomain.trim() || null,
          tag: ignoreDraft.tag.trim() || null,
          note: ignoreDraft.note.trim() || null
        })
      });
      if (!response.ok) throw new Error("Could not add that to the ignore list.");
      setIgnoreDraft({ hash: "", category: "", trackerDomain: "", tag: "", note: "" });
      toast.success("Added to the ignore list");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not add that to the ignore list.");
    } finally {
      setIgnoreBusy(null);
    }
  }

  async function removeIgnoreEntry(entry: DownloadHealthIgnoreEntry) {
    setIgnoreBusy(entry.id);
    try {
      const response = await authedFetch(`/api/download-health/ignore-list/${encodeURIComponent(entry.id)}`, { method: "DELETE" });
      if (!response.ok) throw new Error("Could not remove that entry.");
      toast.success("Removed from the ignore list");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not remove that entry.");
    } finally {
      setIgnoreBusy(null);
    }
  }

  async function purgeAllStrikes() {
    setBusy(true);
    try {
      const response = await authedFetch("/api/download-health/purge-strikes", { method: "POST" });
      if (!response.ok) throw new Error("Could not clear strikes.");
      const result = await response.json() as { purgedCount: number };
      toast.success(result.purgedCount > 0 ? `Cleared ${result.purgedCount} recorded strike${result.purgedCount === 1 ? "" : "s"}` : "There was nothing to clear");
      setConfirmPurge(false);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not clear strikes.");
    } finally {
      setBusy(false);
    }
  }

  const kindCopy = RULE_KIND_COPY[form.kind];

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar tabs={automationNavItems} actions={<PageToolbarAction onClick={() => openRule(null)}>New rule</PageToolbarAction>} />

      <ListCard
        title={rules.length ? `${rules.length} ${rules.length === 1 ? "rule" : "rules"}` : "0 rules"}
        titleCase={false}
      >
        {rules.length === 0 ? (
          <ListEmpty
            title="No rules configured"
            description="Nothing is on by default. Add a rule for a kind of stuck download, choose its strikes, and pick what happens once it trips."
            actions={<Button type="button" size="sm" onClick={() => openRule(null)}>New rule</Button>}
          />
        ) : (
          <ListTable
            listId="download-health-rules"
            items={sorted}
            columns={[
              { id: "rule", label: "Rule", width: "minmax(0,1.3fr)", sort: (rule) => kindLabel(rule.kind).toLowerCase() },
              { id: "scope", label: "Scope", width: "minmax(0,1fr)", sort: (rule) => scopeLabel(rule).toLowerCase() },
              { id: "strikes", label: "Strikes", width: "minmax(0,1fr)", sort: (rule) => rule.strikeLimit },
              { id: "action", label: "Action", width: "minmax(0,1fr)", sort: (rule) => actionLabel(rule.actionPreset).toLowerCase() }
            ]}
          >
            {(rows) => rows.map((rule) => (
              <ListRow key={rule.id} onClick={() => openRule(rule)} selected={mode.kind === "edit" && mode.id === rule.id}>
                <ListNameCell name={kindLabel(rule.kind)} sub={rule.enabled ? "On" : "Off"} />
                <ListCell primary={scopeLabel(rule)} secondary={`${rule.minCompletionPercent}-${rule.maxCompletionPercent}% complete`} />
                <ListCell primary={`${rule.strikeLimit} strike${rule.strikeLimit === 1 ? "" : "s"}`} secondary={`Checked every ${settings.downloadHealthDefaultStrikeWindowMinutes} min`} />
                <ListCell
                  primary={actionLabel(rule.actionPreset)}
                  secondary={rule.privateAction === "move-and-replace" ? "Private torrents keep seeding instead" : undefined}
                />
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Ignored downloads" count={ignoreEntries.length ? `${ignoreEntries.length} entry${ignoreEntries.length === 1 ? "" : "ies"}` : undefined}>
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            A download matching any of these is exempt from every rule above - never struck, whatever it does. A tracker domain is the host the torrent announces to (a private tracker's own domain, subdomains included) or an indexer's name; a tag is one the client put on the torrent, or one you gave its title or indexer in Deluno.
          </p>
          <form onSubmit={(event) => void addIgnoreEntry(event)} className="grid gap-(--grid-gap) sm:grid-cols-5">
            <Input placeholder="Hash" value={ignoreDraft.hash} onChange={(event) => setIgnoreDraft((current) => ({ ...current, hash: event.target.value }))} />
            <Input placeholder="Client category" value={ignoreDraft.category} onChange={(event) => setIgnoreDraft((current) => ({ ...current, category: event.target.value }))} />
            <Input placeholder="Tracker domain" aria-label="Tracker domain" value={ignoreDraft.trackerDomain} onChange={(event) => setIgnoreDraft((current) => ({ ...current, trackerDomain: event.target.value }))} />
            <Input placeholder="Tag" aria-label="Tag" value={ignoreDraft.tag} onChange={(event) => setIgnoreDraft((current) => ({ ...current, tag: event.target.value }))} />
            <div className="flex gap-2">
              <Input placeholder="Note (optional)" value={ignoreDraft.note} onChange={(event) => setIgnoreDraft((current) => ({ ...current, note: event.target.value }))} />
              <Button type="submit" size="sm" disabled={ignoreBusy === "add"}>Add</Button>
            </div>
          </form>

          {ignoreEntries.length === 0 ? null : (
            <ListTable
              listId="download-health-ignored"
              items={ignoreEntries}
              columns={[
                { id: "matches", label: "Matches", sort: (entry) => [entry.hash, entry.category, entry.trackerDomain, entry.tag].filter(Boolean).join(" · ").toLowerCase() },
                { id: "note", label: "Note", sort: (entry) => (entry.note ?? "").toLowerCase() },
                { id: "actions", label: "", width: "80px" }
              ]}
            >
              {(rows) => rows.map((entry) => (
                <ListRow key={entry.id}>
                  <ListCell primary={[entry.hash, entry.category, entry.trackerDomain, entry.tag ? `tag ${entry.tag}` : null].filter(Boolean).join(" · ") || "—"} />
                  <ListCell primary={entry.note || "—"} />
                  <ListCell primary={
                    <Button type="button" variant="outline" size="sm" onClick={() => void removeIgnoreEntry(entry)} disabled={ignoreBusy === entry.id}>
                      Remove
                    </Button>
                  } />
                </ListRow>
              ))}
            </ListTable>
          )}
        </div>
      </ListCard>

      <DryRunCard settings={settings} />

      <StrikeInactivityCard settings={settings} />

      <TimingCard settings={settings} />

      <HistoryRetentionCard settings={settings} />

      <ConnectivityCheckCard settings={settings} skips={connectivityCheckSkips} />

      <ListCard title="Strikes">
        <div className="grid gap-(--grid-gap) p-(--card-pad-x) sm:grid-cols-[1fr_auto] sm:items-center">
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            Clear every recorded strike for every download, across every rule. This does not change any rule; it only forgets what has been counted so far. It cannot be undone.
          </p>
          <Button type="button" variant="destructive" size="sm" onClick={() => setConfirmPurge(true)}>Purge all strikes</Button>
        </div>
      </ListCard>

      <Drawer
        open={open}
        onOpenChange={(nextOpen) => {
          if (!nextOpen) requestClose();
        }}
        title={mode.kind === "create" ? "New download health rule" : kindLabel(editing?.kind ?? form.kind)}
        description={kindCopy?.description}
        onSubmit={submit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create rule" : "Save rule"} onCancel={requestClose} saveEnabled={mode.kind === "create" ? true : undefined} disabled={busy} />}
      >
        <DrawerSection title="Rule">
          <Field label="Kind" help="What this rule watches for.">
            <Select
              value={form.kind}
              onChange={(event) => setForm((current) => keepSeedingStillFits({ ...current, kind: event.target.value }))}
              options={Object.entries(RULE_KIND_COPY).map(([value, copy]) => ({ value, label: copy.label }))}
            />
          </Field>
          <SwitchRow
            label="Rule enabled"
            description="Off: this rule is kept but skipped entirely, as if it did not exist."
            checked={form.enabled}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, enabled: checked }))}
          />
          {kindCopy?.thresholdLabel ? (
            <Field label={kindCopy.thresholdLabel} error={errors.threshold} help={kindCopy.thresholdHelp}>
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step={form.kind === "too-slow" ? "any" : "1"} value={form.threshold} onChange={(event) => setForm((current) => ({ ...current, threshold: event.target.value }))} />
                {form.kind === "too-slow" ? (
                  <Select
                    aria-label="Speed unit"
                    value={form.thresholdUnit}
                    onChange={(event) => setForm((current) => ({ ...current, thresholdUnit: event.target.value === "mb" ? "mb" : "kb" }))}
                    options={[{ value: "kb", label: "KB/s" }, { value: "mb", label: "MB/s" }]}
                  />
                ) : (
                  <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">{kindCopy.thresholdUnit}</span>
                )}
              </div>
            </Field>
          ) : null}
          {form.kind === "too-slow" ? (
            <>
              <Field label="Maximum total time" error={errors.tooSlowMaxTotalMinutes} help="Optional. Once a download has been in the queue this many minutes in total, every check adds a strike whatever its current speed - leave blank for no ceiling.">
                <div className="flex items-center gap-2">
                  <Input type="number" min="1" step="1" placeholder="No ceiling" value={form.tooSlowMaxTotalMinutes} onChange={(event) => setForm((current) => ({ ...current, tooSlowMaxTotalMinutes: event.target.value }))} />
                  <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">min</span>
                </div>
              </Field>
              <Field label="Ignore above this size" error={errors.ignoreAboveSizeMb} help="Optional. A download larger than this never trips this rule - a large release is expected to take longer. Leave blank for no ceiling.">
                <div className="flex items-center gap-2">
                  <Input type="number" min="0" step="1" placeholder="No ceiling" value={form.ignoreAboveSizeMb} onChange={(event) => setForm((current) => ({ ...current, ignoreAboveSizeMb: event.target.value }))} />
                  <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">MB</span>
                </div>
              </Field>
              <SwitchRow
                label="Skip while the client's alternate speed limit is on"
                description="A download you deliberately throttled is not unhealthy. qBittorrent and Transmission (its alt-speed, or turtle mode) report whether this is on; this switch has no effect on any other client. Off by default."
                checked={form.skipWhileAlternateSpeedLimitOn}
                onCheckedChange={(checked) => setForm((current) => ({ ...current, skipWhileAlternateSpeedLimitOn: checked }))}
              />
            </>
          ) : null}
        </DrawerSection>

        <DrawerSection title="Strikes">
          <FieldRow>
            <Field label="Strikes before it acts" error={errors.strikeLimit} help="1 to 100. Each check where the problem is still there adds one strike, and the first check where it is gone clears them. At this many strikes the download is removed and cleaned up as chosen below. 1 acts the moment the problem is first seen.">
              <Input type="number" min="1" max="100" step="1" value={form.strikeLimit} onChange={(event) => setForm((current) => ({ ...current, strikeLimit: event.target.value }))} />
            </Field>
          </FieldRow>
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            {strikeTimeline(Number(form.strikeLimit), settings.downloadHealthDefaultStrikeWindowMinutes)}
          </p>
          {form.kind !== "no-progress" ? (
            <p className="text-(length:--type-body-sm) text-muted-foreground">The first check where the problem is gone (speed back over the limit, peers back) clears this rule's strikes.</p>
          ) : (
            <SwitchRow
              label="Reset strikes on progress"
              description="Any byte of further progress ends the spell early, the same as the problem clearing on its own."
              checked={form.resetStrikesOnProgress}
              onCheckedChange={(checked) => setForm((current) => ({ ...current, resetStrikesOnProgress: checked }))}
            />
          )}
          {form.kind === "no-progress" && form.resetStrikesOnProgress ? (
            <Field label="Minimum progress to reset" error={errors.resetStrikesMinimumProgressMb} help="Optional. A gain smaller than this does not reset the spell - leave blank to let any amount of progress reset it, as above.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" placeholder="Any amount" value={form.resetStrikesMinimumProgressMb} onChange={(event) => setForm((current) => ({ ...current, resetStrikesMinimumProgressMb: event.target.value }))} />
                <span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">MB</span>
              </div>
            </Field>
          ) : null}
        </DrawerSection>

        <DrawerSection title="Scope">
          <Field label="Applies to">
            <Select value={form.appliesTo} onChange={(event) => setForm((current) => keepSeedingStillFits({ ...current, appliesTo: event.target.value }))} options={APPLIES_TO_OPTIONS} />
          </Field>
          <Field label="Tracker privacy" help="A private-tracker account can be banned for early removal or a hit-and-run. Scope this rule away from private trackers to protect it, or away from public ones to leave those alone.">
            <Select value={form.privacyScope} onChange={(event) => setForm((current) => ({ ...current, privacyScope: event.target.value }))} options={PRIVACY_SCOPE_OPTIONS} />
          </Field>
          <FieldRow>
            <Field label="From this % complete" error={errors.minCompletionPercent}>
              <Input type="number" min="0" max="100" step="1" value={form.minCompletionPercent} onChange={(event) => setForm((current) => ({ ...current, minCompletionPercent: event.target.value }))} />
            </Field>
            <Field label="Up to this % complete" error={errors.maxCompletionPercent}>
              <Input type="number" min="0" max="100" step="1" value={form.maxCompletionPercent} onChange={(event) => setForm((current) => ({ ...current, maxCompletionPercent: event.target.value }))} />
            </Field>
          </FieldRow>
          <p className="text-(length:--type-caption) text-muted-foreground">
            Two enabled rules of the same kind may never cover the same protocol and completion range - saving one that would is refused with the exact rule it overlaps.
          </p>
        </DrawerSection>

        <DrawerSection title="Action">
          <Field label="When it strikes">
            <Select
              value={form.actionPreset}
              onChange={(event) => setForm((current) => keepSeedingStillFits({ ...current, actionPreset: event.target.value }))}
              options={Object.entries(ACTION_COPY).map(([value, copy]) => ({ value, label: copy.label }))}
            />
          </Field>
          <p className="text-(length:--type-caption) text-muted-foreground">
            {ACTION_COPY[form.actionPreset]?.description}
          </p>
          {canKeepSeeding(form) ? (
            <>
              <Field
                label="If it is from a private tracker"
                help="A private tracker can ban an account for deleting a torrent early. A moved torrent keeps running in your download client, Deluno stops tracking it and never deletes its files. A torrent Deluno cannot tell is private is treated as a public one."
              >
                <Select
                  value={form.privateAction}
                  onChange={(event) => setForm((current) => ({ ...current, privateAction: event.target.value }))}
                  options={PRIVATE_ACTION_OPTIONS}
                />
              </Field>
              {form.privateAction === "move-and-replace" ? (
                <>
                  <SwitchRow
                    label="Also block this release"
                    description="So the new search does not pick the same release again. Off: the search may find it again."
                    checked={form.blocklistWhenMoved}
                    onCheckedChange={(checked) => setForm((current) => ({ ...current, blocklistWhenMoved: checked }))}
                  />
                  <p className="text-(length:--type-caption) text-muted-foreground">
                    A private torrent with no keep-seeding category is left alone, and Deluno says so on System, Health. Choose the categories on <PageLink page="keepSeeding" />.
                  </p>
                </>
              ) : null}
            </>
          ) : null}
        </DrawerSection>

        {editing ? (
          <DrawerSection>
            <DrawerDanger title="Delete this rule" description="Downloads matching it stop being watched for this kind of problem. Nothing already actioned is undone." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Delete the "${kindLabel(editing?.kind ?? form.kind)}" rule?`}
        description="Downloads matching it stop being watched for this kind of problem."
        confirmLabel="Delete rule"
        busy={busy}
        onConfirm={() => void remove()}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(nextOpen) => {
          if (!nextOpen) setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this rule have not been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
      <ConfirmDialog
        open={confirmPurge}
        onOpenChange={setConfirmPurge}
        title="Purge every recorded strike?"
        description="Every download's strike count, across every rule, goes back to zero. This cannot be undone."
        confirmLabel="Purge all strikes"
        busy={busy}
        onConfirm={() => void purgeAllStrikes()}
      />
    </div>
  );
}

/** A move to keep seeding is dropped the moment the rule can no longer do it, so a saved rule is never left refused. */
function keepSeedingStillFits(form: RuleForm): RuleForm {
  return canKeepSeeding(form) || form.privateAction === "remove" ? form : { ...form, privateAction: "remove" };
}

function sameForm(left: RuleForm, right: RuleForm) {
  return left.kind === right.kind &&
    left.enabled === right.enabled &&
    left.strikeLimit === right.strikeLimit &&
    left.timerMinutes === right.timerMinutes &&
    left.appliesTo === right.appliesTo &&
    left.minCompletionPercent === right.minCompletionPercent &&
    left.maxCompletionPercent === right.maxCompletionPercent &&
    left.resetStrikesOnProgress === right.resetStrikesOnProgress &&
    left.actionPreset === right.actionPreset &&
    left.threshold === right.threshold &&
    left.thresholdUnit === right.thresholdUnit &&
    left.privacyScope === right.privacyScope &&
    left.tooSlowMaxTotalMinutes === right.tooSlowMaxTotalMinutes &&
    left.ignoreAboveSizeMb === right.ignoreAboveSizeMb &&
    left.skipWhileAlternateSpeedLimitOn === right.skipWhileAlternateSpeedLimitOn &&
    left.resetStrikesMinimumProgressMb === right.resetStrikesMinimumProgressMb &&
    left.privateAction === right.privateAction &&
    left.blocklistWhenMoved === right.blocklistWhenMoved;
}

function validate(form: RuleForm): Record<string, string> {
  const errors: Record<string, string> = {};
  const strikeLimit = Number(form.strikeLimit);
  if (!Number.isFinite(strikeLimit) || strikeLimit < 1 || strikeLimit > 100) errors.strikeLimit = "Strikes must be between 1 and 100.";
  const min = Number(form.minCompletionPercent);
  const max = Number(form.maxCompletionPercent);
  if (!Number.isFinite(min) || !Number.isFinite(max) || min < 0 || max > 100 || min > max) {
    errors.minCompletionPercent = "The completion range must be between 0% and 100%, with the low end at or below the high end.";
  }
  if (form.kind === "too-slow" && Number(form.threshold) <= 0) errors.threshold = "Set a speed floor above zero.";
  if (form.kind === "low-peers" && Number(form.threshold) < 1) errors.threshold = "Set a peer floor of at least 1.";
  if (form.kind === "dead-torrent" && (Number(form.threshold) < 0 || Number(form.threshold) > 1440)) errors.threshold = "The grace period must be between 0 minutes and 24 hours.";
  if (form.kind === "too-slow" && form.tooSlowMaxTotalMinutes.trim()) {
    const maxTotalMinutes = Number(form.tooSlowMaxTotalMinutes);
    if (!Number.isFinite(maxTotalMinutes) || maxTotalMinutes < 1 || maxTotalMinutes > 43_200) {
      errors.tooSlowMaxTotalMinutes = "The maximum total time must be between 1 minute and 30 days.";
    }
  }
  if (form.kind === "too-slow" && form.ignoreAboveSizeMb.trim() && Number(form.ignoreAboveSizeMb) < 0) {
    errors.ignoreAboveSizeMb = "The ignore-above size cannot be negative.";
  }
  if (form.resetStrikesMinimumProgressMb.trim() && Number(form.resetStrikesMinimumProgressMb) < 0) {
    errors.resetStrikesMinimumProgressMb = "The minimum progress amount cannot be negative.";
  }
  return errors;
}

/** When strike 1 and removal land (#1194), in the person's own numbers. */
function strikeTimeline(strikeLimit: number, checkEveryMinutes: number) {
  if (!Number.isFinite(strikeLimit) || strikeLimit < 1) return "";
  const first = "Strike 1 lands on the first check where the problem is found.";
  if (strikeLimit === 1) return `${first} With 1 strike the download is removed at that same check.`;
  const wait = (strikeLimit - 1) * checkEveryMinutes;
  return `${first} Checks run every ${checkEveryMinutes} min, so with ${strikeLimit} strikes it is removed about ${wait} min later, if it is still a problem at every check.`;
}

function appliesToLabel(appliesTo: string) {
  return APPLIES_TO_OPTIONS.find((option) => option.value === appliesTo)?.label ?? appliesTo;
}

function scopeLabel(rule: DownloadHealthRule) {
  const base = appliesToLabel(rule.appliesTo);
  return rule.privacyScope === "both" || !rule.privacyScope
    ? base
    : `${base} · ${PRIVACY_SCOPE_OPTIONS.find((option) => option.value === rule.privacyScope)?.label ?? rule.privacyScope}`;
}
