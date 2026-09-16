import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Eye, Loader2, Plus, Trash2 } from "lucide-react";
import { fetchJson, readValidationProblem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { formatBytes } from "../lib/utils";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import { formatDateTime, useDisplayPreferences } from "../lib/display-preferences";
import { OPERATOR_LABELS, type FilterOperator } from "../lib/library-controls";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { automationNavItems } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import type { MediaServerConnection } from "./media-servers-screen";

/** Mirrors `Deluno.Retention.RetentionRule`. */
interface RetentionRule {
  id: string;
  name: string;
  mediaType: "movies" | "tv" | string;
  isEnabled: boolean;
  conditions: string[];
  mediaServerIds: string[];
  removeFromServers: boolean;
  scheduleMode: "manual" | "preview" | "cleanup" | string;
  scheduleIntervalHours: number;
  lastScheduledRunUtc: string | null;
}

interface RetentionField {
  id: string;
  label: string;
  hint: string;
  group: string;
  valueKind: string;
  operators: string[];
  options: string[] | null;
  needsMediaServer: boolean;
}

interface RetentionRunItem {
  entityId: string;
  mediaType: string;
  title: string;
  year: number | null;
  sizeBytes: number | null;
  reasons: string[];
  outcome: "matched" | "excluded-unreachable" | "removed" | "changed" | "failed" | string;
  message: string | null;
}

interface RetentionRun {
  id: string;
  ruleId: string;
  kind: "preview" | "cleanup" | string;
  trigger: string;
  startedUtc: string;
  matchedCount: number;
  excludedCount: number;
  removedCount: number;
  failedCount: number;
  bytes: number;
  summary: string;
  items?: RetentionRunItem[] | null;
}

interface ConditionRow {
  field: string;
  operator: string;
  value: string;
}

interface RuleForm {
  name: string;
  mediaType: string;
  isEnabled: boolean;
  conditions: ConditionRow[];
  mediaServerIds: string[];
  removeFromServers: boolean;
  scheduleMode: string;
  scheduleIntervalHours: string;
}

const MEDIA_TYPES = [
  { value: "movies", label: "Movies" },
  { value: "tv", label: "TV shows" }
];

const SCHEDULE_MODES = [
  { value: "manual", label: "Only when I run it" },
  { value: "preview", label: "Preview on a schedule (removes nothing)" },
  { value: "cleanup", label: "Clean up on a schedule (removes without asking)" }
];

const INTERVALS = [
  { value: "6", label: "Every 6 hours" },
  { value: "24", label: "Every day" },
  { value: "168", label: "Every week" }
];

/** Watch fields use the server's wording; everything else the library filter's. */
const WATCH_OPERATOR_LABELS: Record<string, string> = { beyond: "more than … days ago", within: "within the last … days", unset: "never", is: "is" };

export async function retentionLoader() {
  const [rules, servers, movieFields, tvFields] = await Promise.all([
    fetchJson<RetentionRule[]>("/api/retention/rules"),
    fetchJson<MediaServerConnection[]>("/api/media-servers"),
    fetchJson<RetentionField[]>("/api/retention/fields?mediaType=movies"),
    fetchJson<RetentionField[]>("/api/retention/fields?mediaType=tv")
  ]);
  return { rules, servers, fields: { movies: movieFields, tv: tvFields } as Record<string, RetentionField[]> };
}

function parseCondition(raw: string): ConditionRow {
  const [field = "", operator = "", ...rest] = raw.split(":");
  return { field, operator, value: rest.join(":").split("|").join(", ") };
}

function encodeCondition(row: ConditionRow) {
  const values = row.value.split(",").map((value) => value.trim()).filter(Boolean);
  return values.length ? `${row.field}:${row.operator}:${values.join("|")}` : `${row.field}:${row.operator}`;
}

function formFrom(rule: RetentionRule | null): RuleForm {
  return rule
    ? {
        name: rule.name,
        mediaType: rule.mediaType,
        isEnabled: rule.isEnabled,
        conditions: rule.conditions.map(parseCondition),
        mediaServerIds: rule.mediaServerIds,
        removeFromServers: rule.removeFromServers,
        scheduleMode: rule.scheduleMode,
        scheduleIntervalHours: String(rule.scheduleIntervalHours)
      }
    : {
        name: "",
        mediaType: "movies",
        isEnabled: true,
        conditions: [{ field: "watched", operator: "is", value: "true" }, { field: "lastPlayed", operator: "beyond", value: "90" }],
        mediaServerIds: [],
        removeFromServers: true,
        scheduleMode: "manual",
        scheduleIntervalHours: "24"
      };
}

function payload(form: RuleForm) {
  return {
    name: form.name,
    mediaType: form.mediaType,
    isEnabled: form.isEnabled,
    conditions: form.conditions.filter((row) => row.field && row.operator).map(encodeCondition),
    mediaServerIds: form.mediaServerIds,
    removeFromServers: form.removeFromServers,
    scheduleMode: form.scheduleMode,
    scheduleIntervalHours: Number(form.scheduleIntervalHours)
  };
}

function operatorLabel(field: RetentionField | undefined, operator: string) {
  if (field?.group === "watching") return WATCH_OPERATOR_LABELS[operator] ?? operator;
  return OPERATOR_LABELS[operator as FilterOperator] ?? operator;
}

function describeRow(row: ConditionRow, fields: RetentionField[]) {
  const field = fields.find((item) => item.id === row.field);
  const label = field?.label ?? row.field;
  if (row.field === "watched") return row.value === "false" ? "Not watched" : "Watched";
  if (row.field === "lastPlayed") return row.operator === "unset" ? "Never played" : `Last played ${row.operator === "beyond" ? "over" : "within"} ${row.value} days ago`;
  return `${label} ${operatorLabel(field, row.operator)}${row.value ? ` ${row.value}` : ""}`;
}

function scheduleChip(rule: RetentionRule): { tone: "idle" | "info" | "warn"; label: string } {
  if (!rule.isEnabled) return { tone: "idle", label: "Off" };
  if (rule.scheduleMode === "cleanup") return { tone: "warn", label: "Cleans up on schedule" };
  if (rule.scheduleMode === "preview") return { tone: "info", label: "Previews on schedule" };
  return { tone: "idle", label: "Run by hand" };
}

const OUTCOME: Record<string, { tone: "ok" | "warn" | "bad" | "idle" | "info"; label: string }> = {
  matched: { tone: "info", label: "Would go" },
  "excluded-unreachable": { tone: "warn", label: "Left alone" },
  removed: { tone: "ok", label: "Removed" },
  changed: { tone: "idle", label: "Changed" },
  failed: { tone: "bad", label: "Failed" }
};

/**
 * Retention rules (#494): describe what should be cleaned out, see every title
 * that matches and why, then remove exactly those.
 *
 * Nothing is removed from this page without a preview and a confirmation, and
 * nothing is removed on a schedule unless the rule says so in as many words.
 */
export function RetentionPage() {
  const { rules, servers, fields } = useLoaderData() as Awaited<ReturnType<typeof retentionLoader>>;
  const revalidator = useRevalidator();
  const { preferences } = useDisplayPreferences();

  const [drawer, setDrawer] = useState<{ open: boolean; id: string | null }>({ open: false, id: null });
  const [form, setForm] = useState<RuleForm>(formFrom(null));
  const [initial, setInitial] = useState<RuleForm>(formFrom(null));
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [preview, setPreview] = useState<RetentionRun | null>(null);
  const [cleanup, setCleanup] = useState<RetentionRun | null>(null);
  const [history, setHistory] = useState<RetentionRun[]>([]);
  const [confirmCleanup, setConfirmCleanup] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);

  const editing = drawer.id ? rules.find((rule) => rule.id === drawer.id) ?? null : null;
  const ruleFields = fields[form.mediaType] ?? [];
  const dirty = drawer.open && JSON.stringify(payload(form)) !== JSON.stringify(payload(initial));
  const footerState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const serverNames = useMemo(() => new Map(servers.map((server) => [server.id, server.name])), [servers]);
  const matched = preview?.items?.filter((item) => item.outcome === "matched") ?? [];

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
    setPreview(null);
    setCleanup(null);
    setHistory([]);
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

  function setCondition(index: number, patch: Partial<ConditionRow>) {
    setForm((current) => ({
      ...current,
      conditions: current.conditions.map((row, position) => {
        if (position !== index) return row;
        const next = { ...row, ...patch };
        if (patch.field) {
          const field = ruleFields.find((item) => item.id === patch.field);
          next.operator = field?.operators[0] ?? "";
          next.value = field?.valueKind === "boolean" ? "true" : "";
        }
        return next;
      })
    }));
  }

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
      setForm(settled);
      setInitial(settled);
      setDrawer({ open: true, id: saved.id });
      setSaveState("saved");
      setSaveMessage(editing ? "Saved just now" : "Rule added");
      setPreview(null);
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  async function runPreview() {
    if (!editing) return;
    setBusy("preview");
    setCleanup(null);
    try {
      const response = await authedFetch(`/api/retention/rules/${editing.id}/preview`, { method: "POST" });
      if (!response.ok) throw new Error(`The preview could not run (HTTP ${response.status}).`);
      setPreview((await response.json()) as RetentionRun);
      void loadHistory(editing.id);
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "The preview could not run.");
    } finally {
      setBusy(null);
    }
  }

  async function runCleanup() {
    if (!preview || !editing) return;
    setBusy("cleanup");
    try {
      const response = await authedFetch(`/api/retention/runs/${preview.id}/cleanup`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ entityIds: null })
      });
      const body = await response.json().catch(() => null);
      if (!response.ok) throw new Error((body as { message?: string } | null)?.message ?? "The cleanup could not run.");
      setCleanup(body as RetentionRun);
      setPreview(null);
      setConfirmCleanup(false);
      void loadHistory(editing.id);
      revalidator.revalidate();
    } catch (error) {
      setConfirmCleanup(false);
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "The cleanup could not run.");
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

  const shownRun = cleanup ?? preview;
  // The run on screen above is not repeated in its own history.
  const pastRuns = history.filter((run) => run.id !== shownRun?.id);

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={automationNavItems}
        actions={<PageToolbarAction type="button" onClick={() => open(null)}>New rule</PageToolbarAction>}
      />

      <ListCard title="Retention rules" count={rules.length ? `${rules.filter((rule) => rule.isEnabled).length} of ${rules.length} on` : undefined}>
        {rules.length === 0 ? (
          <ListEmpty
            title="No retention rules"
            description="Describe what should be cleaned out - for example films everyone has watched and nobody has played for three months - and preview exactly which titles that means before anything is removed."
          />
        ) : (
          <ListTable
            columns={[
              { label: "Rule" },
              { label: "Selects" },
              { label: "Media servers" },
              { label: "Last scheduled run" },
              { label: "Status", width: LIST_TRACK.status, mobile: true },
              { label: "On", width: LIST_TRACK.toggle, mobile: true }
            ]}
          >
            {rules.map((rule) => {
              const chip = scheduleChip(rule);
              const conditions = rule.conditions.map((raw) => describeRow(parseCondition(raw), fields[rule.mediaType] ?? []));
              return (
                <ListRow key={rule.id} onClick={() => open(rule)} selected={drawer.open && drawer.id === rule.id}>
                  <ListNameCell name={rule.name} sub={rule.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={conditions[0] ?? "—"} secondary={conditions.length > 1 ? `and ${conditions.slice(1).join(", ")}` : undefined} />
                  <ListCell
                    primary={rule.mediaServerIds.length ? rule.mediaServerIds.map((id) => serverNames.get(id) ?? "Removed server").join(", ") : <span className="text-muted-foreground">None</span>}
                    secondary={rule.mediaServerIds.length ? (rule.removeFromServers ? "Removes from them too" : "Reads watch history only") : undefined}
                  />
                  <ListCell primary={rule.lastScheduledRunUtc ? formatDateTime(rule.lastScheduledRunUtc, preferences) : "—"} />
                  <ListCell mobile><Chip tone={chip.tone}>{chip.label}</Chip></ListCell>
                  <ListCell mobile>
                    <Switch size="sm" aria-label={`${rule.isEnabled ? "Turn off" : "Turn on"} ${rule.name}`} checked={rule.isEnabled} disabled={busy === `toggle:${rule.id}`} onCheckedChange={(checked) => void toggle(rule, checked)} />
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={drawer.open}
        onOpenChange={(next) => {
          if (!next) requestClose();
        }}
        title={editing?.name ?? (form.name || "New retention rule")}
        description={editing ? `${editing.mediaType === "tv" ? "TV shows" : "Movies"} · ${scheduleChip(editing).label}` : "Choose what should be cleaned out. You will see every title it selects before anything is removed."}
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter state={footerState} message={saveMessage} saveLabel={editing ? "Save rule" : "Add rule"} onCancel={requestClose} saveEnabled={editing ? undefined : true} disabled={busy !== null} />
        }
      >
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Name">
              <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} placeholder="Watched films" autoComplete="off" />
            </Field>
            <Field label="Cleans out">
              <Select
                value={form.mediaType}
                disabled={Boolean(editing)}
                onChange={(event) => setForm((current) => ({ ...current, mediaType: event.target.value, conditions: formFrom(null).conditions }))}
                options={MEDIA_TYPES}
              />
            </Field>
          </FieldRow>
          <SwitchRow label="Enabled" description="A rule that is off never runs on its schedule." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>

        <DrawerSection title="Conditions" aside="every one must hold">
          {form.conditions.map((row, index) => {
            const field = ruleFields.find((item) => item.id === row.field);
            const takesValue = row.operator !== "set" && row.operator !== "unset";
            return (
              <div key={index} className="grid grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,1fr)_auto] items-end gap-2">
                <Field label={index === 0 ? "Field" : undefined}>
                  <Select
                    aria-label={`Condition ${index + 1} field`}
                    value={row.field}
                    onChange={(event) => setCondition(index, { field: event.target.value })}
                    options={ruleFields.map((item) => ({ value: item.id, label: item.needsMediaServer ? `${item.label} (media servers)` : item.label }))}
                  />
                </Field>
                <Field label={index === 0 ? "Test" : undefined}>
                  <Select aria-label={`Condition ${index + 1} test`} value={row.operator} onChange={(event) => setCondition(index, { operator: event.target.value })} options={(field?.operators ?? []).map((operator) => ({ value: operator, label: operatorLabel(field, operator) }))} />
                </Field>
                <Field label={index === 0 ? "Value" : undefined}>
                  {field?.valueKind === "boolean" ? (
                    <Select aria-label={`Condition ${index + 1} value`} value={row.value} onChange={(event) => setCondition(index, { value: event.target.value })} options={[{ value: "true", label: "Yes" }, { value: "false", label: "No" }]} />
                  ) : field?.options?.length ? (
                    <Select aria-label={`Condition ${index + 1} value`} value={row.value} onChange={(event) => setCondition(index, { value: event.target.value })} options={field.options.map((option) => ({ value: option, label: option }))} />
                  ) : (
                    <Input aria-label={`Condition ${index + 1} value`} value={row.value} disabled={!takesValue} onChange={(event) => setCondition(index, { value: event.target.value })} placeholder={takesValue ? (row.operator === "beyond" || row.operator === "within" ? "days" : "value") : ""} />
                  )}
                </Field>
                <Button type="button" variant="ghost" size="icon" aria-label={`Remove condition ${index + 1}`} onClick={() => setForm((current) => ({ ...current, conditions: current.conditions.filter((_, position) => position !== index) }))}>
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              </div>
            );
          })}
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => setForm((current) => ({ ...current, conditions: [...current.conditions, { field: ruleFields[0]?.id ?? "", operator: ruleFields[0]?.operators[0] ?? "", value: ruleFields[0]?.valueKind === "boolean" ? "true" : "" }] }))}>
            <Plus className="h-3.5 w-3.5" />
            Add condition
          </Button>
        </DrawerSection>

        <DrawerSection title="Media servers" aside={servers.length ? undefined : "none connected"}>
          {servers.length === 0 ? (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">Connect Plex, Jellyfin or Emby under Find &amp; Download → Media Servers to use watch history.</p>
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
            label="Remove cleaned-up titles from these servers"
            description="If any of them cannot be asked when the rule runs, nothing is selected at all."
            checked={form.removeFromServers}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, removeFromServers: checked }))}
          />
        </DrawerSection>

        <DrawerSection title="Schedule">
          <FieldRow>
            <Field label="Runs" help={form.scheduleMode === "cleanup" ? "Removes everything it selects without showing you first. History still records every title." : undefined}>
              <Select value={form.scheduleMode} onChange={(event) => setForm((current) => ({ ...current, scheduleMode: event.target.value }))} options={SCHEDULE_MODES} />
            </Field>
            <Field label="How often">
              <Select value={form.scheduleIntervalHours} disabled={form.scheduleMode === "manual"} onChange={(event) => setForm((current) => ({ ...current, scheduleIntervalHours: event.target.value }))} options={INTERVALS} />
            </Field>
          </FieldRow>
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Preview" aside={dirty ? "save first" : undefined}>
            <p className="text-[length:var(--type-caption)] text-muted-foreground">Removes nothing. Lists every title the rule selects now, why, and how much space it would free.</p>
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => void runPreview()} disabled={busy !== null || dirty}>
                {busy === "preview" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Eye className="h-3.5 w-3.5" />}
                Preview
              </Button>
              {preview && matched.length > 0 ? (
                <Button type="button" variant="destructive" size="sm" onClick={() => setConfirmCleanup(true)} disabled={busy !== null || dirty}>
                  Clean up {matched.length} {matched.length === 1 ? "title" : "titles"}…
                </Button>
              ) : null}
            </div>
            {shownRun ? (
              <div className="grid gap-2" role="status">
                <p className="text-[length:var(--type-body-sm)] text-foreground">{shownRun.summary}</p>
                {/* Titles left alone because a server could not be asked all share
                    one reason, which the summary above already says. */}
                {(shownRun.items ?? []).filter((item) => item.outcome !== "excluded-unreachable").map((item) => {
                  const outcome = OUTCOME[item.outcome] ?? { tone: "idle" as const, label: item.outcome };
                  return (
                    <div key={item.entityId} className="grid gap-1 rounded-[10px] border border-hairline px-[var(--field-pad-x)] py-2">
                      <div className="flex items-center justify-between gap-2">
                        <span className="min-w-0 truncate text-[length:var(--type-body-sm)] font-medium text-foreground">
                          {item.title}{item.year ? ` (${item.year})` : ""}
                        </span>
                        <span className="flex shrink-0 items-center gap-2">
                          {item.sizeBytes ? <span className="text-[length:var(--type-caption)] text-muted-foreground">{formatBytes(item.sizeBytes)}</span> : null}
                          <Chip tone={outcome.tone}>{outcome.label}</Chip>
                        </span>
                      </div>
                      <ul className="grid gap-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                        {item.reasons.map((reason) => <li key={reason}>{reason}</li>)}
                      </ul>
                      {item.message ? <p className="text-[length:var(--type-caption)] text-foreground">{item.message}</p> : null}
                    </div>
                  );
                })}
              </div>
            ) : null}
          </DrawerSection>
        ) : null}

        {editing && pastRuns.length > 0 ? (
          <DrawerSection title="History">
            <dl className="grid grid-cols-[150px_1fr] gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
              {pastRuns.map((run) => (
                <div key={run.id} className="contents">
                  <dt className="text-muted-foreground">
                    {formatDateTime(run.startedUtc, preferences)}
                    <span className="block text-[length:var(--type-caption)]">{run.kind === "cleanup" ? "Cleanup" : "Preview"}{run.trigger === "schedule" ? " · scheduled" : ""}</span>
                  </dt>
                  <dd className="text-foreground">{run.summary}</dd>
                </div>
              ))}
            </dl>
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger
              title="Delete this rule"
              description="Titles it already removed stay removed; their exclusions stay until you restore them."
              action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmDelete(true)} disabled={busy !== null}>Delete…</Button>}
            />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmCleanup}
        onOpenChange={setConfirmCleanup}
        title={`Remove ${matched.length} ${matched.length === 1 ? "title" : "titles"}?`}
        description={`Deluno removes the titles this preview listed that still match: their files go to the recycle bin (${formatBytes(matched.reduce((sum, item) => sum + (item.sizeBytes ?? 0), 0))}), each gets an exclusion saying why so no list adds it back${form.removeFromServers && form.mediaServerIds.length ? ", and the media servers are told they are gone" : ""}. Exclusions can be restored under Discover Media → Import Lists.`}
        confirmLabel="Remove titles"
        confirmVariant="destructive"
        busy={busy === "cleanup"}
        onConfirm={() => void runCleanup()}
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
