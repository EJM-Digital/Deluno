import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { Loader2, Play } from "lucide-react";
import { fetchJson, readValidationProblem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { formatBytes } from "../lib/utils";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import { formatDateTime, useDisplayPreferences } from "../lib/display-preferences";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { automationNavItems } from "../components/app/settings-shell";
import { describeCondition, RetentionStepBuilder, type RetentionField, type RetentionStep, type RetentionStepCount } from "../components/app/retention-step-builder";
import { RetentionKeepDialog, type KeepChoice } from "../components/app/retention-keep-dialog";
import { Button } from "../components/ui/button";
import { Badge } from "../components/ui/badge";
import { Chip } from "../components/ui/chip";
import { RetentionAddTitleDialog } from "../components/app/retention-add-title-dialog";
import { RetentionPostponeDialog } from "../components/app/retention-postpone-dialog";
import { retentionMarkPresentation } from "../lib/status-tones";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import type { MediaServerConnection } from "./media-servers-screen";

type Level = "movie" | "show" | "season" | "episode";
type RemovalMode = "automatic" | "ask";
/** What a rule does on a title's day (#1011). Only `delete` removes anything. */
type ExpiryAction = "delete" | "unmonitor" | "log" | "tag";

/** Mirrors `Deluno.Retention.RetentionRule`. */
interface RetentionRule {
  id: string;
  name: string;
  level: Level;
  isEnabled: boolean;
  steps: RetentionStep[];
  mediaServerIds: string[];
  removeFromServers: boolean;
  waitDays: number;
  removalMode: RemovalMode;
  checkIntervalHours: number;
  lastCheckedUtc: string | null;
  nextCheckUtc: string | null;
  expiryAction: ExpiryAction;
  expiryTag: string | null;
  keepInDelunoOnly: boolean;
  deferWhilePlaying: boolean;
}

/** Mirrors `Deluno.Retention.RetentionTrial`. */
interface RetentionTrial {
  considered: number;
  stepCounts: RetentionStepCount[];
  matchedCount: number;
  matchedBytes: number;
  undecidedCount: number;
  unreachableServers: string[];
  problems: string[];
  unmanaged: { server: string; title: string; year: number | null }[];
  expiryAction: ExpiryAction;
  expiryDescription: string;
}

/** Mirrors `Deluno.Retention.RetentionMark`. */
interface RetentionMark {
  ruleId: string;
  ruleName: string;
  entityId: string;
  level: Level;
  showId: string | null;
  title: string;
  year: number | null;
  sizeBytes: number | null;
  reasons: string[];
  firstMatchedUtc: string;
  removeAfterUtc: string;
  state: "leaving" | "ready" | "held" | "done" | string;
  stateReason: string | null;
  /** A person put it on the list by hand (#1008). */
  manual: boolean;
}

/** Mirrors `Deluno.Retention.RetentionKeep`. */
interface RetentionKeep {
  id: string;
  entityId: string;
  level: Level;
  title: string;
  ruleId: string | null;
  ruleName: string | null;
  note: string | null;
  createdUtc: string;
}

/** Mirrors `Deluno.Retention.RetentionRun`. */
interface RetentionRun {
  id: string;
  kind: "check" | "confirmed" | "preview" | "cleanup" | string;
  trigger: string;
  startedUtc: string;
  removedCount: number;
  summary: string;
}

interface RuleForm {
  name: string;
  level: Level;
  isEnabled: boolean;
  steps: RetentionStep[];
  mediaServerIds: string[];
  removeFromServers: boolean;
  waitDays: string;
  removalMode: RemovalMode;
  checkIntervalHours: string;
  expiryAction: ExpiryAction;
  expiryTag: string;
  keepInDelunoOnly: boolean;
  deferWhilePlaying: boolean;
}

/** What each action does, in the words the choice is made in. Plain words; the codes stay in the API. */
const EXPIRY_ACTIONS: { value: ExpiryAction; label: string }[] = [
  { value: "delete", label: "Remove it (files to the recycle bin)" },
  { value: "unmonitor", label: "Stop monitoring it (files stay)" },
  { value: "log", label: "Only write it down in Activity" },
  { value: "tag", label: "Give it a tag" }
];

const LEVELS: { value: Level; label: string; noun: string }[] = [
  { value: "movie", label: "Movies", noun: "movie" },
  { value: "show", label: "Whole shows", noun: "show" },
  { value: "season", label: "Seasons", noun: "season" },
  { value: "episode", label: "Episodes", noun: "episode" }
];

const INTERVALS = [1, 2, 4, 8, 12, 24, 48, 72, 168].map((hours) => ({
  value: String(hours),
  label: hours === 1 ? "Every hour" : hours % 24 === 0 ? (hours === 24 ? "Every day" : hours === 168 ? "Every week" : `Every ${hours / 24} days`) : `Every ${hours} hours`
}));

export async function retentionLoader() {
  const [rules, servers, marks, keeps, ...byLevel] = await Promise.all([
    fetchJson<RetentionRule[]>("/api/retention/rules"),
    fetchJson<MediaServerConnection[]>("/api/media-servers"),
    fetchJson<RetentionMark[]>("/api/retention/leaving-soon"),
    fetchJson<RetentionKeep[]>("/api/retention/keeps"),
    ...LEVELS.map((level) => fetchJson<RetentionField[]>(`/api/retention/fields?level=${level.value}`))
  ]);
  const fields = Object.fromEntries(LEVELS.map((level, index) => [level.value, byLevel[index] as RetentionField[]])) as Record<Level, RetentionField[]>;
  return { rules: rules as RetentionRule[], servers: servers as MediaServerConnection[], marks: marks as RetentionMark[], keeps: keeps as RetentionKeep[], fields };
}

function formFrom(rule: RetentionRule | null): RuleForm {
  return rule
    ? {
        name: rule.name,
        level: rule.level,
        isEnabled: rule.isEnabled,
        steps: rule.steps,
        mediaServerIds: rule.mediaServerIds,
        removeFromServers: rule.removeFromServers,
        waitDays: String(rule.waitDays),
        removalMode: rule.removalMode,
        checkIntervalHours: String(rule.checkIntervalHours),
        expiryAction: rule.expiryAction,
        expiryTag: rule.expiryTag ?? "",
        keepInDelunoOnly: rule.keepInDelunoOnly,
        deferWhilePlaying: rule.deferWhilePlaying
      }
    : {
        name: "",
        level: "movie",
        isEnabled: true,
        steps: [
          { type: "condition", op: "keep", condition: "server.watchedBy:anyone:" },
          { type: "condition", op: "keep", condition: "server.lastViewed:beyond:90" }
        ],
        mediaServerIds: [],
        removeFromServers: true,
        waitDays: "14",
        removalMode: "automatic",
        checkIntervalHours: "8",
        // A new rule starts on the gentlest choices: it only writes the day down, and nothing is removed while it plays.
        // Removing is a choice made on purpose. (A rule saved before these choices existed keeps removing.)
        expiryAction: "log",
        expiryTag: "",
        keepInDelunoOnly: false,
        deferWhilePlaying: true
      };
}

function payload(form: RuleForm) {
  return {
    name: form.name,
    level: form.level,
    isEnabled: form.isEnabled,
    steps: form.steps,
    mediaServerIds: form.mediaServerIds,
    removeFromServers: form.removeFromServers,
    waitDays: Number(form.waitDays),
    removalMode: form.removalMode,
    checkIntervalHours: Number(form.checkIntervalHours),
    expiryAction: form.expiryAction,
    expiryTag: form.expiryAction === "tag" ? form.expiryTag.trim() : null,
    keepInDelunoOnly: form.keepInDelunoOnly,
    deferWhilePlaying: form.deferWhilePlaying
  };
}

/** What a rule does on the day, for the list. */
function dayWords(rule: RetentionRule) {
  const asks = rule.removalMode === "ask";
  switch (rule.expiryAction) {
    case "unmonitor":
      return asks ? "Then asks, and stops monitoring" : "Then stops monitoring it";
    case "log":
      return asks ? "Then asks you" : "Then only writes it down";
    case "tag":
      return asks ? `Then asks, and tags “${rule.expiryTag ?? ""}”` : `Then tags it “${rule.expiryTag ?? ""}”`;
    default:
      return asks ? "Then asks you" : "Then removes it";
  }
}

/** The first step of a rule, and how many follow, for the list. */
function summarise(steps: RetentionStep[], fields: RetentionField[]) {
  const first = steps[0];
  if (!first) return { primary: "—", secondary: undefined };
  const primary = first.type === "block" ? `Block ${first.name || "1"}` : describeCondition(first.condition, fields);
  return { primary, secondary: steps.length > 1 ? `and ${steps.length - 1} more ${steps.length === 2 ? "step" : "steps"}` : undefined };
}

function levelLabel(level: Level) {
  return LEVELS.find((item) => item.value === level)?.label ?? "Movies";
}



/**
 * Retention (#494, DESIGN-008): a rule marks what it matches as Leaving soon,
 * the title waits the rule's days, and is removed on its day if it still
 * matches and nobody kept it - or waits for a person, for a rule that asks first.
 */
/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["LibraryChanged"], areas: [ConfigAreas.retention, ConfigAreas.mediaServers] };

