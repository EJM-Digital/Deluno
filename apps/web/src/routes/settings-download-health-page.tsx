/**
 * Settings -> Download health (#905).
 *
 * Its own page, replacing the flat per-kind toggles that used to sit on
 * Search & Cycles -> Failed Downloads: each rule kind here carries its own
 * strikes, its own timer, its own completion range/protocol scope, and its
 * own action - "Replace it", "Retry elsewhere, keep files", or "Just tell
 * me". Nothing is on for a rule that was not already on before this screen
 * existed (#936); the first read of /api/download-health/rules synthesizes
 * that starting point once, server-side.
 *
 * Two rule kinds Cleanuparr and #905's own wishlist name - "magnet stuck
 * fetching metadata" and "dangerous or junk files" among them - are not
 * offered here: no client adapter reports the telemetry they would need yet
 * (see Deluno.Contracts.DownloadHealthRuleKinds). Also on this page: the
 * ignore list (#817) and purge-all-strikes (#826), both part of the same
 * engine rather than separate screens.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
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
import { fetchJson, type DownloadHealthIgnoreEntry, type DownloadHealthRule, type PlatformSettingsSnapshot } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

const RULE_KIND_COPY: Record<string, { label: string; description: string; thresholdLabel?: string; thresholdUnit?: string; thresholdHelp?: string }> = {
  "no-progress": {
    label: "No progress",
    description: "Nothing has arrived at all, whatever the client's other numbers say."
  },
  "too-slow": {
    label: "Too slow",
    description: "Slower than a speed floor you choose. Off until a floor makes sense for your own connections.",
    thresholdLabel: "Slower than",
    thresholdUnit: "KB/s",
    thresholdHelp: "A download under this speed, for the timer below, trips this rule."
  },
  "low-peers": {
    label: "Not enough peers",
    description: "Fewer peers or seeders than a floor you choose. Torrents only; Usenet has no peers.",
    thresholdLabel: "Fewer than",
    thresholdUnit: "peers",
    thresholdHelp: "A download under this peer count, for the timer below, trips this rule."
  },
  "dead-torrent": {
    label: "Dead torrent",
    description: "Zero peers or seeders left, past a grace period so a magnet that has not yet announced is not judged instantly. Torrents only.",
    thresholdLabel: "Grace period",
    thresholdUnit: "minutes",
    thresholdHelp: "How long a freshly added download is left alone before zero peers can trip this rule."
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
}

export async function settingsDownloadHealthLoader(): Promise<LoaderData> {
  const [rules, ignoreEntries, settings] = await Promise.all([
    fetchJson<DownloadHealthRule[]>("/api/download-health/rules"),
    fetchJson<DownloadHealthIgnoreEntry[]>("/api/download-health/ignore-list"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings")
  ]);
  return { rules, ignoreEntries, settings };
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
      <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
        <SwitchRow
          label="Dry run"
          description="Every rule still watches and still counts strikes, but nothing is removed, blocklisted, searched for, or reclaimed. Each action it would have taken is logged to Activity as a preview instead."
          checked={enabled}
          onCheckedChange={(checked) => void onChange(checked)}
        />
        {saveState === "error" ? <p className="text-[length:var(--type-caption)] text-destructive">Could not save. Try again.</p> : null}
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
      <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)] sm:grid-cols-[1fr_auto] sm:items-end">
        <Field label="Clear strikes after this long without a new one" error={error ?? undefined} help="Separate from any rule's own timer - a download that goes this long without tripping any rule again starts from zero.">
          <div className="flex items-center gap-2">
            <Input type="number" min="1" max="720" step="1" value={hours} onChange={(event) => setHours(event.target.value)} className="max-w-[8rem]" />
            <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">hours</span>
          </div>
        </Field>
        <Button type="button" size="sm" onClick={() => void save()} disabled={!dirty || saveState === "saving"}>Save</Button>
      </div>
      {saveState === "error" ? <p className="px-[var(--card-pad-x)] pb-[var(--card-pad-x)] text-[length:var(--type-caption)] text-destructive">Could not save. Try again.</p> : null}
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
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [errors, setErrors] = useState<{ window?: string; sampler?: string }>({});
  const dirty = windowMinutes !== String(settings.downloadHealthDefaultStrikeWindowMinutes) ||
    samplerMinutes !== String(settings.downloadThroughputSamplerIntervalMinutes);

  async function save() {
    const window = Number(windowMinutes);
    const sampler = Number(samplerMinutes);
    const nextErrors: { window?: string; sampler?: string } = {};
    if (!Number.isFinite(window) || window < 1 || window > 1440) nextErrors.window = "Must be between 1 and 1440 minutes.";
    if (!Number.isFinite(sampler) || sampler < 1 || sampler > 60) nextErrors.sampler = "Must be between 1 and 60 minutes.";
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length > 0) return;

    setSaveState("saving");
    try {
      const next = await mutation.mutate({
        downloadHealthDefaultStrikeWindowMinutes: window,
        downloadThroughputSamplerIntervalMinutes: sampler
      });
      setWindowMinutes(String(next.downloadHealthDefaultStrikeWindowMinutes));
      setSamplerMinutes(String(next.downloadThroughputSamplerIntervalMinutes));
      setSaveState("saved");
    } catch {
      setSaveState("error");
    }
  }

  return (
    <ListCard title="Timing" count="How often Deluno checks in">
      <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
        <FieldRow>
          <Field label="Strike pacing outside the rules above" error={errors.window} help="Only applies to a finding no rule above covers, such as the client itself reporting stalled or errored. A rule's own timer above is unaffected.">
            <div className="flex items-center gap-2">
              <Input type="number" min="1" max="1440" step="1" value={windowMinutes} onChange={(event) => setWindowMinutes(event.target.value)} />
              <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">min</span>
            </div>
          </Field>
          <Field label="Speed chart sampling" error={errors.sampler} help="How often Deluno records a throughput sample for the speed chart.">
            <div className="flex items-center gap-2">
              <Input type="number" min="1" max="60" step="1" value={samplerMinutes} onChange={(event) => setSamplerMinutes(event.target.value)} />
              <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">min</span>
            </div>
          </Field>
        </FieldRow>
        <div>
          <Button type="button" size="sm" onClick={() => void save()} disabled={!dirty || saveState === "saving"}>Save timing</Button>
        </div>
        {saveState === "error" ? <p className="text-[length:var(--type-caption)] text-destructive">Could not save. Try again.</p> : null}
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
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [error, setError] = useState<string | null>(null);
  const dirty = days !== String(settings.historyRetentionDays);

  async function save() {
    const value = Number(days);
    if (!Number.isFinite(value) || value < 1 || value > 3650) {
      setError("Must be between 1 and 3650 days.");
      return;
    }
    setError(null);
    setSaveState("saving");
    try {
      const next = await mutation.mutate({ historyRetentionDays: value });
      setDays(String(next.historyRetentionDays));
      setSaveState("saved");
    } catch {
      setSaveState("error");
    }
  }

  return (
    <ListCard title="History retention" count="How long Deluno keeps its own records">
      <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)] sm:grid-cols-[1fr_auto] sm:items-end">
        <Field label="Keep Activity and strike history for" error={error ?? undefined} help="Deluno's own record of what it did and when - never a title's files or its own retention, which stays under System -> Recycle bin.">
          <div className="flex items-center gap-2">
            <Input type="number" min="1" max="3650" step="1" value={days} onChange={(event) => setDays(event.target.value)} className="max-w-[8rem]" />
            <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">days</span>
          </div>
        </Field>
        <Button type="button" size="sm" onClick={() => void save()} disabled={!dirty || saveState === "saving"}>Save</Button>
      </div>
      {saveState === "error" ? <p className="px-[var(--card-pad-x)] pb-[var(--card-pad-x)] text-[length:var(--type-caption)] text-destructive">Could not save. Try again.</p> : null}
    </ListCard>
  );
}

/**
 * #824: a person's own reachability guard, separate from #901's built-in
 * outage detector - Deluno's own integrations can look fine while a site the
 * person actually relies on is down for them specifically. Off by default.
 */
