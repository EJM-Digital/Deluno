/**
 * Release Preferences — list → drawer plus one page-level form.
 *
 *   PageToolbar (Library Profiles tabs · All/Movies/TV · Test a release · New custom rule)
 *   ListCard  guide presets       (row → drawer: what it contains · Apply)
 *   ListCard  advanced rules      (row → drawer: setup · Advanced matching · Remove)
 *   ListCard  safeguards   (page form, saved by PageFooter)
 *
 * Rules describe a release's traits: Radarr and Sonarr call these Custom
 * Formats. Deluno turns reviewed rules into typed preferences. A rule is either written here or started from the bundled TRaSH
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
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { LibraryImpactLinks } from "../components/ui/library-impact";
import { ListGroupHeader, MediaTypeFilter, mediaTypeLabel, useMediaTypeSplit } from "../components/ui/media-type-split";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { configurationNavAreas } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import {
  compileQualityProfilePreferences,
  activateTrashGuideVersion,
  fetchTrashGuidePackage,
  fetchTrashGuideUpdateCheck,
  fetchTrashGuideVersions,
  fetchJson,
  previewTrashGuideSync,
  applyTrashGuideSync,
  previewReleasePreference,
  runTrashGuideUpdateCheck,
  setTrashGuideUpdateCheckEnabled,
  type CustomFormatItem,
  type GuideCustomFormat,
  type GuideFormatBundle,
  type GuidePackage,
  type GuidePackageUpdatePreview,
  type GuideUpdateCheckState,
  type StoredGuidePackage,
  type LibraryItem,
  type PolicySetItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem,
  type ReleasePreferencePreviewResponse
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";

const TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

// Presentation metadata only. The guide package (including the category values
// and all catalogue entries) is served by the backend; these labels keep the
// picker readable without reintroducing a frontend catalogue copy.
const GUIDE_CATEGORY_ORDER = [
  "hdr",
  "codec",
  "audio",
  "channels",
  "source",
  "streaming",
  "edition",
  "groups",
  "anime",
  "language",
  "unwanted",
  "misc"
] as const;

const GUIDE_CATEGORY_LABELS: Record<string, string> = {
  hdr: "HDR & Color",
  codec: "Video Codec",
  audio: "Audio Format",
  channels: "Audio Channels",
  source: "Source / Edition",
  streaming: "Streaming Service",
  edition: "Edition",
  groups: "Release Groups",
  anime: "Anime",
  language: "Language",
  unwanted: "Block / Unwanted",
  misc: "Misc"
};

/** Legacy compatibility inputs. Anything at or under -10000 blocks a release outright. */
const SCORE_OPTIONS = [
  { value: "-10000", label: "Block this release (legacy input)" },
  { value: "-100", label: "Strongly avoid (legacy input)" },
  { value: "-25", label: "Avoid (legacy input)" },
  { value: "25", label: "Mild preference (legacy input)" },
  { value: "100", label: "Prefer (legacy input)" },
  { value: "500", label: "Strongly prefer (legacy input)" }
];

const INTENT_OPTIONS = [
  { value: "blocked", label: "Must not have" },
  { value: "avoid", label: "Avoid" },
  { value: "neutral", label: "I do not care" },
  { value: "prefer", label: "Prefer" },
  { value: "strong-prefer", label: "Strongly prefer" },
  { value: "custom", label: "Custom intent (advanced)" }
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
  libraries: LibraryItem[];
  policySets: PolicySetItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  settings: PlatformSettingsSnapshot;
  guide: GuidePackage;
  guideUpdateCheck: GuideUpdateCheckState;
  guideVersions: StoredGuidePackage[];
}

