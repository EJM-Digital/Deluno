/**
 * Release preferences — list → drawer plus one page-level form.
 *
 *   PageToolbar (Media Plans tabs · All/Movies/TV · Test a release · New rule)
 *   ListCard  presets      (row → drawer: what it contains · Apply)
 *   ListCard  release rules (row → drawer: Basics · Conditions · Remove)
 *   ListCard  safeguards   (page form, saved by PageFooter)
 *
 * Rules score a release by its traits: Radarr and Sonarr call these Custom
 * Formats. A rule is either written here or started from the bundled TRaSH
 * guide catalogue, which is offered as "Start from" inside the rule drawer
 * rather than as a separate browsable tab.
 *
 * Contracts: GET/POST /api/custom-formats, PUT/DELETE /api/custom-formats/{id},
 * POST /api/custom-formats/dry-run, PATCH /api/settings.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { FlaskConical, Loader2, Plus, RotateCcw, X } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { ListGroupHeader, MediaTypeFilter, mediaTypeLabel, useMediaTypeSplit } from "../components/ui/media-type-split";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { configurationNavAreas } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import {
  BUNDLED_CUSTOM_FORMATS,
  CF_CATEGORY_META,
  CF_CATEGORY_ORDER,
  CUSTOM_FORMAT_BUNDLES,
  findBundledCF,
  type BundledCF,
  type CFCategory,
  type CustomFormatBundle
} from "../lib/trash-guide-data";
import {
  fetchJson,
  type CustomFormatItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";

const TABS = configurationNavAreas.find((area) => area.label === "Media plans")?.items ?? [];

/** Scores users actually reach for. Anything at or under -10000 blocks a release outright. */
const SCORE_OPTIONS = [
  { value: "-10000", label: "Block this release" },
  { value: "-100", label: "Strongly avoid (−100)" },
  { value: "-25", label: "Avoid (−25)" },
  { value: "25", label: "Mild preference (+25)" },
  { value: "100", label: "Prefer (+100)" },
  { value: "500", label: "Strongly prefer (+500)" }
];

const CONDITION_TYPES = [
  { value: "releaseTitle", label: "Release title" },
  { value: "source", label: "Source" },
  { value: "resolution", label: "Resolution" },
  { value: "hdr", label: "HDR format" },
  { value: "codec", label: "Video codec" },
  { value: "releaseGroup", label: "Release group" },
  { value: "language", label: "Language" }
] as const;

type ConditionType = (typeof CONDITION_TYPES)[number]["value"];
interface Condition {
  type: ConditionType;
  value: string;
  negate: boolean;
  required: boolean;
}

const DEFAULT_NEVER_GRAB_RULES = ["cam", "camrip", "telesync", "telecine", "workprint", "screener", "sample", "trailer", "extras"];

interface LoaderData {
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  settings: PlatformSettingsSnapshot;
}

export async function settingsCustomFormatsLoader(): Promise<LoaderData> {
  const [overview, customFormats] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats")
  ]);
  return { ...overview, customFormats };
}

interface RuleForm {
  name: string;
  mediaType: "movies" | "tv";
  score: string;
  conditions: Condition[];
  trashId: string | null;
}

interface SafeguardForm {
  scoringMode: string;
  neverGrab: string[];
}

type DrawerMode =
  | { kind: "rule"; id: string | null }
  | { kind: "preset"; id: string }
  | { kind: "test" }
  | null;

