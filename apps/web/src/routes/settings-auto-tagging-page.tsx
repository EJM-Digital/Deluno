/**
 * Auto-tagging — list → drawer.
 *
 *   PageToolbar (Media Management tabs · Run the rules now · New rule)
 *   ListCard  (name · when · gives · applies to · status · on · ›)
 *   Rule drawer (Rule · Conditions · Status · Delete)
 *
 * A rule is tags plus conditions. Conditions of one kind are alternatives, kinds
 * combine with "and", and each can be turned around with "is not" — the same
 * rules Sonarr and Radarr use. Nothing exists on a new install (#936).
 *
 * Contracts: GET/POST /api/auto-tag-rules, PUT/DELETE /api/auto-tag-rules/{id},
 * POST /api/auto-tag-rules/run.
 */
import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { Play, Plus, X } from "lucide-react";
import { TagPicker, joinTagList, parseTagList } from "../components/app/tag-picker";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { readErrorText, fetchJson, type LibraryItem, type QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import {
  CONDITION_LABELS,
  CONDITION_TYPES,
  conditionAppliesTo,
  describeConditions,
  emptyCondition,
  isAddTimeOnlyCondition,
  isRangeCondition,
  isYesNoCondition,
  joinRange,
  ruleProblem,
  scopeLabel,
  splitRange,
  type AutoTagCondition,
  type AutoTagConditionType,
  type AutoTagRuleItem
} from "../lib/auto-tagging";
import { settingsOverviewLoader } from "./settings-overview-page";

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  rules: AutoTagRuleItem[];
}

interface RuleForm {
  name: string;
  tags: string;
  removeAutomatically: boolean;
  appliesTo: "both" | "movies" | "series";
  isEnabled: boolean;
  specifications: AutoTagCondition[];
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsAutoTaggingLoader(): Promise<LoaderData> {
  const [overview, rules] = await Promise.all([settingsOverviewLoader(), fetchJson<AutoTagRuleItem[]>("/api/auto-tag-rules")]);
  return { libraries: overview.libraries, qualityProfiles: overview.qualityProfiles, rules };
}

const SERIES_TYPE_OPTIONS = [
  { value: "standard", label: "Standard" },
  { value: "daily", label: "Daily" },
  { value: "anime", label: "Anime" }
];

const YES_NO_OPTIONS = [
  { value: "true", label: "Yes" },
  { value: "false", label: "No" }
];

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["LibraryChanged", "QualityProfileChanged"], areas: [ConfigAreas.autoTagging, ConfigAreas.tags] };