export function RetentionPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { rules, servers, marks, keeps, fields } = useLoaderData() as Awaited<ReturnType<typeof retentionLoader>>;
  const revalidator = useRevalidator();
  const { preferences } = useDisplayPreferences();

  const [drawer, setDrawer] = useState<{ open: boolean; id: string | null }>({ open: false, id: null });
  const [form, setForm] = useState<RuleForm>(formFrom(null));
  const [initial, setInitial] = useState<RuleForm>(formFrom(null));
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [history, setHistory] = useState<RetentionRun[]>([]);
  const [trial, setTrial] = useState<RetentionTrial | null>(null);
  const [trialBusy, setTrialBusy] = useState(false);
  const [keeping, setKeeping] = useState<RetentionMark | null>(null);
  const [postponing, setPostponing] = useState<RetentionMark | null>(null);
  const [adding, setAdding] = useState(false);
  const [confirming, setConfirming] = useState<RetentionMark | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [pageMessage, setPageMessage] = useState<string | null>(null);

  const editing = drawer.id ? rules.find((rule) => rule.id === drawer.id) ?? null : null;
  const confirmingAction: ExpiryAction = rules.find((rule) => rule.id === confirming?.ruleId)?.expiryAction ?? "delete";
  const ruleFields = fields[form.level] ?? [];
  const dirty = drawer.open && JSON.stringify(payload(form)) !== JSON.stringify(payload(initial));
  const footerState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const serverNames = useMemo(() => new Map(servers.map((server) => [server.id, server.name])), [servers]);
  const leavingBytes = marks.reduce((sum, mark) => sum + (mark.sizeBytes ?? 0), 0);

  async function loadHistory(ruleId: string) {
    setHistory(await fetchJson<RetentionRun[]>(`/api/retention/rules/${ruleId}/runs?take=10`).catch(() => []));
  }

  function open(rule: RetentionRule | null) {
    const next = formFrom(rule);
    setDrawer({ open: true, id: rule?.id ?? null });
    setForm(next);
    setInitial(next);
    setSaveState(undefined);
    setSaveMessage(null);
    setHistory([]);
    setTrial(null);
    if (rule) void loadHistory(rule.id);
  }

  function close() {
    setDrawer({ open: false, id: null });
    setConfirmDiscard(false);
  }

  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else close();
  }

  // Live counts: what the rule as drawn would select now. Asked a moment after
  // the last change, so typing a value does not read every library per key.
  const trialKey = drawer.open
    ? JSON.stringify({ level: form.level, steps: form.steps, servers: form.mediaServerIds, remove: form.removeFromServers, action: form.expiryAction, tag: form.expiryTag })
    : null;
  useEffect(() => {
    if (!trialKey) return;
    let cancelled = false;
    setTrialBusy(true);
    const timer = window.setTimeout(async () => {
      try {
        const response = await authedFetch("/api/retention/rules/trial", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload(form))
        });
        if (!cancelled && response.ok) setTrial((await response.json()) as RetentionTrial);
      } finally {
        if (!cancelled) setTrialBusy(false);
      }
    }, 700);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
    // The key is the form's rule-shaping parts; the name, wait and schedule do not change a count.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [trialKey]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaveState("saving");
    setSaveMessage(null);
    try {
      const response = await authedFetch(editing ? `/api/retention/rules/${editing.id}` : "/api/retention/rules", {
        method: editing ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload(form))
      });
      if (!response.ok) {
        const problem = await readValidationProblem(response.clone()).catch(() => null);
        throw new Error(problem?.errors ? Object.values(problem.errors).flat().join(" ") : "The rule could not be saved.");
      }
      const saved = (await response.json()) as RetentionRule;
      const settled = formFrom(saved);
      setForm((current) => settleFormAfterSave(current, form, settled));
      setInitial(settled);
      setDrawer({ open: true, id: saved.id });
      setSaveState("saved");
      setSaveMessage(editing ? "Saved just now" : "Rule added");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  async function runNow() {
    if (!editing) return;
    setBusy("check");
    try {
      const response = await authedFetch(`/api/retention/rules/${editing.id}/check`, { method: "POST" });
      if (!response.ok) throw new Error(`The rule could not be checked (HTTP ${response.status}).`);
      const run = (await response.json()) as RetentionRun;
      setSaveState("saved");
      setSaveMessage(run.summary);
      void loadHistory(editing.id);
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "The rule could not be checked.");
    } finally {
      setBusy(null);
    }
  }

  async function toggle(rule: RetentionRule, isEnabled: boolean) {
    setBusy(`toggle:${rule.id}`);
    try {
      await authedFetch(`/api/retention/rules/${rule.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...payload(formFrom(rule)), isEnabled })
      });
      revalidator.revalidate();
    } finally {
      setBusy(null);
    }
  }

  async function remove() {
    if (!editing) return;
    setBusy("delete");
    try {
      const response = await authedFetch(`/api/retention/rules/${editing.id}`, { method: "DELETE" });
      if (!response.ok) throw new Error();
      setConfirmDelete(false);
      setInitial(form);
      close();
      revalidator.revalidate();
    } catch {
      setConfirmDelete(false);
      setSaveState("error");
      setSaveMessage("The rule could not be deleted.");
    } finally {
      setBusy(null);
    }
  }

  async function keep(mark: RetentionMark, choice: KeepChoice) {
    setBusy("keep");
    try {
      const response = await authedFetch("/api/retention/keeps", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ entityId: mark.entityId, ruleId: choice.scope === "rule" ? mark.ruleId : null, note: choice.note })
      });
      if (!response.ok) throw new Error();
      setKeeping(null);
      setPageMessage(`${mark.title} is kept${choice.scope === "rule" ? ` from “${mark.ruleName}”` : " from every rule"}.`);
      revalidator.revalidate();
    } catch {
      setKeeping(null);
      setPageMessage(`${mark.title} could not be kept. Try again.`);
    } finally {
      setBusy(null);
    }
  }

  async function postpone(mark: RetentionMark, days: number) {
    setBusy("postpone");
    try {
      const response = await authedFetch("/api/retention/leaving-soon/postpone", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ruleId: mark.ruleId, entityIds: [mark.entityId], days })
      });
      if (!response.ok) {
        const problem = await readValidationProblem(response.clone()).catch(() => null);
        throw new Error(problem?.errors ? Object.values(problem.errors).flat().join(" ") : "");
      }
      const moved = (await response.json()) as RetentionMark[];
      setPostponing(null);
      setPageMessage(
        moved.length > 0
          ? `${mark.title} is postponed by ${days} ${days === 1 ? "day" : "days"}: ${formatDateTime(moved[0].removeAfterUtc, preferences)}. It is not kept.`
          : `${mark.title} has nothing to postpone.`
      );
      revalidator.revalidate();
    } catch (error) {
      setPostponing(null);
      setPageMessage(error instanceof Error && error.message ? error.message : `${mark.title} could not be postponed. Try again.`);
    } finally {
      setBusy(null);
    }
  }

  async function undoKeep(item: RetentionKeep) {
    setBusy(`undo:${item.id}`);
    try {
      await authedFetch(`/api/retention/keeps/${item.id}`, { method: "DELETE" });
      revalidator.revalidate();
    } finally {
      setBusy(null);
    }
  }

  async function confirmRemoval(mark: RetentionMark) {
    setBusy("confirm");
    try {
      const response = await authedFetch("/api/retention/leaving-soon/remove", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ruleId: mark.ruleId, entityIds: [mark.entityId] })
      });
      const run = (await response.json().catch(() => null)) as RetentionRun | null;
      setConfirming(null);
      setPageMessage(run?.summary ?? `${mark.title} could not be removed.`);
      revalidator.revalidate();
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        tabs={automationNavItems}
        actions={<PageToolbarAction type="button" onClick={() => open(null)}>New rule</PageToolbarAction>}
      />

      <ListCard
        title="Leaving soon"
        count={marks.length ? `${marks.length} ${marks.length === 1 ? "title" : "titles"}${leavingBytes > 0 ? ` · ${formatBytes(leavingBytes)}` : ""}` : undefined}
        actions={
          rules.some((rule) => rule.isEnabled) ? (
            <Button type="button" variant="outline" size="sm" onClick={() => setAdding(true)} disabled={busy !== null}>
              Add a title…
            </Button>
          ) : undefined
        }
      >
        {pageMessage ? <p className="px-(--card-pad-x) py-3 text-(length:--type-caption) text-foreground" role="status">{pageMessage}</p> : null}
        {marks.length === 0 ? (
          <ListEmpty title="Nothing is leaving soon" description="When a rule matches a title, it waits here for the rule's days before it goes, and anyone can keep it." />
        ) : (
          <ListTable
            listId="retention-leaving-soon"
            items={marks}
            columns={[
              { id: "title", label: "Title", sort: (mark) => mark.title.toLowerCase() },
              { id: "why", label: "Why", sort: (mark) => (mark.reasons[0] ?? "").toLowerCase() },
              { id: "goes", label: "Goes", sort: (mark) => new Date(mark.removeAfterUtc) },
              { id: "size", label: "Size", width: LIST_TRACK.status, sort: (mark) => mark.sizeBytes },
              { id: "actions", label: "", width: "320px", mobile: true, srOnly: true }
            ]}
          >
            {(rows) => rows.map((mark) => {
              const chip = retentionMarkPresentation(mark.state);
              // A rule that does not remove titles is "Ready", not "Ready to remove".
              const removes = (rules.find((rule) => rule.id === mark.ruleId)?.expiryAction ?? "delete") === "delete";
              const chipLabel = mark.state === "ready" && !removes ? "Ready" : chip.label;
              return (
                <ListRow key={`${mark.ruleId}:${mark.entityId}`}>
                  <ListNameCell name={`${mark.title}${mark.year && mark.level === "movie" ? ` (${mark.year})` : ""}`} sub={`${levelLabel(mark.level)} · ${mark.ruleName}`} />
                  <ListCell
                    primary={mark.reasons[0] ?? "—"}
                    secondary={
                      mark.manual ? (
                        <span className="inline-flex items-center gap-1.5">
                          <Badge variant="info">Manual</Badge>
                          <span>added by hand</span>
                        </span>
                      ) : mark.reasons.length > 1 ? (
                        `and ${mark.reasons.length - 1} more`
                      ) : undefined
                    }
                  />
                  <ListCell
                    primary={<Chip tone={chip.tone}>{chipLabel}</Chip>}
                    secondary={mark.state === "held" || mark.state === "done" ? mark.stateReason ?? undefined : formatDateTime(mark.removeAfterUtc, preferences)}
                  />
                  <ListCell primary={mark.sizeBytes ? formatBytes(mark.sizeBytes) : "—"} />
                  <ListCell mobile>
                    <span className="flex justify-end gap-2">
                      <Button type="button" variant="outline" size="sm" onClick={() => setKeeping(mark)} disabled={busy !== null} aria-label={`Keep ${mark.title}`}>Keep…</Button>
                      {mark.state !== "done" ? (
                        <Button type="button" variant="outline" size="sm" onClick={() => setPostponing(mark)} disabled={busy !== null} aria-label={`Postpone ${mark.title}`}>Postpone…</Button>
                      ) : null}
                      {mark.state === "ready" ? (
                        <Button type="button" variant={removes ? "destructive" : "outline"} size="sm" onClick={() => setConfirming(mark)} disabled={busy !== null} aria-label={`${removes ? "Remove" : "Go ahead with"} ${mark.title}`}>
                          {removes ? "Remove…" : "Go ahead…"}
                        </Button>
                      ) : null}
                    </span>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <ListCard title={rules.length ? `${rules.filter((rule) => rule.isEnabled).length} of ${rules.length} on` : "0 rules"} titleCase={false}>
        {rules.length === 0 ? (
          <ListEmpty
            title="No retention rules"
            description="Describe what should be cleaned out - for example films nobody has watched in three months. Matches wait in Leaving soon before anything is removed."
          />
        ) : (
          <ListTable
            listId="retention-rules"
            items={rules}
            columns={[
              { id: "rule", label: "Rule", sort: (rule) => rule.name.toLowerCase() },
              { id: "selects", label: "Selects", sort: (rule) => summarise(rule.steps, fields[rule.level] ?? []).primary?.toString().toLowerCase() },
              { id: "matches", label: "When it matches", sort: (rule) => rule.waitDays },
              { id: "nextCheck", label: "Next check", sort: (rule) => (rule.isEnabled && rule.nextCheckUtc ? new Date(rule.nextCheckUtc) : null) },
              { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (rule) => rule.isEnabled }
            ]}
          >
            {(rows) => rows.map((rule) => {
              const selects = summarise(rule.steps, fields[rule.level] ?? []);
              return (
                <ListRow key={rule.id} onClick={() => open(rule)} selected={drawer.open && drawer.id === rule.id}>
                  <ListNameCell
                    name={rule.name}
                    sub={`${levelLabel(rule.level)}${rule.mediaServerIds.length ? ` · ${rule.mediaServerIds.map((id) => serverNames.get(id) ?? "Removed server").join(", ")}` : ""}`}
                  />
                  <ListCell primary={selects.primary} secondary={selects.secondary} />
                  <ListCell primary={`Waits ${rule.waitDays} ${rule.waitDays === 1 ? "day" : "days"}`} secondary={dayWords(rule)} />
                  <ListCell primary={rule.isEnabled && rule.nextCheckUtc ? formatDateTime(rule.nextCheckUtc, preferences) : "—"} />
                  <ListCell mobile>
                    <Switch size="sm" aria-label={`${rule.isEnabled ? "Turn off" : "Turn on"} ${rule.name}`} checked={rule.isEnabled} disabled={busy === `toggle:${rule.id}`} onCheckedChange={(checked) => void toggle(rule, checked)} />
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      {keeps.length > 0 ? (
        <ListCard title="Kept" count={`${keeps.length} ${keeps.length === 1 ? "title" : "titles"}`}>
          <ListTable
            listId="retention-kept"
            items={keeps}
            columns={[
              { id: "title", label: "Title", sort: (item) => item.title.toLowerCase() },
              { id: "keptFrom", label: "Kept from", sort: (item) => (item.ruleId ? item.ruleName ?? "Deleted rule" : "Every rule").toLowerCase() },
              { id: "note", label: "Note", sort: (item) => (item.note ?? "").toLowerCase() },
              { id: "actions", label: "", width: "120px", mobile: true, srOnly: true }
            ]}
          >
            {(rows) => rows.map((item) => (
              <ListRow key={item.id}>
                <ListNameCell name={item.title} sub={levelLabel(item.level)} />
                <ListCell primary={item.ruleId ? `“${item.ruleName ?? "Deleted rule"}”` : "Every rule"} secondary={formatDateTime(item.createdUtc, preferences)} />
                <ListCell primary={item.note ?? <span className="text-muted-foreground">—</span>} />
                <ListCell mobile>
                  <Button type="button" variant="ghost" size="sm" onClick={() => void undoKeep(item)} disabled={busy === `undo:${item.id}`}>Undo</Button>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      <Drawer
        open={drawer.open}
        onOpenChange={(next) => {
          if (!next) requestClose();
        }}
        title={editing?.name ?? (form.name || "New retention rule")}
        description={editing ? levelLabel(editing.level) : "Choose what should be cleaned out. Matches wait in Leaving soon before anything is removed."}
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter state={footerState} message={saveMessage} saveLabel={editing ? "Save rule" : "Add rule"} onCancel={requestClose} saveEnabled={editing ? undefined : true} disabled={busy !== null} />
        }
      >
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Name">
              <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} placeholder="Unwatched films" autoComplete="off" />
            </Field>
            <Field label="Cleans out">
              <Select
                value={form.level}
                disabled={Boolean(editing)}
                onChange={(event) => setForm((current) => ({ ...current, level: event.target.value as Level, steps: formFrom(null).steps }))}
                options={LEVELS.map((level) => ({ value: level.value, label: level.label }))}
              />
            </Field>
          </FieldRow>
          <SwitchRow label="Enabled" description="A rule that is off is not checked, and marks nothing." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>

        <DrawerSection title="Steps" aside={trialBusy ? "counting…" : trial && !trial.problems.length ? `${trial.matchedCount} match${trial.matchedBytes > 0 ? ` · ${formatBytes(trial.matchedBytes)}` : ""}` : undefined}>
          <RetentionStepBuilder
            steps={form.steps}
            onChange={(steps) => setForm((current) => ({ ...current, steps }))}
            fields={ruleFields}
            serverKinds={servers.filter((server) => form.mediaServerIds.includes(server.id)).map((server) => server.kind)}
            counts={trial && !trial.problems.length ? trial.stepCounts : null}
            considered={trial?.considered ?? null}
            noun={LEVELS.find((level) => level.value === form.level)?.noun ?? "title"}
          />
          {trial && !trial.problems.length && trial.matchedCount > 0 ? (
            <p className="text-(length:--type-caption) text-foreground">
              {trial.matchedCount === 1 ? "This title" : `These ${trial.matchedCount} titles`}, on {trial.matchedCount === 1 ? "its" : "their"} day: {trial.expiryDescription}
            </p>
          ) : null}
          {trial?.problems.length ? (
            <ul className="grid gap-0.5 text-(length:--type-caption) text-warning" role="alert">
              {trial.problems.map((problem) => <li key={problem}>{problem}</li>)}
            </ul>
          ) : null}
          {trial?.unreachableServers.length ? (
            <p className="text-(length:--type-caption) text-warning">Nothing would be selected: {trial.unreachableServers.join("; ")}.</p>
          ) : null}
          {trial && trial.undecidedCount > 0 ? (
            <p className="text-(length:--type-caption) text-muted-foreground">
              {trial.undecidedCount} {trial.undecidedCount === 1 ? "title" : "titles"} could not be decided — a server could not answer a step — and would be left alone.
            </p>
          ) : null}
          {trial && trial.unmanaged.length > 0 ? (
            <details className="text-(length:--type-caption) text-muted-foreground">
              <summary className="cursor-pointer">
                {trial.unmanaged.length} on your servers but not in Deluno. They are never touched.
              </summary>
              <ul className="mt-2 grid gap-1">
                {trial.unmanaged.slice(0, 50).map((item) => (
                  <li key={`${item.server}:${item.title}:${item.year ?? ""}`} className="flex items-center justify-between gap-2">
                    <span className="text-foreground">
                      {item.title}
                      {item.year ? ` (${item.year})` : ""} <span className="text-muted-foreground">· {item.server}</span>
                    </span>
                    <a
                      className="shrink-0 text-primary hover:underline"
                      href={`${form.level === "movie" ? "/movies" : "/tv"}?add=true&title=${encodeURIComponent(item.year ? `${item.title} ${item.year}` : item.title)}`}
                    >
                      Bring it in
                    </a>
                  </li>
                ))}
              </ul>
            </details>
          ) : null}
        </DrawerSection>

        <DrawerSection title="Media servers" aside={servers.length ? undefined : "none connected"}>
          {servers.length === 0 ? (
            <p className="text-(length:--type-caption) text-muted-foreground">Connect Plex, Jellyfin or Emby under Find &amp; Download → Media Servers to use what they know.</p>
          ) : (
            servers.map((server) => (
              <SwitchRow
                key={server.id}
                label={server.name}
                description={server.serverName ? `Reports itself as ${server.serverName}` : server.baseUrl}
                checked={form.mediaServerIds.includes(server.id)}
                onCheckedChange={(checked) => setForm((current) => ({ ...current, mediaServerIds: checked ? [...current.mediaServerIds, server.id] : current.mediaServerIds.filter((id) => id !== server.id) }))}
              />
            ))
          )}
          <SwitchRow
            label="Tell these servers when a title goes"
            description="If any of them cannot be asked when a check runs, nothing is marked or removed until they can."
            checked={form.removeFromServers}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, removeFromServers: checked }))}
          />
          <SwitchRow
            label="Keep the list in Deluno only"
            description="Nothing is created on these servers. The list stays here for review, and keeping, postponing and the day's action work as usual."
            checked={form.keepInDelunoOnly}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, keepInDelunoOnly: checked }))}
          />
          <SwitchRow
            label="Never remove something while it plays"
            description="Deluno asks every media server you have connected what is being played. A title that is playing on any of them waits for the next check, and so does everything if one cannot say."
            checked={form.deferWhilePlaying}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, deferWhilePlaying: checked }))}
          />
        </DrawerSection>

        <DrawerSection title="When it matches">
          <FieldRow>
            <Field label="Leaving soon for" help="Days a title waits before its removal day. It is checked again on the day.">
              <Input type="number" min={1} max={365} value={form.waitDays} onChange={(event) => setForm((current) => ({ ...current, waitDays: event.target.value }))} />
            </Field>
            <Field label="Check every">
              <Select value={form.checkIntervalHours} onChange={(event) => setForm((current) => ({ ...current, checkIntervalHours: event.target.value }))} options={INTERVALS} />
            </Field>
          </FieldRow>
          <Field label="What happens">
            <Select
              value={form.expiryAction}
              onChange={(event) => setForm((current) => ({ ...current, expiryAction: event.target.value as ExpiryAction }))}
              options={EXPIRY_ACTIONS}
            />
          </Field>
          {form.expiryAction === "tag" ? (
            <Field label="Tag to give" help="Made if it does not exist yet. A season or episode gives its show the tag.">
              <Input value={form.expiryTag} onChange={(event) => setForm((current) => ({ ...current, expiryTag: event.target.value }))} placeholder="watched-and-old" maxLength={60} autoComplete="off" />
            </Field>
          ) : null}
          <Field label="On the day" help={form.removalMode === "ask" ? "It waits in Leaving soon as Ready until you confirm." : "Done if it still matches and nobody kept it."}>
            <SegmentedControl<RemovalMode>
              value={form.removalMode}
              onValueChange={(removalMode) => setForm((current) => ({ ...current, removalMode }))}
              options={[
                { value: "automatic", label: "Do it by itself" },
                { value: "ask", label: "Ask me first" }
              ]}
            />
          </Field>
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Check" aside={dirty ? "save first" : editing.lastCheckedUtc ? `last ${formatDateTime(editing.lastCheckedUtc, preferences)}` : undefined}>
            <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void runNow()} disabled={busy !== null || dirty}>
              {busy === "check" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Play className="h-3.5 w-3.5" />}
              Run now
            </Button>
            {history.length > 0 ? (
              <dl className="grid grid-cols-[150px_1fr] gap-x-(--grid-gap) gap-y-2 text-(length:--type-body-sm)">
                {history.map((run) => (
                  <div key={run.id} className="contents">
                    <dt className="text-muted-foreground">
                      {formatDateTime(run.startedUtc, preferences)}
                      <span className="block text-(length:--type-caption)">{run.kind === "confirmed" ? "Confirmed" : run.trigger === "schedule" ? "Scheduled" : run.kind === "check" ? "Run now" : "Earlier version"}</span>
                    </dt>
                    <dd className="text-foreground">{run.summary}</dd>
                  </div>
                ))}
              </dl>
            ) : null}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger
              title="Delete this rule"
              description="Its Leaving soon titles are cleared. Titles it already removed stay removed, with their exclusions."
              action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmDelete(true)} disabled={busy !== null}>Delete…</Button>}
            />
          </DrawerSection>
        ) : null}
      </Drawer>

      <RetentionKeepDialog
        open={keeping !== null}
        onOpenChange={(next) => {
          if (!next) setKeeping(null);
        }}
        title={keeping?.title ?? "title"}
        ruleName={keeping?.ruleName ?? "this rule"}
        busy={busy === "keep"}
        onConfirm={(choice) => keeping && void keep(keeping, choice)}
      />
      <RetentionPostponeDialog
        open={postponing !== null}
        onOpenChange={(next) => {
          if (!next) setPostponing(null);
        }}
        title={postponing?.title ?? "title"}
        removeAfterUtc={postponing?.removeAfterUtc ?? null}
        busy={busy === "postpone"}
        onConfirm={(days) => postponing && void postpone(postponing, days)}
      />
      <RetentionAddTitleDialog
        open={adding}
        onOpenChange={setAdding}
        rules={rules}
        onAdded={(message) => {
          setPageMessage(message);
          revalidator.revalidate();
        }}
      />
      <ConfirmDialog
        open={confirming !== null}
        onOpenChange={(next) => {
          if (!next) setConfirming(null);
        }}
        title={`${confirmingAction === "delete" ? "Remove" : "Go ahead with"} ${confirming?.title ?? "title"}?`}
        description={
          confirmingAction === "delete"
            ? `The rule is checked once more first. If it still matches, the files go to the recycle bin (${confirming?.sizeBytes ? formatBytes(confirming.sizeBytes) : "size unknown"}) and ${confirming?.level === "season" || confirming?.level === "episode" ? "the episodes are no longer wanted" : "an exclusion records why, so no list adds it back"}. If something is playing it, it waits.`
            : "The rule is checked once more first. If it still matches, its action is done. No files are removed."
        }
        confirmLabel={confirmingAction === "delete" ? "Remove" : "Go ahead"}
        confirmVariant={confirmingAction === "delete" ? "destructive" : "default"}
        busy={busy === "confirm"}
        onConfirm={() => confirming && void confirmRemoval(confirming)}
      />
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete “${editing?.name ?? "rule"}”?`}
        description="Its history is kept. Titles it removed stay removed."
        confirmLabel="Delete rule"
        confirmVariant="destructive"
        busy={busy === "delete"}
        onConfirm={() => void remove()}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(next) => {
          if (!next) setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          setInitial(form);
          close();
        }}
      />
    </div>
  );
}