export function SettingsCustomFormatsPage() {
  const { customFormats, settings } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [busy, setBusy] = useState<string | null>(null);

  const split = useMediaTypeSplit(customFormats, (format) => format.mediaType);
  const [drawer, setDrawer] = useState<DrawerMode>(null);

  /* ------------------------------------------------------------- rules */
  const [form, setForm] = useState<RuleForm>(() => emptyRule());
  const [initialForm, setInitialForm] = useState<RuleForm>(() => emptyRule());
  const [ruleState, setRuleState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [ruleMessage, setRuleMessage] = useState<string | null>(null);

  const editing = drawer?.kind === "rule" && drawer.id ? customFormats.find((format) => format.id === drawer.id) ?? null : null;
  const ruleDirty = drawer?.kind === "rule" && !same(form, initialForm);
  const ruleFooter: DrawerSaveState = ruleState === "saving" ? "saving" : ruleDirty ? "dirty" : ruleState ?? "clean";
  useEffect(() => {
    if (ruleDirty && (ruleState === "saved" || ruleState === "error")) setRuleState(undefined);
  }, [ruleDirty, ruleState]);

  function openRule(format: CustomFormatItem | null) {
    const next = format ? ruleFrom(format) : emptyRule();
    setForm(next);
    setInitialForm(next);
    setRuleState(undefined);
    setRuleMessage(null);
    setDrawer({ kind: "rule", id: format?.id ?? null });
  }

  /** Choosing a guide format fills the whole rule in — name, score and conditions. */
  function startFrom(trashId: string) {
    const bundled = findBundledCF(trashId);
    if (!bundled) {
      setForm((current) => ({ ...current, trashId: null }));
      return;
    }
    setForm((current) => ({
      ...current,
      trashId,
      name: bundled.name,
      score: String(bundled.defaultScore),
      conditions: bundled.patterns.map((pattern) => ({ type: "releaseTitle" as ConditionType, value: pattern, negate: false, required: true }))
    }));
  }

  async function submitRule(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (ruleState === "saving") return;
    if (!form.name.trim()) {
      setRuleState("error");
      setRuleMessage("Give this rule a name.");
      return;
    }
    setRuleState("saving");
    try {
      const body = JSON.stringify({
        name: form.name.trim(),
        mediaType: form.mediaType,
        score: Number(form.score || 0),
        trashId: form.trashId,
        conditions: JSON.stringify(form.conditions.filter((condition) => condition.value.trim())),
        upgradeAllowed: true
      });
      const response = editing
        ? await authedFetch(`/api/custom-formats/${editing.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body })
        : await authedFetch("/api/custom-formats", { method: "POST", headers: { "Content-Type": "application/json" }, body });
      if (!response.ok) throw new Error(editing ? "Rule could not be saved." : "Rule could not be created.");
      const saved = (await response.json()) as CustomFormatItem;
      setInitialForm(form);
      setRuleState("saved");
      setRuleMessage("Saved just now");
      setDrawer({ kind: "rule", id: saved.id });
      revalidator.revalidate();
    } catch (error) {
      setRuleState("error");
      setRuleMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  async function removeRule(format: CustomFormatItem) {
    setBusy(`remove:${format.id}`);
    try {
      const response = await authedFetch(`/api/custom-formats/${format.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Rule could not be removed.");
      setDrawer(null);
      toast.success(`${format.name} removed`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Rule could not be removed.");
    } finally {
      setBusy(null);
    }
  }

  /* ----------------------------------------------------------- presets */
  const appliedTrashIds = useMemo(
    () => new Set(customFormats.filter((format) => format.trashId).map((format) => `${format.mediaType}:${format.trashId}`)),
    [customFormats]
  );
  const presetDetail = drawer?.kind === "preset" ? CUSTOM_FORMAT_BUNDLES.find((bundle) => bundle.id === drawer.id) ?? null : null;
  const [presetMessage, setPresetMessage] = useState<string | null>(null);
  const [presetState, setPresetState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();

  function presetProgress(bundle: CustomFormatBundle) {
    const target = bundle.mediaType === "tv" ? "tv" : "movies";
    const resolved = bundle.includes.map((entry) => findBundledCF(entry.trashId)).filter((cf): cf is BundledCF => Boolean(cf));
    const applied = resolved.filter((cf) => appliedTrashIds.has(`${target}:${cf.trashId}`));
    return { target, resolved, applied: applied.length, total: resolved.length };
  }

  async function applyPreset(bundle: CustomFormatBundle) {
    const { target, resolved, applied } = presetProgress(bundle);
    const missing = resolved.filter((cf) => !appliedTrashIds.has(`${target}:${cf.trashId}`));
    if (missing.length === 0) {
      setPresetState("saved");
      setPresetMessage("Every rule in this preset is already applied.");
      return;
    }
    setBusy(`preset:${bundle.id}`);
    setPresetState("saving");
    setPresetMessage(null);
    try {
      for (const cf of missing) {
        const score = bundle.includes.find((entry) => entry.trashId === cf.trashId)?.score ?? cf.defaultScore;
        const response = await authedFetch("/api/custom-formats", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            name: cf.name,
            mediaType: target,
            score,
            trashId: cf.trashId,
            conditions: JSON.stringify(cf.patterns.map((pattern) => ({ type: "releaseTitle", value: pattern, negate: false, required: true }))),
            upgradeAllowed: true
          })
        });
        if (!response.ok) throw new Error(`${cf.name} could not be added.`);
      }
      setPresetState("saved");
      setPresetMessage(`Added ${missing.length} ${missing.length === 1 ? "rule" : "rules"}${applied ? ` — ${applied} were already here` : ""}.`);
      revalidator.revalidate();
    } catch (error) {
      setPresetState("error");
      setPresetMessage(error instanceof Error ? error.message : "Preset could not be applied.");
    } finally {
      setBusy(null);
    }
  }

  /* -------------------------------------------------- test a release */
  const [releaseName, setReleaseName] = useState("");
  const [testScope, setTestScope] = useState<"all" | "movies" | "tv">("all");
  const [results, setResults] = useState<DryRunResult[] | null>(null);

  async function runTest() {
    if (!releaseName.trim()) return;
    setBusy("test");
    setResults(null);
    try {
      setResults(
        await fetchJson<DryRunResult[]>("/api/custom-formats/dry-run", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ releaseName: releaseName.trim(), mediaType: testScope === "all" ? null : testScope })
        })
      );
    } catch {
      setResults([]);
    } finally {
      setBusy(null);
    }
  }

  /* -------------------------------------------------------- safeguards */
  const [savedSafeguards, setSavedSafeguards] = useState<SafeguardForm>(() => safeguardsFrom(settings));
  const [safeguards, setSafeguards] = useState<SafeguardForm>(savedSafeguards);
  const [newRule, setNewRule] = useState("");
  const [safeguardState, setSafeguardState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [safeguardMessage, setSafeguardMessage] = useState<string | null>(null);

  const safeguardsDirty = !same(safeguards, savedSafeguards);
  const settingsSafeguards = useMemo(() => safeguardsFrom(settings), [settings]);
  useEffect(() => {
    if (safeguardsDirty || same(savedSafeguards, settingsSafeguards)) return;
    setSavedSafeguards(settingsSafeguards);
    setSafeguards(settingsSafeguards);
  }, [safeguardsDirty, savedSafeguards, settingsSafeguards]);

  const safeguardFooter: DrawerSaveState = safeguardState === "saving" ? "saving" : safeguardsDirty ? "dirty" : safeguardState ?? "clean";
  useUnsavedChanges(safeguardsDirty || Boolean(ruleDirty));
  useEffect(() => {
    if (safeguardsDirty && (safeguardState === "saved" || safeguardState === "error")) setSafeguardState(undefined);
  }, [safeguardsDirty, safeguardState]);

  function addNeverGrab() {
    const value = newRule.trim();
    if (!value || safeguards.neverGrab.some((rule) => rule.toLowerCase() === value.toLowerCase())) return;
    setSafeguards((current) => ({ ...current, neverGrab: [...current.neverGrab, value] }));
    setNewRule("");
  }

  async function submitSafeguards(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (safeguardState === "saving") return;
    setSafeguardState("saving");
    try {
      await settingsMutation.mutate({
          searchScoringMode: safeguards.scoringMode,
          releaseNeverGrabPatterns: safeguards.neverGrab.join("\n")
      });
      setSavedSafeguards(safeguards);
      setSafeguardState("saved");
      setSafeguardMessage("Saved just now");
    } catch (error) {
      setSafeguardState("error");
      setSafeguardMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  /* ------------------------------------------------------------ render */
  return (
    <form onSubmit={submitSafeguards} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar
        tabs={TABS}
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
            <Button type="button" variant="outline" onClick={() => setDrawer({ kind: "test" })}>
              <FlaskConical className="h-4 w-4" />
              Test a release
            </Button>
            <Button type="button" onClick={() => openRule(null)}>
              <Plus className="h-4 w-4" />
              New rule
            </Button>
          </>
        }
      />

      <ListCard title="Presets" count="Start with a goal rather than a rules list">
        <ListTable columns={[{ label: "Preset" }, { label: "Best for", width: "minmax(0,1.6fr)" }, { label: "Rules" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
          {CUSTOM_FORMAT_BUNDLES.map((bundle) => {
            const progress = presetProgress(bundle);
            const complete = progress.total > 0 && progress.applied === progress.total;
            return (
              <ListRow key={bundle.id} onClick={() => { setPresetMessage(null); setDrawer({ kind: "preset", id: bundle.id }); }} selected={drawer?.kind === "preset" && drawer.id === bundle.id}>
                <ListNameCell name={bundle.name} sub={`${bundle.level} · ${bundle.mediaType === "all" ? "Movies and TV" : mediaTypeLabel(bundle.mediaType)}`} />
                <ListCell primary={bundle.bestFor} secondary={bundle.description} />
                <ListCell numeric primary={`${progress.applied} of ${progress.total}`} secondary="applied" />
                <ListCell mobile>
                  <Chip tone={complete ? "ok" : progress.applied ? "info" : "muted"}>{complete ? "Applied" : progress.applied ? "Partly applied" : "Not applied"}</Chip>
                </ListCell>
              </ListRow>
            );
          })}
        </ListTable>
      </ListCard>

      <ListCard title="Release rules" count={customFormats.length ? `${customFormats.length} ${customFormats.length === 1 ? "rule" : "rules"}` : undefined}>
        {customFormats.length === 0 ? (
          <ListEmpty
            title="No release rules yet"
            description="Apply a preset above, or write a rule of your own. Rules add or subtract points from a release based on its traits, and the highest-scoring release wins."
            actions={<Button type="button" variant="outline" onClick={() => openRule(null)}><Plus className="h-4 w-4" />New rule</Button>}
          />
        ) : (
          <ListTable columns={[{ label: "Rule" }, { label: "Matches on", width: "minmax(0,1.4fr)" }, { label: "Score" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
            {split.groups.flatMap((group) => [
              split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
              ...group.items.map((format) => {
                const conditions = parseConditions(format.conditions);
                const guide = format.trashId ? findBundledCF(format.trashId) : undefined;
                return (
                  <ListRow key={format.id} onClick={() => openRule(format)} selected={drawer?.kind === "rule" && drawer.id === format.id}>
                    <ListNameCell name={format.name} sub={guide ? "From the guide catalogue" : "Written here"} />
                    {/* Guide rules are raw regex — lead with what they mean, not the pattern. */}
                    <ListCell
                      primary={guide?.description ?? (conditions.length ? conditionSummary(conditions) : "No conditions")}
                      secondary={conditions.length ? `${conditions.length} ${conditions.length === 1 ? "condition" : "conditions"}` : "never matches anything"}
                    />
                    <ListCell numeric primary={scoreLabel(format.score)} secondary={format.score <= -10000 ? "never grabbed" : "points"} />
                    <ListCell mobile>
                      <Chip tone={format.score <= -10000 ? "bad" : format.score > 0 ? "ok" : "warn"}>
                        {format.score <= -10000 ? "Blocked" : format.score > 0 ? "Preferred" : "Avoided"}
                      </Chip>
                    </ListCell>
                  </ListRow>
                );
              })
            ])}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Safeguards" count="The final guardrails applied after quality and score are worked out">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <Field
            label="How Deluno picks a release"
            help="Hybrid is the normal choice. Rules only makes your configured policy the sole decision-maker."
            className="max-w-[28rem]"
          >
            <Select
              value={safeguards.scoringMode}
              onChange={(event) => setSafeguards((current) => ({ ...current, scoringMode: event.target.value }))}
              options={[
                { value: "hybrid", label: "Rules and ranking together" },
                { value: "rules-only", label: "Rules only" },
                { value: "ml-only", label: "Ranking only" }
              ]}
            />
          </Field>

          <Field label="Never grab" help="Reject any release containing these words or release groups. Plain text — no regex needed.">
            <div className="grid gap-2">
              {safeguards.neverGrab.length ? (
                <div className="flex flex-wrap gap-2">
                  {safeguards.neverGrab.map((rule) => (
                    <Chip key={rule} tone="muted">
                      {rule}
                      <button
                        type="button"
                        aria-label={`Remove ${rule}`}
                        className="ml-1 text-muted-foreground transition-colors hover:text-destructive"
                        onClick={() => setSafeguards((current) => ({ ...current, neverGrab: current.neverGrab.filter((item) => item !== rule) }))}
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </Chip>
                  ))}
                </div>
              ) : null}
              <div className="flex flex-wrap gap-2">
                <Input
                  value={newRule}
                  onChange={(event) => setNewRule(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") {
                      event.preventDefault();
                      addNeverGrab();
                    }
                  }}
                  placeholder="Word, phrase, or release group"
                  aria-label="Add a never-grab word"
                  className="w-56"
                />
                <Button type="button" variant="outline" onClick={addNeverGrab}>
                  <Plus className="h-4 w-4" />
                  Add
                </Button>
                <Button type="button" variant="outline" onClick={() => setSafeguards((current) => ({ ...current, neverGrab: DEFAULT_NEVER_GRAB_RULES }))}>
                  <RotateCcw className="h-4 w-4" />
                  Restore defaults
                </Button>
              </div>
            </div>
          </Field>
        </div>
      </ListCard>

      <PageFooter state={safeguardFooter} message={safeguardMessage} saveLabel="Save safeguards" onDiscard={() => setSafeguards(savedSafeguards)} />

      {/* --------------------------------------------------- rule drawer */}
      <Drawer
        open={drawer?.kind === "rule"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={editing ? editing.name : "New release rule"}
        description={editing ? `${mediaTypeLabel(editing.mediaType)} · ${scoreLabel(editing.score)}` : "Score a release by the traits in its name"}
        onSubmit={submitRule}
        footer={
          <DrawerFooter
            state={ruleFooter}
            message={ruleMessage}
            saveLabel={editing ? "Save rule" : "Create rule"}
            onCancel={() => setDrawer(null)}
            disabled={busy !== null}
          />
        }
      >
        <DrawerSection title="Basics">
          {!editing ? (
            <Field label="Start from" optional help="Pick a rule from the bundled guide catalogue to fill this in, or leave it blank and write your own.">
              <Select value={form.trashId ?? ""} onChange={(event) => startFrom(event.target.value)} placeholder="Write my own">
                {CF_CATEGORY_ORDER.map((category) => {
                  const entries = BUNDLED_CUSTOM_FORMATS.filter((cf) => cf.category === category && !cf.bundleOnly);
                  if (!entries.length) return null;
                  return (
                    <optgroup key={category} label={CF_CATEGORY_META[category as CFCategory]?.label ?? category}>
                      {entries.map((cf) => (
                        <option key={cf.trashId} value={cf.trashId}>
                          {cf.name}
                        </option>
                      ))}
                    </optgroup>
                  );
                })}
              </Select>
            </Field>
          ) : null}
          <Field label="Name" help="What this rule is looking for, in your own words.">
            <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} placeholder="e.g. Dolby Vision" />
          </Field>
          <FieldRow>
            <Field label="Applies to">
              <SegmentedControl<"movies" | "tv">
                aria-label="Applies to"
                value={form.mediaType}
                onValueChange={(value) => setForm((current) => ({ ...current, mediaType: value }))}
                options={[
                  { value: "movies", label: "Movies" },
                  { value: "tv", label: "TV" }
                ]}
              />
            </Field>
            <Field label="Score" help="Positive prefers, negative avoids, −10000 never grabs.">
              <PresetField
                inputType="number"
                value={form.score}
                onChange={(value) => setForm((current) => ({ ...current, score: value }))}
                options={SCORE_OPTIONS}
                customLabel="Custom score"
                customPlaceholder="Points"
              />
            </Field>
          </FieldRow>
        </DrawerSection>

        <DrawerSection title="Conditions" aside={form.conditions.length ? `${form.conditions.length} · all must match` : "none yet"}>
          {form.conditions.length === 0 ? (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">
              Without a condition this rule never matches anything. Add at least one.
            </p>
          ) : null}
          {form.conditions.map((condition, index) => (
            <div key={index} className="grid gap-2 rounded-[10px] border border-hairline p-3">
              <FieldRow>
                <Field label="Look at" hideLabel={false}>
                  <Select
                    value={condition.type}
                    onChange={(event) => updateCondition(setForm, index, { type: event.target.value as ConditionType })}
                    options={CONDITION_TYPES.map((type) => ({ value: type.value, label: type.label }))}
                  />
                </Field>
                <Field label="Match">
                  <Select
                    value={condition.negate ? "not" : "is"}
                    onChange={(event) => updateCondition(setForm, index, { negate: event.target.value === "not" })}
                    options={[
                      { value: "is", label: "Contains" },
                      { value: "not", label: "Does not contain" }
                    ]}
                  />
                </Field>
              </FieldRow>
              <Field label="Text or pattern" help="Plain text is matched anywhere in the release name.">
                <Input
                  value={condition.value}
                  onChange={(event) => updateCondition(setForm, index, { value: event.target.value })}
                  placeholder="e.g. DV, DoVi, Dolby.?Vision"
                  className="font-mono"
                />
              </Field>
              <div className="flex justify-end">
                <Button
                  type="button"
                  variant="destructive"
                  size="sm"
                  onClick={() => setForm((current) => ({ ...current, conditions: current.conditions.filter((_, i) => i !== index) }))}
                >
                  Remove condition
                </Button>
              </div>
            </div>
          ))}
          <div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setForm((current) => ({ ...current, conditions: [...current.conditions, { type: "releaseTitle", value: "", negate: false, required: true }] }))}
            >
              <Plus className="h-3.5 w-3.5" />
              Add a condition
            </Button>
          </div>
        </DrawerSection>

        {editing ? (
          <DrawerSection>
            <DrawerDanger
              title="Remove this rule"
              description="Releases stop being scored by it immediately. Media already imported is untouched."
              action={
                <Button type="button" variant="destructive" size="sm" onClick={() => void removeRule(editing)} disabled={busy !== null}>
                  {busy === `remove:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Remove
                </Button>
              }
            />
          </DrawerSection>
        ) : null}
      </Drawer>

      {/* ------------------------------------------------- preset drawer */}
      <Drawer
        open={drawer?.kind === "preset"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={presetDetail?.name ?? "Preset"}
        description={presetDetail ? `${presetDetail.level} · ${presetDetail.mediaType === "all" ? "Movies and TV" : mediaTypeLabel(presetDetail.mediaType)}` : undefined}
        footer={
          <DrawerFooter
            state={presetState ?? "clean"}
            message={presetMessage}
            saveLabel="Apply preset"
            // Tool-style drawer: no form to submit, the button calls the handler directly.
            saveType="button"
            saveEnabled={presetDetail ? presetProgress(presetDetail).applied < presetProgress(presetDetail).total : false}
            onSave={() => presetDetail && void applyPreset(presetDetail)}
            onCancel={() => setDrawer(null)}
            disabled={busy !== null}
          />
        }
      >
        {presetDetail ? (
          <>
            <DrawerSection title="What this is for">
              <p className="text-[length:var(--type-body-sm)] text-muted-foreground">{presetDetail.description}</p>
              <p className="text-[length:var(--type-body-sm)] text-foreground">
                <span className="font-medium">Best for:</span> {presetDetail.bestFor}
              </p>
              {presetDetail.warnings?.length ? (
                <div className="grid gap-1">
                  {presetDetail.warnings.map((warning) => (
                    <p key={warning} className="text-[length:var(--type-caption)] text-warning">
                      {warning}
                    </p>
                  ))}
                </div>
              ) : null}
            </DrawerSection>
            <DrawerSection
              title="Rules it adds"
              aside={`${presetProgress(presetDetail).applied} of ${presetProgress(presetDetail).total} already here`}
            >
              <div className="grid gap-1.5">
                {presetProgress(presetDetail).resolved.map((cf) => {
                  const target = presetDetail.mediaType === "tv" ? "tv" : "movies";
                  const already = appliedTrashIds.has(`${target}:${cf.trashId}`);
                  const score = presetDetail.includes.find((entry) => entry.trashId === cf.trashId)?.score ?? cf.defaultScore;
                  return (
                    <div key={cf.trashId} className="flex items-center justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0">
                      <span className="min-w-0">
                        <span className="block truncate text-[length:var(--type-body-sm)] text-foreground">{cf.name}</span>
                        <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{cf.description}</span>
                      </span>
                      <span className="flex shrink-0 items-center gap-2">
                        <span className="text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{scoreLabel(score)}</span>
                        {already ? <Chip tone="ok">Added</Chip> : null}
                      </span>
                    </div>
                  );
                })}
              </div>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

      {/* --------------------------------------------------- test drawer */}
      <Drawer
        open={drawer?.kind === "test"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title="Test a release"
        description="See which rules would match a release name, and why"
        footer={<DrawerFooter state="clean" saveType="button" saveLabel="Close" saveEnabled={false} onCancel={() => setDrawer(null)} />}
      >
        <DrawerSection title="Release name">
          <Field label="Paste a release name" hideLabel>
            <Input
              value={releaseName}
              onChange={(event) => setReleaseName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") {
                  event.preventDefault();
                  void runTest();
                }
              }}
              placeholder="Movie.Title.2024.2160p.UHD.BluRay.DV.HDR.x265-GROUP"
              aria-label="Release name to test"
              className="font-mono"
            />
          </Field>
          <FieldRow>
            <Field label="Rules to test">
              <SegmentedControl<"all" | "movies" | "tv">
                aria-label="Rules to test"
                value={testScope}
                onValueChange={setTestScope}
                options={[
                  { value: "all", label: "All" },
                  { value: "movies", label: "Movies" },
                  { value: "tv", label: "TV" }
                ]}
              />
            </Field>
            <Field label="Run" hideLabel>
              <Button type="button" onClick={() => void runTest()} disabled={busy === "test" || !releaseName.trim()}>
                {busy === "test" ? <Loader2 className="h-4 w-4 animate-spin" /> : <FlaskConical className="h-4 w-4" />}
                Test
              </Button>
            </Field>
          </FieldRow>
          {customFormats.length === 0 ? (
            <p className="text-[length:var(--type-caption)] text-warning">You have no rules yet, so nothing can match. Apply a preset first.</p>
          ) : null}
        </DrawerSection>

        {results ? (
          <DrawerSection
            title="Result"
            aside={`${results.filter((result) => result.isMatch).length} of ${results.length} matched · ${scoreLabel(results.filter((result) => result.isMatch).reduce((total, result) => total + result.score, 0))}`}
          >
            {results.length === 0 ? (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">Nothing to report — the test could not be run.</p>
            ) : (
              <div className="grid gap-1.5">
                {[...results].sort((a, b) => Number(b.isMatch) - Number(a.isMatch)).map((result) => (
                  <div key={result.formatId} className="grid gap-1 border-b border-hairline py-2 last:border-b-0">
                    <div className="flex items-center justify-between gap-3">
                      <span className={`min-w-0 truncate text-[length:var(--type-body-sm)] ${result.isMatch ? "text-foreground" : "text-muted-foreground"}`}>
                        {result.formatName}
                      </span>
                      <span className="flex shrink-0 items-center gap-2">
                        <span className="text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{scoreLabel(result.score)}</span>
                        <Chip tone={result.isMatch ? "ok" : "muted"}>{result.isMatch ? "Matched" : "No match"}</Chip>
                      </span>
                    </div>
                    {result.isMatch && result.matchedConditions.length ? (
                      <span className="block truncate font-mono text-[length:var(--type-caption)] text-muted-foreground">
                        {result.matchedConditions.join(" · ")}
                      </span>
                    ) : null}
                  </div>
                ))}
              </div>
            )}
          </DrawerSection>
        ) : null}
      </Drawer>
    </form>
  );
}

/* ---------------------------------------------------------------- bits */

interface DryRunResult {
  formatId: string;
  formatName: string;
  mediaType: string;
  score: number;
  isMatch: boolean;
  matchedConditions: string[];
  missedConditions: string[];
}

function same<T>(a: T, b: T) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function updateCondition(setForm: React.Dispatch<React.SetStateAction<RuleForm>>, index: number, patch: Partial<Condition>) {
  setForm((current) => ({
    ...current,
    conditions: current.conditions.map((condition, i) => (i === index ? { ...condition, ...patch } : condition))
  }));
}

function emptyRule(): RuleForm {
  return { name: "", mediaType: "movies", score: "100", conditions: [], trashId: null };
}

function ruleFrom(format: CustomFormatItem): RuleForm {
  return {
    name: format.name,
    mediaType: format.mediaType === "tv" ? "tv" : "movies",
    score: String(format.score),
    conditions: parseConditions(format.conditions),
    trashId: format.trashId ?? null
  };
}

function safeguardsFrom(settings: PlatformSettingsSnapshot): SafeguardForm {
  return {
    scoringMode: settings.searchScoringMode,
    neverGrab: settings.releaseNeverGrabPatterns.split(/\r?\n|,/).map((item) => item.trim()).filter(Boolean)
  };
}

/**
 * Conditions are stored as a JSON array. Rows written before that format used
 * newline-separated patterns, so those are read as release-title matches rather
 * than shown as broken.
 */
function parseConditions(raw: string | null | undefined): Condition[] {
  if (!raw?.trim()) return [];
  const trimmed = raw.trim();
  if (trimmed.startsWith("[")) {
    try {
      const parsed = JSON.parse(trimmed) as Partial<Condition>[];
      return parsed.map((condition) => ({
        type: (condition.type ?? "releaseTitle") as ConditionType,
        value: String(condition.value ?? ""),
        negate: Boolean(condition.negate),
        required: condition.required !== false
      }));
    } catch {
      /* fall through to the legacy shape */
    }
  }
  return trimmed
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((value) => ({ type: "releaseTitle" as ConditionType, value, negate: false, required: true }));
}

function conditionSummary(conditions: Condition[]) {
  const first = conditions[0]!;
  const label = CONDITION_TYPES.find((type) => type.value === first.type)?.label ?? first.type;
  // Patterns can be long regex; the drawer shows them in full.
  const value = first.value.length > 32 ? `${first.value.slice(0, 32)}…` : first.value;
  return `${label} ${first.negate ? "without" : "with"} “${value}”`;
}

function scoreLabel(score: number) {
  if (score <= -10000) return "Blocked";
  return `${score > 0 ? "+" : ""}${score.toLocaleString()}`;
}