export function SettingsAutoTaggingPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { rules, libraries, qualityProfiles } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<RuleForm>(emptyForm);
  const [initialForm, setInitialForm] = useState<RuleForm>(emptyForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [confirmRun, setConfirmRun] = useState(false);
  const [busy, setBusy] = useState(false);
  const [togglingId, setTogglingId] = useState<string | null>(null);

  const names = useMemo(
    () => ({
      qualityProfile: (id: string) => qualityProfiles.find((profile) => profile.id === id)?.name ?? "a profile that no longer exists",
      library: (id: string) => libraries.find((library) => library.id === id)?.name ?? "a library that no longer exists"
    }),
    [libraries, qualityProfiles]
  );
  const rootFolders = useMemo(() => [...new Set(libraries.map((library) => library.rootPath).filter(Boolean))], [libraries]);

  const isRuleDrawer = mode.kind === "create" || mode.kind === "edit";
  const editing = mode.kind === "edit" ? rules.find((rule) => rule.id === mode.id) ?? null : null;
  const dirty = isRuleDrawer && JSON.stringify(form) !== JSON.stringify(initialForm);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  function openRule(rule: AutoTagRuleItem | null) {
    const next = rule ? formFrom(rule) : emptyForm();
    setMode(rule ? { kind: "edit", id: rule.id } : { kind: "create" });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
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

  function updateCondition(index: number, change: Partial<AutoTagCondition>) {
    setErrors((current) => ({ ...current, specifications: "" }));
    setForm((current) => ({
      ...current,
      specifications: current.specifications.map((condition, position) => (position === index ? { ...condition, ...change } : condition))
    }));
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy || !isRuleDrawer) return;
    const tags = parseTagList(form.tags);
    const problems = ruleProblem({ name: form.name, tags, specifications: form.specifications });
    setErrors(problems);
    if (Object.keys(problems).length) return;

    setBusy(true);
    setSaveState("saving");
    try {
      const payload = {
        name: form.name.trim(),
        tags,
        removeAutomatically: form.removeAutomatically,
        appliesTo: form.appliesTo,
        isEnabled: form.isEnabled,
        specifications: form.specifications.map((condition) => ({ ...condition, value: condition.value.trim() }))
      };
      const response = await authedFetch(mode.kind === "edit" ? `/api/auto-tag-rules/${mode.id}` : "/api/auto-tag-rules", {
        method: mode.kind === "edit" ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      if (!response.ok) throw new Error(await readErrorText(response, mode.kind === "edit" ? "The rule could not be saved." : "The rule could not be created."));
      if (mode.kind === "create") {
        const created = (await response.json()) as AutoTagRuleItem;
        setMode({ kind: "edit", id: created.id });
        setSaveMessage("Rule created");
      } else {
        setSaveMessage("Saved just now");
      }
      const settled = { ...form, name: payload.name, tags: joinTagList(tags) };
      setForm((current) => settleFormAfterSave(current, form, settled));
      setInitialForm(settled);
      setSaveState("saved");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(false);
    }
  }

  async function handleRemove() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/auto-tag-rules/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error(await readErrorText(response, "The rule could not be deleted."));
      toast.success(`${editing?.name ?? "Rule"} deleted`);
      setConfirmRemove(false);
      setInitialForm(form);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The rule could not be deleted.");
    } finally {
      setBusy(false);
    }
  }

  async function toggleEnabled(rule: AutoTagRuleItem, isEnabled: boolean) {
    setTogglingId(rule.id);
    try {
      const response = await authedFetch(`/api/auto-tag-rules/${rule.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...rule, isEnabled })
      });
      if (!response.ok) throw new Error(await readErrorText(response, `Could not ${isEnabled ? "turn on" : "pause"} ${rule.name}.`));
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The rule could not be updated.");
    } finally {
      setTogglingId(null);
    }
  }

  async function runNow() {
    setBusy(true);
    try {
      const result = await fetchJson<{ changedTitles: number }>("/api/auto-tag-rules/run", { method: "POST" });
      toast.success(result.changedTitles === 0 ? "Nothing needed tagging." : `Changed the tags on ${result.changedTitles} ${result.changedTitles === 1 ? "title" : "titles"}. Activity has the details.`);
      setConfirmRun(false);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The rules could not be run.");
    } finally {
      setBusy(false);
    }
  }

  function valueControl(condition: AutoTagCondition, index: number) {
    const label = `Condition ${index + 1} value`;
    if (isYesNoCondition(condition.type)) {
      return <Select aria-label={label} value={condition.value === "false" ? "false" : "true"} onChange={(event) => updateCondition(index, { value: event.target.value })} options={YES_NO_OPTIONS} />;
    }
    if (isRangeCondition(condition.type)) {
      const range = splitRange(condition.value);
      return (
        <div className="flex items-center gap-2">
          <Input aria-label={`Condition ${index + 1} from`} inputMode="numeric" placeholder="From" value={range.from} onChange={(event) => updateCondition(index, { value: joinRange(event.target.value, range.to) })} />
          <span className="text-muted-foreground">to</span>
          <Input aria-label={`Condition ${index + 1} to`} inputMode="numeric" placeholder="To" value={range.to} onChange={(event) => updateCondition(index, { value: joinRange(range.from, event.target.value) })} />
        </div>
      );
    }
    switch (condition.type) {
      case "qualityProfile":
        return <Select aria-label={label} value={condition.value} placeholder="Choose a quality profile" onChange={(event) => updateCondition(index, { value: event.target.value })} options={qualityProfiles.map((profile) => ({ value: profile.id, label: profile.name }))} />;
      case "library":
        return <Select aria-label={label} value={condition.value} placeholder="Choose a library" onChange={(event) => updateCondition(index, { value: event.target.value })} options={libraries.map((library) => ({ value: library.id, label: library.name }))} />;
      case "rootFolder":
        return <Select aria-label={label} value={condition.value} placeholder="Choose a folder" onChange={(event) => updateCondition(index, { value: event.target.value })} options={rootFolders.map((folder) => ({ value: folder, label: folder }))} />;
      case "seriesType":
        return <Select aria-label={label} value={condition.value} placeholder="Choose a type" onChange={(event) => updateCondition(index, { value: event.target.value })} options={SERIES_TYPE_OPTIONS} />;
      case "tag":
        return <TagPicker single label={label} value={condition.value} onChange={(value) => updateCondition(index, { value })} warnUnused={false} />;
      default:
        return <Input aria-label={label} value={condition.value} placeholder={PLACEHOLDERS[condition.type] ?? ""} onChange={(event) => updateCondition(index, { value: event.target.value })} autoComplete="off" />;
    }
  }

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        tabs={librarySetupNavItems}
        actions={
          <>
            <Button type="button" variant="outline" onClick={() => setConfirmRun(true)} disabled={rules.length === 0 || busy}>
              <Play className="h-4 w-4" />
              Run the rules now
            </Button>
            <PageToolbarAction onClick={() => openRule(null)}>New rule</PageToolbarAction>
          </>
        }
      />

      <ListCard title={rules.length ? `${rules.length} ${rules.length === 1 ? "rule" : "rules"} · ${rules.filter((rule) => rule.isEnabled).length} on` : "0 rules"} titleCase={false}>
        {rules.length === 0 ? (
          <ListEmpty
            title="No auto-tagging rules"
            description="Deluno tags nothing on its own until you add a rule. A rule gives tags to every title that meets its conditions, so you do not have to tag them by hand."
            actions={
              <Button type="button" size="sm" onClick={() => openRule(null)}>
                <Plus className="h-3.5 w-3.5" />
                Add the first rule
              </Button>
            }
          />
        ) : (
          <ListTable
            listId="auto-tagging"
            items={rules}
            columns={[
              { id: "name", label: "Name", sort: (rule) => rule.name.toLowerCase() },
              { id: "when", label: "When", width: "minmax(0,1.6fr)", sort: (rule) => describeConditions(rule.specifications, names).toLowerCase() },
              { id: "gives", label: "Gives", sort: (rule) => rule.tags.join(", ").toLowerCase() },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (rule) => rule.isEnabled },
              { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (rule) => rule.isEnabled }
            ]}
          >
            {(rows) => rows.map((rule) => (
              <ListRow key={rule.id} onClick={() => openRule(rule)} selected={mode.kind === "edit" && mode.id === rule.id}>
                <ListNameCell name={rule.name} sub={scopeLabel(rule.appliesTo)} />
                <ListCell primary={describeConditions(rule.specifications, names)} secondary={rule.removeAutomatically ? "Takes the tags back when a title stops matching" : undefined} />
                <ListCell>
                  <div className="flex flex-wrap gap-1.5">
                    {rule.tags.map((tag) => (
                      <Chip key={tag} tone="info" dot={false}>
                        {tag}
                      </Chip>
                    ))}
                  </div>
                </ListCell>
                <ListCell mobile>
                  <Chip tone={rule.isEnabled ? "ok" : "idle"}>{rule.isEnabled ? "On" : "Paused"}</Chip>
                </ListCell>
                <ListCell mobile>
                  <Switch size="sm" aria-label={`${rule.isEnabled ? "Pause" : "Turn on"} ${rule.name}`} checked={rule.isEnabled} disabled={togglingId === rule.id} onCheckedChange={(checked) => void toggleEnabled(rule, checked)} />
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={isRuleDrawer}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={mode.kind === "create" ? "New auto-tagging rule" : editing?.name ?? form.name}
        description={mode.kind === "create" ? "Give tags to every title that meets the conditions." : describeConditions(form.specifications, names) || "Give tags to every title that meets the conditions."}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create rule" : "Save rule"} onCancel={requestClose} disabled={busy} />}
      >
        {isRuleDrawer ? (
          <>
            <DrawerSection title="Rule">
              <Field label="Name" error={errors.name}>
                <Input value={form.name} onChange={(event) => { setErrors((current) => ({ ...current, name: "" })); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Example: anime" autoComplete="off" />
              </Field>
              <Field label="Applies to">
                <SegmentedControl<"both" | "movies" | "series"> aria-label="Applies to" value={form.appliesTo} onValueChange={(appliesTo) => setForm((current) => ({ ...current, appliesTo, specifications: current.specifications.filter((condition) => conditionAppliesTo(condition.type, appliesTo)) }))} options={[{ value: "both", label: "Both" }, { value: "movies", label: "Movies" }, { value: "series", label: "TV shows" }]} />
              </Field>
              <Field label="Tags to give" error={errors.tags} help="Every title that meets the conditions gets these tags.">
                <TagPicker label="Tags to give" value={form.tags} onChange={(tags) => { setErrors((current) => ({ ...current, tags: "" })); setForm((current) => ({ ...current, tags })); }} warnUnused={false} />
              </Field>
              <SwitchRow
                label="Remove tags automatically"
                description="When a title stops meeting the conditions, take these tags back off it. Off, the tags stay once given."
                checked={form.removeAutomatically}
                onCheckedChange={(checked) => setForm((current) => ({ ...current, removeAutomatically: checked }))}
              />
            </DrawerSection>

            <DrawerSection title="Conditions" aside="Any one of a kind · all kinds">
              <p className="text-(length:--type-caption) text-muted-foreground">
                Conditions of the same kind are alternatives, so any one will do. Conditions of different kinds must all be met. “Is not” turns a condition around.
              </p>
              {errors.specifications ? <p className="text-(length:--type-caption) text-destructive">{errors.specifications}</p> : null}
              {form.specifications.map((condition, index) => (
                <div key={index} className="grid gap-2 rounded-(--radius-control) border border-border p-(--card-pad-x)">
                  <div className="grid grid-cols-[minmax(0,1fr)_7rem_auto] items-center gap-2">
                    <Select
                      aria-label={`Condition ${index + 1} kind`}
                      value={condition.type}
                      onChange={(event) => updateCondition(index, emptyCondition(event.target.value as AutoTagConditionType))}
                      options={CONDITION_TYPES.filter((type) => conditionAppliesTo(type, form.appliesTo)).map((type) => ({ value: type, label: CONDITION_LABELS[type] }))}
                    />
                    <Select
                      aria-label={`Condition ${index + 1} is or is not`}
                      value={condition.negate ? "not" : "is"}
                      onChange={(event) => updateCondition(index, { negate: event.target.value === "not" })}
                      options={[{ value: "is", label: "is" }, { value: "not", label: "is not" }]}
                    />
                    <Button type="button" variant="ghost" size="sm" aria-label={`Remove condition ${index + 1}`} onClick={() => setForm((current) => ({ ...current, specifications: current.specifications.filter((_, position) => position !== index) }))}>
                      <X className="h-4 w-4" />
                    </Button>
                  </div>
                  {valueControl(condition, index)}
                  {isAddTimeOnlyCondition(condition.type) ? (
                    <p className="text-(length:--type-caption) text-muted-foreground">Only checked when a title is added. A later edit or refresh leaves the tags this rule gave as they are.</p>
                  ) : null}
                </div>
              ))}
              <div>
                <Button type="button" variant="outline" size="sm" onClick={() => setForm((current) => ({ ...current, specifications: [...current.specifications, emptyCondition("genre")] }))}>
                  <Plus className="h-3.5 w-3.5" />
                  Add a condition
                </Button>
              </div>
            </DrawerSection>

            <DrawerSection title="Status">
              <SwitchRow label="Use this rule" description="When paused, the rule gives and removes nothing." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
            </DrawerSection>

            {editing ? (
              <DrawerSection>
                <DrawerDanger title="Delete this rule" description="Tags it already gave stay on their titles." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>

      <ConfirmDialog open={confirmRemove} onOpenChange={setConfirmRemove} title={`Delete “${editing?.name ?? form.name}”?`} description="Tags it already gave stay on their titles. It will not tag anything new." confirmLabel="Delete rule" busy={busy} onConfirm={() => void handleRemove()} />
      <ConfirmDialog open={confirmRun} onOpenChange={setConfirmRun} title="Run the rules over every title?" description="Deluno checks each title in your library against every rule that is on, gives the tags that fit, and takes back the tags of rules set to remove automatically. Each change is written to Activity." confirmLabel="Run the rules" busy={busy} onConfirm={() => void runNow()} />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this rule haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

const PLACEHOLDERS: Partial<Record<AutoTagConditionType, string>> = {
  genre: "Example: Animation",
  originalLanguage: "Example: ja or Japanese",
  status: "Example: Ended",
  keyword: "Example: time loop",
  studio: "Example: Studio Ghibli",
  certification: "Example: PG-13",
  collection: "Example: Marvel Cinematic Universe",
  sourceList: "The name of the list or collection"
};

function emptyForm(): RuleForm {
  return { name: "", tags: "", removeAutomatically: false, appliesTo: "both", isEnabled: true, specifications: [emptyCondition("genre")] };
}

function formFrom(rule: AutoTagRuleItem): RuleForm {
  return {
    name: rule.name,
    tags: joinTagList(rule.tags),
    removeAutomatically: rule.removeAutomatically,
    appliesTo: rule.appliesTo === "movies" || rule.appliesTo === "series" ? rule.appliesTo : "both",
    isEnabled: rule.isEnabled,
    specifications: rule.specifications.map((condition) => ({ type: condition.type, negate: condition.negate, value: condition.value }))
  };
}