export async function settingsCustomFormatsLoader(): Promise<LoaderData> {
  const [overview, customFormats, guide, guideUpdateCheck, guideVersions] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchTrashGuidePackage(),
    fetchTrashGuideUpdateCheck(),
    fetchTrashGuideVersions().catch(() => [] as StoredGuidePackage[])
  ]);
  return { ...overview, customFormats, guide, guideUpdateCheck, guideVersions };
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
  const { customFormats, settings, libraries, policySets, qualityProfiles, guide, guideUpdateCheck, guideVersions } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [busy, setBusy] = useState<string | null>(null);
  const [updateCheck, setUpdateCheck] = useState(guideUpdateCheck);
  const [updateCheckBusy, setUpdateCheckBusy] = useState<"settings" | "run" | "sync-preview" | "sync-apply" | "activate" | null>(null);
  const [guideUpdateDetailsOpen, setGuideUpdateDetailsOpen] = useState(false);
  const [guideVersionsOpen, setGuideVersionsOpen] = useState(false);
  const [versions, setVersions] = useState(guideVersions);
  // The loader re-reads the history on every revalidate; without this the
  // panel keeps showing the list from the first render after a sync.
  useEffect(() => setVersions(guideVersions), [guideVersions]);
  const [guideSyncPreview, setGuideSyncPreview] = useState<GuidePackageUpdatePreview | null>(null);

  const split = useMediaTypeSplit(customFormats, (format) => format.mediaType);
  const librariesByFormat = useMemo(() => {
    const map = new Map<string, LibraryItem[]>();
    for (const library of libraries) {
      const plan = library.defaultPolicySetId ? policySets.find((candidate) => candidate.id === library.defaultPolicySetId) : null;
      const profile = library.qualityProfileId ? qualityProfiles.find((candidate) => candidate.id === library.qualityProfileId) : plan?.qualityProfileId ? qualityProfiles.find((candidate) => candidate.id === plan.qualityProfileId) : null;
      const formatIds = new Set([...splitCsv(plan?.customFormatIds), ...splitCsv(profile?.customFormatIds)]);
      for (const formatId of formatIds) map.set(formatId, [...(map.get(formatId) ?? []), library]);
    }
    return map;
  }, [libraries, policySets, qualityProfiles]);
  const [drawer, setDrawer] = useState<DrawerMode>(null);

  /* ------------------------------------------------------------- rules */
  const [form, setForm] = useState<RuleForm>(() => emptyRule());
  const [initialForm, setInitialForm] = useState<RuleForm>(() => emptyRule());
  const [ruleState, setRuleState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [ruleMessage, setRuleMessage] = useState<string | null>(null);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [legacyScoreOpen, setLegacyScoreOpen] = useState(false);

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
    setAdvancedOpen(!format);
    setLegacyScoreOpen(false);
    setDrawer({ kind: "rule", id: format?.id ?? null });
  }

  async function setGuideUpdateCheckEnabled(isEnabled: boolean) {
    setUpdateCheckBusy("settings");
    try {
      const next = await setTrashGuideUpdateCheckEnabled(isEnabled);
      setUpdateCheck(next);
      toast.success(isEnabled ? "Weekly guide-change checks enabled" : "Guide-change checks disabled");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not update guide-check preference.");
    } finally {
      setUpdateCheckBusy(null);
    }
  }

  async function runGuideUpdateCheck() {
    if (!updateCheck.isEnabled) return;
    setUpdateCheckBusy("run");
    try {
      const next = await runTrashGuideUpdateCheck();
      setUpdateCheck(next);
      setGuideSyncPreview(null);
      setGuideUpdateDetailsOpen((next.report?.changes.length ?? 0) > 0 || (next.report?.addedSources.length ?? 0) > 0);
      toast.success(next.status === "up-to-date" ? "Guide sources are up to date" : "Guide change report ready");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not check TRaSH Guides.");
    } finally {
      setUpdateCheckBusy(null);
    }
  }

  async function previewGuideSync() {
    const remoteRevision = updateCheck.report?.remoteRevision;
    if (!remoteRevision) return;
    setUpdateCheckBusy("sync-preview");
    try {
      const preview = await previewTrashGuideSync({
        expectedCurrentIntegritySha256: guide.integritySha256,
        expectedUpstreamRevision: remoteRevision
      });
      setGuideSyncPreview(preview);
      if (preview.canApply) {
        toast.success("Guide sync preview is ready");
      } else {
        toast.error(preview.errors[0] ?? "Could not stage a guide sync preview.");
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not stage a TRaSH Guides sync.");
    } finally {
      setUpdateCheckBusy(null);
    }
  }

  /**
   * Return to a retained guide version.
   *
   * Deliberately not called "rollback": the owner is choosing which retained
   * snapshot is in use, and every version stays retained afterwards, so this
   * is reversible in both directions.
   */
  async function goBackToGuideVersion(stored: StoredGuidePackage) {
    setUpdateCheckBusy("activate");
    try {
      await activateTrashGuideVersion(stored.package.version, stored.package.id);
      setVersions(await fetchTrashGuideVersions());
      setGuideSyncPreview(null);
      revalidator.revalidate();
      toast.success(`Guide package version ${stored.package.version} is in use again`);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not go back to that guide version.");
    } finally {
      setUpdateCheckBusy(null);
    }
  }

  async function applyGuideSync() {
    const remoteRevision = updateCheck.report?.remoteRevision;
    if (!remoteRevision || !guideSyncPreview?.canApply) return;
    setUpdateCheckBusy("sync-apply");
    try {
      const applied = await applyTrashGuideSync({
        expectedCurrentIntegritySha256: guide.integritySha256,
        expectedUpstreamRevision: remoteRevision,
        expectedProposedIntegritySha256: guideSyncPreview.proposedIntegritySha256
      });
      setGuideSyncPreview(null);
      revalidator.revalidate();
      try {
        setUpdateCheck(await runTrashGuideUpdateCheck());
      } catch {
        // The versioned package is already saved. Retain the prior report if a
        // fresh metadata-only check is unavailable right now.
      }
      toast.success(`TRaSH guide snapshot synced as version ${applied.package.version}`);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not sync the reviewed guide snapshot.");
    } finally {
      setUpdateCheckBusy(null);
    }
  }

  /** Choosing a guide format fills the whole rule in — name, score and conditions. */
  function startFrom(trashId: string) {
    const bundled = guide.customFormats.find((format) => format.trashId === trashId);
    if (!bundled) {
      setForm((current) => ({ ...current, trashId: null }));
      setAdvancedOpen(true);
      return;
    }
    setForm((current) => ({
      ...current,
      trashId,
      name: bundled.name,
      score: String(bundled.originalScore),
      conditions: bundled.patterns.map((pattern) => ({ type: "releaseTitle" as ConditionType, value: pattern, negate: false, required: true }))
    }));
    setAdvancedOpen(false);
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
  const presetDetail = drawer?.kind === "preset" ? guide.bundles.find((bundle) => bundle.id === drawer.id) ?? null : null;
  const [presetMessage, setPresetMessage] = useState<string | null>(null);
  const [presetState, setPresetState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();

  function presetProgress(bundle: GuideFormatBundle) {
    const target = bundle.mediaType === "tv" ? "tv" : "movies";
    const resolved = bundle.includes.map((entry) => guide.customFormats.find((format) => format.trashId === entry.trashId)).filter((cf): cf is GuideCustomFormat => Boolean(cf));
    const applied = resolved.filter((cf) => appliedTrashIds.has(`${target}:${cf.trashId}`));
    return { target, resolved, applied: applied.length, total: resolved.length };
  }

  async function applyPreset(bundle: GuideFormatBundle) {
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
        const score = bundle.includes.find((entry) => entry.trashId === cf.trashId)?.score ?? cf.originalScore;
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
  const [currentReleaseName, setCurrentReleaseName] = useState("");
  const [testScope, setTestScope] = useState<"all" | "movies" | "tv">("all");
  const [testProfileId, setTestProfileId] = useState("");
  const [results, setResults] = useState<DryRunResult[] | null>(null);
  const [typedPreview, setTypedPreview] = useState<ReleasePreferencePreviewResponse | null>(null);
  const [typedPreviewError, setTypedPreviewError] = useState<string | null>(null);

  const testProfiles = useMemo(
    () => qualityProfiles.filter((profile) => testScope === "all" || profile.mediaType === testScope),
    [qualityProfiles, testScope]
  );

  function openTestDrawer() {
    setResults(null);
    setTypedPreview(null);
    setTypedPreviewError(null);
    setTestProfileId((current) => current && testProfiles.some((profile) => profile.id === current) ? current : testProfiles[0]?.id ?? "");
    setDrawer({ kind: "test" });
  }

  async function runTest() {
    if (!releaseName.trim()) return;
    setBusy("test");
    setResults(null);
    setTypedPreview(null);
    setTypedPreviewError(null);
    try {
      const legacyRequest = fetchJson<DryRunResult[]>("/api/custom-formats/dry-run", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ releaseName: releaseName.trim(), mediaType: testScope === "all" ? null : testScope })
        });
      const profile = qualityProfiles.find((candidate) => candidate.id === testProfileId);
      const typedRequest = profile
        ? compileQualityProfilePreferences(profile.id)
            .then((compilation) => previewReleasePreference({
              planId: compilation.plan.id,
              planVersion: compilation.plan.version,
              releaseName: releaseName.trim(),
              currentReleaseName: currentReleaseName.trim() || undefined
            }))
        : Promise.resolve(null);
      const [legacy, typed] = await Promise.all([legacyRequest, typedRequest]);
      setResults(legacy);
      setTypedPreview(typed);
    } catch (error) {
      setResults([]);
      setTypedPreviewError(error instanceof Error ? error.message : "The typed release preview could not be run.");
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
  const selectedGuide = form.trashId ? guide.customFormats.find((format) => format.trashId === form.trashId) ?? null : null;
  return (
    <form onSubmit={submitSafeguards} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar
        tabs={TABS}
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
            <Button type="button" variant="outline" onClick={openTestDrawer}>
              <FlaskConical className="h-4 w-4" />
              Test a release
            </Button>
            <PageToolbarAction onClick={() => openRule(null)}>New custom rule</PageToolbarAction>
          </>
        }
      />

      <ListCard title="Guide presets" count="Start with a goal instead of building rules yourself">
        <ListTable columns={[{ label: "Preset" }, { label: "Best for", width: "minmax(0,1.6fr)" }, { label: "Rules" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
          {guide.bundles.map((bundle) => {
            const progress = presetProgress(bundle);
            const complete = progress.total > 0 && progress.applied === progress.total;
            return (
              <ListRow key={bundle.id} onClick={() => { setPresetMessage(null); setDrawer({ kind: "preset", id: bundle.id }); }} selected={drawer?.kind === "preset" && drawer.id === bundle.id}>
                <ListNameCell name={bundle.name} sub={`${bundle.level} · ${bundle.mediaType === "all" ? "Movies and TV" : mediaTypeLabel(bundle.mediaType)}`} />
                <ListCell primary={bundle.bestFor} secondary={bundle.description} />
                <ListCell numeric primary={`${progress.applied} of ${progress.total}`} secondary="applied" />
                <ListCell mobile>
                  <Chip tone={complete ? "ok" : progress.applied ? "info" : "idle"}>{complete ? "Applied" : progress.applied ? "Partly applied" : "Not applied"}</Chip>
                </ListCell>
              </ListRow>
            );
          })}
        </ListTable>
      </ListCard>

      <ListCard title="TRaSH guide updates" count="Optional check and owner-approved sync — Deluno never changes a plan automatically">
        <div className="grid gap-3 p-[var(--card-pad-x)]">
          <SwitchRow
            label="Check upstream guide changes weekly"
            description="Off by default. When enabled, Deluno compares the public TRaSH Git tree with the exact guide files behind your saved rules. A detected update can be staged, reviewed, and explicitly synced; it never changes a local rule or plan automatically."
            checked={updateCheck.isEnabled}
            onCheckedChange={(checked) => void setGuideUpdateCheckEnabled(checked)}
            disabled={updateCheckBusy !== null}
          />
          <div className="flex flex-wrap items-center justify-between gap-2 rounded-[10px] border border-hairline bg-surface-2 px-3 py-2">
            <div className="min-w-0">
              <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">{guideUpdateCheckLabel(updateCheck.status)}</p>
              <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                {updateCheck.error ?? updateCheck.report?.summary ?? (updateCheck.isEnabled ? "No guide check has run yet." : "Enable checks to allow an outbound TRaSH Guides comparison.")}
              </p>
            </div>
            <Button type="button" variant="outline" size="sm" onClick={() => void runGuideUpdateCheck()} disabled={!updateCheck.isEnabled || updateCheckBusy !== null}>
              {updateCheckBusy === "run" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
              Check TRaSH now
            </Button>
          </div>
          {updateCheck.report && (updateCheck.report.changes.length || updateCheck.report.addedSources.length) ? (
            <Disclosure
              title="Review detected guide changes"
              summary={`${updateCheck.report.changes.length} tracked change(s) · ${updateCheck.report.addedSources.length} new source file(s)`}
              open={guideUpdateDetailsOpen}
              onOpenChange={setGuideUpdateDetailsOpen}
            >
              <p className="text-[length:var(--type-caption)] text-muted-foreground">
                {updateCheck.report.changes.filter((change) => change.isInUse).length} changed source item(s) affect saved custom formats. Review these before changing any guide package.
              </p>
              {updateCheck.report.changes.map((change) => (
                <div key={`${change.kind}:${change.id}`} className="rounded-[10px] border border-hairline px-3 py-2">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="text-[length:var(--type-body-sm)] font-medium text-foreground">{change.name}</span>
                    <Chip tone={change.changeType === "removed" || change.isInUse ? "warn" : "info"}>{change.changeType === "removed" ? "Removed upstream" : "Changed upstream"}</Chip>
                  </div>
                  <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">
                    {change.mediaType === "tv" ? "TV" : "Movies"} · {change.kind} · {change.isInUse ? `used by ${change.inUseCustomFormatIds.length} saved custom format${change.inUseCustomFormatIds.length === 1 ? "" : "s"}` : "not used by a saved custom format"}
                  </p>
                </div>
              ))}
              {updateCheck.report.addedSources.map((source) => (
                <p key={source.sourcePath} className="text-[length:var(--type-caption)] text-muted-foreground">
                  New upstream {source.mediaType === "tv" ? "TV" : "movie"} {source.kind}: <span className="font-mono">{source.sourcePath}</span>
                </p>
              ))}
              <div className="mt-3 flex flex-wrap items-center justify-between gap-2 rounded-[10px] border border-hairline bg-surface-2 px-3 py-2">
                <p className="max-w-2xl text-[length:var(--type-caption)] text-muted-foreground">
                  Build a versioned sync preview from this exact upstream revision. Deluno preserves its reviewed mappings, keeps unknown rules Advanced, and does not change saved custom formats, library profiles, or scenario plans.
                </p>
                <Button type="button" variant="outline" size="sm" onClick={() => void previewGuideSync()} disabled={updateCheckBusy !== null}>
                  {updateCheckBusy === "sync-preview" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Preview sync
                </Button>
              </div>
              {guideSyncPreview ? (
                <div className="mt-3 rounded-[10px] border border-hairline bg-surface-2 p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div>
                      <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">
                        {guideSyncPreview.canApply ? `Ready to sync guide package v${guideSyncPreview.proposed.version}` : "Sync preview needs attention"}
                      </p>
                      <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                        {guideSyncPreview.canApply
                          ? `Pins the source inventory to ${guideSyncPreview.proposed.source.upstreamRevision.slice(0, 12)}. Existing local rules and plans stay as they are.`
                          : "Nothing has been saved. Resolve the reported problem, then run a fresh check."}
                      </p>
                    </div>
                    {guideSyncPreview.canApply ? (
                      <Button type="button" size="sm" onClick={() => void applyGuideSync()} disabled={updateCheckBusy !== null}>
                        {updateCheckBusy === "sync-apply" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                        Sync reviewed snapshot
                      </Button>
                    ) : null}
                  </div>
                  {guideSyncPreview.errors.map((error) => <p key={error} className="mt-2 text-[length:var(--type-caption)] text-destructive">{error}</p>)}
                  {guideSyncPreview.warnings.map((warning) => <p key={warning} className="mt-2 text-[length:var(--type-caption)] text-warning">{warning}</p>)}
                  {/*
                    The diff was computed and then reported as a count. "Three
                    profiles have a diff" is not a readable plan diff - it is
                    the number of readable plan diffs Deluno declined to show
                    (#350).
                  */}
                  {guideSyncPreview.profileDiffs.some((diff) => diff.changes.length > 0) ? (
                    <div className="mt-2 grid gap-2" aria-label="Guide profile plan diff">
                      <p className="text-[length:var(--type-caption)] text-muted-foreground">
                        These guide profiles compile differently under the new snapshot. Syncing does not change them; apply each plan change yourself.
                      </p>
                      {guideSyncPreview.profileDiffs
                        .filter((diff) => diff.changes.length > 0)
                        .map((diff) => (
                          <div key={diff.profileId} className="rounded-[10px] border border-hairline px-3 py-2">
                            <div className="flex flex-wrap items-center justify-between gap-2">
                              <span className="text-[length:var(--type-body-sm)] font-medium text-foreground">{diff.profileName}</span>
                              <span className="text-[length:var(--type-caption)] text-muted-foreground">
                                {diff.currentAdvancedRuleCount} → {diff.proposedAdvancedRuleCount} rule{diff.proposedAdvancedRuleCount === 1 ? "" : "s"} needing review
                              </span>
                            </div>
                            <ul className="mt-1 grid gap-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                              {diff.changes.map((change) => <li key={change}>{change}</li>)}
                            </ul>
                            {diff.warnings.map((warning) => (
                              <p key={warning} className="mt-1 text-[length:var(--type-caption)] text-warning">{warning}</p>
                            ))}
                            <p className="mt-1 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                              {(diff.currentPlanHash ?? "none").slice(0, 12)} → {(diff.proposedPlanHash ?? "none").slice(0, 12)}
                            </p>
                          </div>
                        ))}
                    </div>
                  ) : (
                    <p className="mt-2 text-[length:var(--type-caption)] text-muted-foreground">
                      No guide profile compiles differently under this snapshot.
                    </p>
                  )}
                </div>
              ) : null}
            </Disclosure>
          ) : null}

          {/*
            #350: an update produces a rollback point. Every guide version is
            immutable and kept, but a point you cannot return to is not one, so
            the history is here with the way back beside it.
          */}
          {versions.length > 1 ? (
            <Disclosure
              title="Guide version history"
              summary={`${versions.length} retained version${versions.length === 1 ? "" : "s"} · go back to any of them`}
              open={guideVersionsOpen}
              onOpenChange={setGuideVersionsOpen}
            >
              <div className="grid gap-2" aria-label="Guide package versions">
                {versions.map((stored) => (
                  <div key={`${stored.package.id}:${stored.package.version}`} className="flex flex-wrap items-center justify-between gap-2 rounded-[10px] border border-hairline px-3 py-2">
                    <div className="min-w-0">
                      <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">
                        Version {stored.package.version}
                        {stored.isActive ? " · in use" : ""}
                      </p>
                      <p className="mt-0.5 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                        {stored.package.source.upstreamRevision.slice(0, 12)} · {stored.integritySha256.slice(0, 12)}
                      </p>
                    </div>
                    {stored.isActive ? (
                      <Chip tone="ok">Current</Chip>
                    ) : (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={() => void goBackToGuideVersion(stored)}
                        disabled={updateCheckBusy !== null}
                      >
                        {updateCheckBusy === "activate" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                        Go back to this version
                      </Button>
                    )}
                  </div>
                ))}
              </div>
            </Disclosure>
          ) : null}
        </div>
      </ListCard>

      <ListCard title="Advanced release rules" count={customFormats.length ? `${customFormats.length} ${customFormats.length === 1 ? "rule" : "rules"} · full TRaSH and custom controls` : undefined}>
        {customFormats.length === 0 ? (
          <ListEmpty
            title="No advanced release rules yet"
            description="Most people can choose release preferences from a Library Profile. Use this area when you want the full guide catalogue, advanced matching, or your own rule."
            actions={<Button type="button" variant="outline" onClick={() => openRule(null)}><Plus className="h-4 w-4" />New custom rule</Button>}
          />
        ) : (
          <ListTable columns={[{ label: "Rule" }, { label: "Matches on", width: "minmax(0,1.4fr)" }, { label: "Intent" }, { label: "Used by" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
            {split.groups.flatMap((group) => [
              split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
              ...group.items.map((format) => {
                const conditions = parseConditions(format.conditions);
                const guideFormat = format.trashId ? guide.customFormats.find((candidate) => candidate.trashId === format.trashId) : undefined;
                const usedBy = librariesByFormat.get(format.id) ?? [];
                return (
                  <ListRow key={format.id} onClick={() => openRule(format)} selected={drawer?.kind === "rule" && drawer.id === format.id}>
                    <ListNameCell name={format.name} sub={guideFormat ? "From the guide catalogue" : "Written here"} />
                    {/* Guide rules are raw regex — lead with what they mean, not the pattern. */}
                    <ListCell
                      primary={guideFormat?.description ?? (conditions.length ? conditionSummary(conditions) : "No conditions")}
                      secondary={conditions.length ? `${conditions.length} ${conditions.length === 1 ? "condition" : "conditions"}` : "never matches anything"}
                    />
                    <ListCell primary={ruleIntent(format.score)} secondary={format.score <= -10000 ? "never grabbed" : "legacy input"} />
                    {/*
                      A library reaches these rules through its quality profile,
                      whether that came from a Library Profile or was attached
                      directly — claiming "Inherited through a Library Profile"
                      named an entity most installs never create (#255).
                    */}
                    <ListCell primary={<LibraryImpactLinks libraries={usedBy} />} secondary={usedBy.length ? "Applied through the library's quality profile" : "Not applied to any library yet"} />
                    <ListCell mobile>
                      {/*
                        A legacy input of 0 is neutral, not avoided: a 1080p tag rule
                        scoring 0 inside a 1080p profile looked banned (#257).
                        Only a negative score is an active penalty.
                      */}
                      <Chip tone={format.score <= -10000 ? "bad" : format.score > 0 ? "ok" : format.score < 0 ? "warn" : "idle"}>
                        {ruleIntent(format.score)}
                      </Chip>
                    </ListCell>
                  </ListRow>
                );
              })
            ])}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Safeguards" count="The final guardrails applied after quality and legacy inputs are worked out">
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
                    <Chip key={rule} tone="idle">
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

      <PageFooter state={safeguardFooter} message={safeguardMessage} saveLabel="Save safeguards" />

      {/* --------------------------------------------------- rule drawer */}
      <Drawer
        open={drawer?.kind === "rule"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={editing ? editing.name : "New custom release rule"}
        description={editing ? `${mediaTypeLabel(editing.mediaType)} · ${selectedGuide ? "Guide-backed" : "Custom rule"}` : "Choose a guide rule or build your own"}
        onSubmit={submitRule}
        footer={
          <DrawerFooter
            state={ruleFooter}
            message={ruleMessage}
            saveLabel={editing ? "Save rule" : "Create rule"}
            onCancel={() => setDrawer(null)}
            saveEnabled={editing ? undefined : true}
            disabled={busy !== null}
          />
        }
      >
        <DrawerSection title="Rule setup">
          {!editing ? (
            <Field label="Guide choice" optional help="Choose a guide-backed rule and Deluno fills in the technical matching for you. Choose Custom rule when you want to define your own.">
              <Select value={form.trashId ?? ""} onChange={(event) => startFrom(event.target.value)} placeholder="Custom rule">
                {GUIDE_CATEGORY_ORDER.map((category) => {
                  const entries = guide.customFormats.filter((cf) => cf.category === category && !cf.bundleOnly);
                  if (!entries.length) return null;
                  return (
                    <optgroup key={category} label={GUIDE_CATEGORY_LABELS[category] ?? category}>
                      {entries.map((cf) => (
                        <option key={cf.trashId} value={cf.trashId}>
                          {friendlyGuideName(cf)}
                        </option>
                      ))}
                    </optgroup>
                  );
                })}
              </Select>
            </Field>
          ) : null}
          {selectedGuide ? (
            <div className="grid gap-1 rounded-[10px] border border-primary/25 bg-primary/5 px-3 py-2">
              <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">{friendlyGuideName(selectedGuide)}</p>
              <p className="text-[length:var(--type-caption)] text-muted-foreground">{selectedGuide.description}</p>
              <p className="text-[length:var(--type-caption)] text-muted-foreground">The guide definition stays attached; the technical match is available under Advanced matching.</p>
            </div>
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
            <Field label="Intent" help="Tell Deluno whether this trait is required, avoided, or preferred.">
              <Select
                value={scoreIntent(form.score)}
                onChange={(event) => {
                  const option = INTENT_OPTIONS.find((candidate) => candidate.value === event.target.value);
                  if (option?.value !== "custom") setForm((current) => ({ ...current, score: scoreForIntent(option?.value) }));
                }}
                options={INTENT_OPTIONS}
              />
            </Field>
          </FieldRow>
        </DrawerSection>

        <Disclosure
          title="Advanced legacy input"
          summary="Only needed for an imported guide score or a custom compatibility value"
          open={legacyScoreOpen}
          onOpenChange={setLegacyScoreOpen}
        >
          <Field label="Legacy guide input" help="Stored for compatibility and provenance. It is not Deluno's typed decision value.">
            <PresetField
              inputType="number"
              value={form.score}
              onChange={(value) => setForm((current) => ({ ...current, score: value }))}
              options={SCORE_OPTIONS}
              customLabel="Custom legacy input"
              customPlaceholder="Legacy value"
            />
          </Field>
        </Disclosure>

        <DrawerSection title="Matching" aside={selectedGuide ? "Guide-backed" : form.conditions.length ? `${form.conditions.length} criteria` : "Set up below"}>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            {selectedGuide
              ? "Deluno will use the guide definition above. You only need to open Advanced matching if you want to change how it identifies releases."
              : "A custom rule needs at least one thing to look for. Use plain words such as WEB-DL, HDR, or a release group name."}
          </p>
          <Disclosure
            title="Advanced matching"
            summary={selectedGuide ? "View or edit the guide-backed criteria" : "Choose what Deluno should look for"}
            open={advancedOpen}
            onOpenChange={setAdvancedOpen}
          >
            {form.conditions.length === 0 ? (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">Add at least one matching criterion before saving this custom rule.</p>
            ) : null}
            {form.conditions.map((condition, index) => (
              <div key={index} className="grid gap-2 rounded-[10px] border border-hairline bg-surface-2/40 p-3">
                <FieldRow>
                  <Field label="Match on">
                    <Select
                      value={condition.type}
                      onChange={(event) => updateCondition(setForm, index, { type: event.target.value as ConditionType })}
                      options={CONDITION_TYPES.map((type) => ({ value: type.value, label: type.label }))}
                    />
                  </Field>
                  <Field label="Rule">
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
                <Field label="Words to match" help="Plain text is enough. Deluno also accepts a pattern for advanced matching.">
                  <Input
                    value={condition.value}
                    onChange={(event) => updateCondition(setForm, index, { value: event.target.value })}
                    placeholder="e.g. WEB-DL, HDR, or GROUP"
                    className={selectedGuide ? "font-mono" : undefined}
                  />
                </Field>
                <div className="flex justify-end">
                  <Button
                    type="button"
                    variant="destructive"
                    size="sm"
                    onClick={() => setForm((current) => ({ ...current, conditions: current.conditions.filter((_, i) => i !== index) }))}
                  >
                    Remove criterion
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
                Add matching criterion
              </Button>
            </div>
          </Disclosure>
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Library impact" aside={librariesByFormat.get(editing.id)?.length ? `${librariesByFormat.get(editing.id)!.length} libraries` : "Not assigned"}>
            <p className="text-[length:var(--type-caption)] text-muted-foreground">This rule only changes releases for libraries whose Library Profile includes it.</p>
            <LibraryImpactLinks libraries={librariesByFormat.get(editing.id) ?? []} emptyLabel="No Library Profile uses this rule yet." />
          </DrawerSection>
        ) : null}

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
                  const score = presetDetail.includes.find((entry) => entry.trashId === cf.trashId)?.score ?? cf.originalScore;
                  return (
                    <div key={cf.trashId} className="flex items-center justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0">
                      <span className="min-w-0">
                        <span className="block truncate text-[length:var(--type-body-sm)] text-foreground">{cf.name}</span>
                        <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{cf.description}</span>
                      </span>
                      <span className="flex shrink-0 items-center gap-2">
                        <span className="text-[length:var(--type-caption)] text-muted-foreground">{ruleIntent(score)}</span>
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
          <Field label="Current release" optional help="Add the installed release name to see whether the candidate is an upgrade, equivalent, rejected, or held for review.">
            <Input
              value={currentReleaseName}
              onChange={(event) => setCurrentReleaseName(event.target.value)}
              placeholder="Optional installed release name"
              aria-label="Current installed release name"
              className="font-mono"
            />
          </Field>
          <Field label="Typed plan" optional help="This uses the same persisted release-preference plan as search and import. Leave it blank to run only the legacy matcher check.">
            <Select
              value={testProfileId}
              onChange={(event) => setTestProfileId(event.target.value)}
              placeholder={testProfiles.length ? "Choose a Quality Profile" : "No Quality Profiles available"}
              options={testProfiles.map((profile) => ({ value: profile.id, label: `${profile.name} · ${profile.mediaType === "tv" ? "TV" : "Movies"}` }))}
              disabled={!testProfiles.length}
            />
          </Field>
          {customFormats.length === 0 ? (
            <p className="text-[length:var(--type-caption)] text-warning">You have no rules yet, so nothing can match. Apply a preset first.</p>
          ) : null}
        </DrawerSection>

        {results ? (
          <DrawerSection
            title="Result"
            aside={`${results.filter((result) => result.isMatch).length} of ${results.length} matched`}
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
                        <span className="text-[length:var(--type-caption)] text-muted-foreground">{result.isMatch ? ruleIntent(result.score) : "No intent"}</span>
                        <Chip tone={result.isMatch ? "ok" : "idle"}>{result.isMatch ? "Matched" : "No match"}</Chip>
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

        {typedPreviewError ? (
          <DrawerSection title="Typed plan preview">
            <p role="alert" className="text-[length:var(--type-caption)] text-destructive">{typedPreviewError}</p>
          </DrawerSection>
        ) : null}

        {typedPreview ? (
          <DrawerSection title="Typed plan preview" aside={typedPreview.comparison ? typedComparisonLabel(typedPreview.comparison.status) : typedStatusLabel(typedPreview.candidateEvaluation.status)}>
            <div className="grid gap-3">
              <div className="flex flex-wrap items-center gap-2">
                <Chip tone={typedPreview.comparison?.status === "rejected" ? "bad" : typedPreview.comparison?.status === "needsReview" ? "warn" : typedPreview.comparison?.status === "currentBetter" || typedPreview.comparison?.status === "equivalent" ? "info" : typedPreview.candidateEvaluation.status === "meetsPlan" ? "ok" : "info"}>
                  {typedPreview.comparison ? typedComparisonLabel(typedPreview.comparison.status) : typedStatusLabel(typedPreview.candidateEvaluation.status)}
                </Chip>
                <span className="text-[length:var(--type-caption)] text-muted-foreground">Plan {typedPreview.planVersion}</span>
              </div>
              {typedPreview.comparison?.reasons.length ? (
                <ul className="grid gap-1 text-[length:var(--type-body-sm)] text-muted-foreground" aria-label="Typed comparison reasons">
                  {typedPreview.comparison.reasons.map((reason) => <li key={reason}>{reason}</li>)}
                </ul>
              ) : null}
              {typedPreview.candidateEvaluation.reasons.length ? (
                <ul className="grid gap-1 text-[length:var(--type-caption)] text-muted-foreground" aria-label="Typed evaluation reasons">
                  {typedPreview.candidateEvaluation.reasons.map((reason) => <li key={reason}>{reason}</li>)}
                </ul>
              ) : null}
              <div className="grid gap-1.5" aria-label="Typed family outcomes">
                {typedPreview.candidateEvaluation.families.map((family) => (
                  <div key={family.familyId} className="flex flex-wrap items-start justify-between gap-2 rounded-[10px] border border-hairline px-3 py-2">
                    <span className="min-w-0 text-[length:var(--type-body-sm)] text-foreground">{humanizeTypedName(family.familyId)}</span>
                    <span className="max-w-full text-right text-[length:var(--type-caption)] text-muted-foreground">{family.explanation}</span>
                  </div>
                ))}
              </div>
              <p className="text-[length:var(--type-caption)] text-muted-foreground">Detected {typedPreview.candidateFacts.length} typed evidence item{typedPreview.candidateFacts.length === 1 ? "" : "s"}. The plan hash is {typedPreview.planHash.slice(0, 12)}…</p>
            </div>
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

function typedStatusLabel(value: string) {
  return value === "meetsPlan" ? "Meets plan" : value === "belowGoal" ? "Below goal" : value === "needsReview" ? "Needs review" : "Missing evidence";
}

function typedComparisonLabel(value: string) {
  // "currentBetter" is not a rejection: the release passed every hard gate
  // and the installed file simply wins, so it must not read as a violation.
  return value === "upgrade" ? "Upgrade" : value === "rejected" ? "Rejected" : value === "needsReview" ? "Needs review" : value === "equivalent" ? "Equivalent" : value === "currentBetter" ? "Your file is better" : value === "bestMatchNow" ? "Best match now" : "Acceptable";
}

function humanizeTypedName(value: string) {
  return value
    .split(/[._-]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

function splitCsv(value: string | null | undefined) {
  return (value ?? "").split(",").map((item) => item.trim()).filter(Boolean);
}

function guideUpdateCheckLabel(status: GuideUpdateCheckState["status"]) {
  return status === "up-to-date" ? "No tracked guide changes" : status === "update-available" ? "Guide changes need review" : status === "failed" ? "Guide check could not finish" : status === "never-checked" ? "Ready for the first guide check" : "Guide checks are off";
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

function friendlyGuideName(format: GuideCustomFormat) {
  const names: Record<string, string> = {
    "HD Bluray Tier 01": "Top-tier Blu-ray groups",
    "HD Bluray Tier 02": "Trusted Blu-ray groups",
    "WEB Tier 01": "Top-tier WEB groups",
    "WEB Tier 02": "Trusted WEB groups",
    "No Release Group": "Releases without a release group",
    "LQ (Low Quality Groups)": "Known low-quality release groups"
  };
  return names[format.name] ?? format.name;
}

function ruleIntent(score: number) {
  if (score <= -10000) return "Must not have";
  if (score < 0) return "Avoid";
  if (score === 0) return "I do not care";
  if (score >= 500) return "Strongly prefer";
  return "Prefer";
}

function scoreIntent(score: string) {
  const value = Number(score);
  if (!Number.isFinite(value)) return "custom";
  if (value <= -10000) return "blocked";
  if (value < 0) return "avoid";
  if (value === 0) return "neutral";
  if (value >= 500) return "strong-prefer";
  if (value > 0) return "prefer";
  return "custom";
}

function scoreForIntent(intent: string | undefined) {
  switch (intent) {
    case "blocked": return "-10000";
    case "avoid": return "-100";
    case "neutral": return "0";
    case "strong-prefer": return "500";
    case "prefer": return "100";
    default: return "0";
  }
}