function ConnectivityCheckCard({ settings }: { settings: PlatformSettingsSnapshot }) {
  const mutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [enabled, setEnabled] = useState(settings.downloadHealthConnectivityCheckEnabled);
  const [urls, setUrls] = useState(settings.downloadHealthConnectivityCheckUrls);
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const dirty = urls !== settings.downloadHealthConnectivityCheckUrls;

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
      <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
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
        {saveState === "error" ? <p className="text-[length:var(--type-caption)] text-destructive">Could not save. Try again.</p> : null}
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
  privacyScope: string;
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
    privacyScope: "both"
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
    threshold: String(rule.threshold),
    privacyScope: rule.privacyScope
  };
}

function kindLabel(kind: string) {
  return RULE_KIND_COPY[kind]?.label ?? kind;
}

function actionLabel(preset: string) {
  return ACTION_COPY[preset]?.label ?? preset;
}

export function SettingsDownloadHealthPage() {
  const { rules, ignoreEntries, settings } = useLoaderData() as LoaderData;
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
  const [ignoreDraft, setIgnoreDraft] = useState({ hash: "", category: "", trackerDomain: "", note: "" });
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
        threshold: Number(form.threshold || 0),
        privacyScope: form.privacyScope
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
    if (!ignoreDraft.hash.trim() && !ignoreDraft.category.trim() && !ignoreDraft.trackerDomain.trim()) {
      toast.error("Give at least a hash, a client category, or a tracker/indexer domain to ignore.");
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
          note: ignoreDraft.note.trim() || null
        })
      });
      if (!response.ok) throw new Error("Could not add that to the ignore list.");
      setIgnoreDraft({ hash: "", category: "", trackerDomain: "", note: "" });
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
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={automationNavItems} actions={<PageToolbarAction onClick={() => openRule(null)}>New rule</PageToolbarAction>} />

      <ListCard
        title="Download health rules"
        count={rules.length ? `${rules.length} ${rules.length === 1 ? "rule" : "rules"}` : undefined}
      >
        {rules.length === 0 ? (
          <ListEmpty
            title="No rules configured"
            description="Nothing is on by default. Add a rule for a kind of stuck download, choose its strikes and timer, and pick what happens once it trips."
            actions={<Button type="button" size="sm" onClick={() => openRule(null)}>New rule</Button>}
          />
        ) : (
          <ListTable
            columns={[
              { label: "Rule", width: "minmax(0,1.3fr)" },
              { label: "Scope", width: "minmax(0,1fr)" },
              { label: "Strikes & timer", width: "minmax(0,1fr)" },
              { label: "Action", width: "minmax(0,1fr)" }
            ]}
          >
            {sorted.map((rule) => (
              <ListRow key={rule.id} onClick={() => openRule(rule)} selected={mode.kind === "edit" && mode.id === rule.id}>
                <ListNameCell name={kindLabel(rule.kind)} sub={rule.enabled ? "On" : "Off"} />
                <ListCell primary={scopeLabel(rule)} secondary={`${rule.minCompletionPercent}-${rule.maxCompletionPercent}% complete`} />
                <ListCell primary={`${rule.strikeLimit} strike${rule.strikeLimit === 1 ? "" : "s"}`} secondary={`Every ${rule.timerMinutes} min`} />
                <ListCell primary={actionLabel(rule.actionPreset)} />
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Ignored downloads" count={ignoreEntries.length ? `${ignoreEntries.length} entry${ignoreEntries.length === 1 ? "" : "ies"}` : undefined}>
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <p className="text-[length:var(--type-body-sm)] text-muted-foreground">
            A download matching any of these is exempt from every rule above - never struck, whatever it does.
          </p>
          <form onSubmit={(event) => void addIgnoreEntry(event)} className="grid gap-[var(--grid-gap)] sm:grid-cols-4">
            <Input placeholder="Hash" value={ignoreDraft.hash} onChange={(event) => setIgnoreDraft((current) => ({ ...current, hash: event.target.value }))} />
            <Input placeholder="Client category" value={ignoreDraft.category} onChange={(event) => setIgnoreDraft((current) => ({ ...current, category: event.target.value }))} />
            <Input placeholder="Tracker/indexer domain" value={ignoreDraft.trackerDomain} onChange={(event) => setIgnoreDraft((current) => ({ ...current, trackerDomain: event.target.value }))} />
            <div className="flex gap-2">
              <Input placeholder="Note (optional)" value={ignoreDraft.note} onChange={(event) => setIgnoreDraft((current) => ({ ...current, note: event.target.value }))} />
              <Button type="submit" size="sm" disabled={ignoreBusy === "add"}>Add</Button>
            </div>
          </form>

          {ignoreEntries.length === 0 ? null : (
            <ListTable columns={[{ label: "Matches" }, { label: "Note" }, { label: "", width: "80px" }]}>
              {ignoreEntries.map((entry) => (
                <ListRow key={entry.id}>
                  <ListCell primary={[entry.hash, entry.category, entry.trackerDomain].filter(Boolean).join(" · ") || "—"} />
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

      <ConnectivityCheckCard settings={settings} />

      <ListCard title="Strikes">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)] sm:grid-cols-[1fr_auto] sm:items-center">
          <p className="text-[length:var(--type-body-sm)] text-muted-foreground">
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
              onChange={(event) => setForm((current) => ({ ...current, kind: event.target.value }))}
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
                <Input type="number" min="0" step="1" value={form.threshold} onChange={(event) => setForm((current) => ({ ...current, threshold: event.target.value }))} />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">{kindCopy.thresholdUnit}</span>
              </div>
            </Field>
          ) : null}
        </DrawerSection>

        <DrawerSection title="Strikes & timer">
          <FieldRow>
            <Field label="Strikes before it acts" error={errors.strikeLimit} help="1 to 20. 1 acts the moment the problem is first seen.">
              <Input type="number" min="1" max="20" step="1" value={form.strikeLimit} onChange={(event) => setForm((current) => ({ ...current, strikeLimit: event.target.value }))} />
            </Field>
            <Field label="Timer" error={errors.timerMinutes} help="How often, in minutes, a further strike can be added while the problem continues.">
              <div className="flex items-center gap-2">
                <Input type="number" min="1" max="1440" step="1" value={form.timerMinutes} onChange={(event) => setForm((current) => ({ ...current, timerMinutes: event.target.value }))} />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">min</span>
              </div>
            </Field>
          </FieldRow>
          <SwitchRow
            label="Reset strikes on progress"
            description="Any byte of further progress ends the spell early, the same as the problem clearing on its own."
            checked={form.resetStrikesOnProgress}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, resetStrikesOnProgress: checked }))}
          />
        </DrawerSection>

        <DrawerSection title="Scope">
          <Field label="Applies to">
            <Select value={form.appliesTo} onChange={(event) => setForm((current) => ({ ...current, appliesTo: event.target.value }))} options={APPLIES_TO_OPTIONS} />
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
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            Two enabled rules of the same kind may never cover the same protocol and completion range - saving one that would is refused with the exact rule it overlaps.
          </p>
        </DrawerSection>

        <DrawerSection title="Action">
          <Field label="When it strikes">
            <Select
              value={form.actionPreset}
              onChange={(event) => setForm((current) => ({ ...current, actionPreset: event.target.value }))}
              options={Object.entries(ACTION_COPY).map(([value, copy]) => ({ value, label: copy.label }))}
            />
          </Field>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            {ACTION_COPY[form.actionPreset]?.description}
          </p>
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
    left.privacyScope === right.privacyScope;
}

function validate(form: RuleForm): Record<string, string> {
  const errors: Record<string, string> = {};
  const strikeLimit = Number(form.strikeLimit);
  if (!Number.isFinite(strikeLimit) || strikeLimit < 1 || strikeLimit > 20) errors.strikeLimit = "Strikes must be between 1 and 20.";
  const timerMinutes = Number(form.timerMinutes);
  if (!Number.isFinite(timerMinutes) || timerMinutes < 1 || timerMinutes > 1440) errors.timerMinutes = "The timer must be between 1 minute and 24 hours.";
  const min = Number(form.minCompletionPercent);
  const max = Number(form.maxCompletionPercent);
  if (!Number.isFinite(min) || !Number.isFinite(max) || min < 0 || max > 100 || min > max) {
    errors.minCompletionPercent = "The completion range must be between 0% and 100%, with the low end at or below the high end.";
  }
  if (form.kind === "too-slow" && Number(form.threshold) <= 0) errors.threshold = "Set a speed floor above zero.";
  if (form.kind === "low-peers" && Number(form.threshold) < 1) errors.threshold = "Set a peer floor of at least 1.";
  if (form.kind === "dead-torrent" && (Number(form.threshold) < 0 || Number(form.threshold) > 1440)) errors.threshold = "The grace period must be between 0 minutes and 24 hours.";
  return errors;
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
