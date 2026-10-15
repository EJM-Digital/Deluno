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
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { FlaskConical, Loader2, Plus, RotateCcw, X } from "lucide-react";
import { Button } from "../components/ui/button";
import { Checkbox } from "../components/ui/checkbox";
import { Chip } from "../components/ui/chip";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { FilterChips } from "../components/ui/filter-chips";
import { Input } from "../components/ui/input";
import { Textarea } from "../components/ui/textarea";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { LibraryImpactLinks } from "../components/ui/library-impact";
import { ListGroupHeader, MediaTypeFilter, mediaTypeLabel, useMediaTypeSplit } from "../components/ui/media-type-split";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { ChoiceRows } from "../components/ui/choice-rows";
import { friendlyRuleName } from "../lib/guide-names";
import {
  createRuleFromGuide,
  guideBundleTargets,
  guideFormatAppliesTo,
  guideSpecifications,
  isAnimeDefaultFormat,
  isGenuineGuideFormat,
  readSpecifications,
  specificationKind,
  type RuleSpecification
} from "../lib/guide-formats";
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
  applyGuideRuleMigration,
  fetchGuideRuleMigration,
  fetchTrashGuidePackage,
  fetchTrashGuideUpdateCheck,
  fetchTrashGuideVersions,
  fetchJson,
  readErrorMessage,
  previewTrashGuideSync,
  applyTrashGuideSync,
  previewReleasePreference,
  runTrashGuideUpdateCheck,
  setTrashGuideUpdateCheckEnabled,
  type CustomFormatItem,
  type GuideFormatBundle,
  type GuidePackage,
  type GuidePackageUpdatePreview,
  type GuideRuleMigrationPreview,
  type GuideUpdateCheckState,
  type StoredGuidePackage,
  type LibraryItem,
  type PolicySetItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem,
  type ReleasePreferencePreviewResponse,
  readErrorText
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import { KNOWN_INDEXER_FLAGS } from "./connections/presets";
import { localizeResetTimes } from "../lib/reset-times";

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
  "french",
  "german",
  "dutch",
  "asian",
  "uk",
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
  french: "French",
  german: "German",
  dutch: "Dutch",
  asian: "Asian",
  uk: "UK",
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

/**
 * The five intents, each with what choosing it actually does.
 *
 * These were a dropdown, which meant four of the five were invisible until you
 * opened it — on the one control where the whole question is "what would each
 * of these do to my library?". James: "doesnt need a drop down it can just be
 * a toggle with description".
 */
const INTENT_OPTIONS = [
  { value: "blocked", label: "Must not have", description: "Deluno will never take a release that matches this." },
  { value: "avoid", label: "Avoid", description: "Taken only when nothing better is available." },
  { value: "neutral", label: "I do not care", description: "Makes no difference to which release Deluno picks." },
  { value: "prefer", label: "Prefer", description: "Tips a close call towards releases that match." },
  { value: "strong-prefer", label: "Strongly prefer", description: "Weighs heavily in favour, and can justify replacing a file you already have." },
  { value: "custom", label: "Custom intent (advanced)", description: "Keeps an imported value that does not land on one of the choices above." }
];

/**
 * Four columns, not five.
 *
 * "Intent" and "Status" were the same words twice on every row — the text
 * "Strongly prefer" beside a chip reading "Strongly prefer" — so the coloured
 * chip keeps the job and the column it duplicated is gone. The width goes to
 * what the rule means, which is the part you actually read.
 */
const RULE_COLUMNS = [
  { label: "Rule", width: "minmax(0,1.1fr)" },
  { label: "Matches on", width: "minmax(0,1.6fr)" },
  { label: "Used by" },
  { label: "Intent", width: LIST_TRACK.status, mobile: true }
];

const CONDITION_TYPES = [
  { value: "releaseTitle", label: "Release title" },
  { value: "source", label: "Source" },
  { value: "resolution", label: "Resolution" },
  { value: "hdr", label: "HDR format" },
  { value: "codec", label: "Video codec" },
  { value: "releaseGroup", label: "Release group" },
  { value: "language", label: "Language" },
  // #745: matched against what the indexer itself reported for the release
  // (see KNOWN_INDEXER_FLAGS), never against the release name.
  { value: "indexerFlag", label: "Indexer flag" },
  // #775: a min/max range against the release's parsed year or its size.
  { value: "year", label: "Year" },
  { value: "size", label: "Size" },
  // #776: TV only — filtered out for a movie rule below.
  { value: "releaseType", label: "Release type" },
  // #772: a parsed edition tag (IMAX, Extended, Director's Cut, ...).
  { value: "edition", label: "Edition" }
] as const;

// #776: single episode / multi-episode / season pack, exactly the values
// Deluno's own ReleaseEpisodeClassifier reports.
const RELEASE_TYPE_OPTIONS = [
  { value: "single episode", label: "Single episode" },
  { value: "multi-episode", label: "Multi-episode" },
  { value: "season pack", label: "Season pack" }
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
  ruleMigration: GuideRuleMigrationPreview | null;
}

export async function settingsCustomFormatsLoader(): Promise<LoaderData> {
  const [overview, customFormats, guide, guideUpdateCheck, guideVersions, ruleMigration] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchTrashGuidePackage(),
    fetchTrashGuideUpdateCheck(),
    fetchTrashGuideVersions().catch(() => [] as StoredGuidePackage[]),
    fetchGuideRuleMigration().catch(() => null)
  ]);
  return { ...overview, customFormats, guide, guideUpdateCheck, guideVersions, ruleMigration };
}

interface RuleForm {
  name: string;
  mediaType: "movies" | "tv";
  score: string;
  conditions: Condition[];
  trashId: string | null;
  /**
   * A rule stored with TRaSH or Radarr/Sonarr specifications. Deluno's editor
   * cannot represent them, so they are shown as they are and saved untouched
   * - editing one used to turn every specification into an empty title match.
   */
  specifications: string | null;
}

interface SafeguardForm {
  scoringMode: string;
  neverGrab: string[];
}

type DrawerMode =
  | { kind: "rule"; id: string | null }
  | { kind: "preset"; id: string }
  | { kind: "test" }
  | { kind: "guide-move" }
  | { kind: "import" }
  | null;

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["QualityProfileChanged", "PolicySetChanged", "SettingsChanged"], areas: [ConfigAreas.customFormats] };

export function SettingsCustomFormatsPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { customFormats, settings, libraries, policySets, qualityProfiles, guide, guideUpdateCheck, guideVersions, ruleMigration } = useLoaderData() as LoaderData;
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
  const ruleFooter: DrawerSaveState = resolveDrawerSaveState(ruleState, ruleDirty);
  useEffect(() => {
    if (shouldClearSaveState(ruleState, ruleDirty)) setRuleState(undefined);
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

  /**
   * Choosing a guide format fills in its name and score. Its conditions are
   * the guide's own and come from the server when the rule is created, so
   * they are shown here but not copied into the editor.
   */
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
      conditions: [],
      specifications: null
    }));
    setAdvancedOpen(false);
  }

  /**
   * TRaSH's movies AMZN and TV AMZN are different formats with different ids.
   * Switching media type moves a guide choice to that media's copy, or clears
   * it when TRaSH has none.
   */
  function changeRuleMediaType(mediaType: "movies" | "tv") {
    setForm((current) => {
      const chosen = !editing && current.trashId ? guide.customFormats.find((format) => format.trashId === current.trashId) : undefined;
      if (!chosen || guideFormatAppliesTo(chosen, mediaType)) return { ...current, mediaType };
      const counterpart = guide.customFormats.find((format) =>
        format.name === chosen.name && format.category === chosen.category && guideFormatAppliesTo(format, mediaType));
      return counterpart
        ? { ...current, mediaType, trashId: counterpart.trashId, score: String(counterpart.originalScore) }
        : { ...current, mediaType, trashId: null, name: "" };
    });
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
        conditions: !editing && form.trashId
          ? null
          : form.specifications ?? JSON.stringify(form.conditions.filter((condition) => condition.value.trim())),
        upgradeAllowed: true
      });
      const response = editing
        ? await authedFetch(`/api/custom-formats/${editing.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body })
        : await authedFetch("/api/custom-formats", { method: "POST", headers: { "Content-Type": "application/json" }, body });
      if (!response.ok) {
        const detail = await response.text().catch(() => "");
        throw new Error(readErrorMessage(detail, response.url || "/api/custom-formats", response.status));
      }
      const saved = (await response.json()) as CustomFormatItem;
      // The drawer stays on the saved rule, so it has to show what was saved:
      // a rule created from the guide got its conditions from the server, and
      // keeping the pre-save form would save empty conditions over them.
      const next = ruleFrom(saved);
      setForm(next);
      setInitialForm(next);
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
      if (!response.ok && response.status !== 204) throw new Error(await readErrorText(response, "Rule could not be removed."));
      setDrawer(null);
      toast.success(`${format.name} removed`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Rule could not be removed.");
    } finally {
      setBusy(null);
    }
  }

  /* ------------------------------------------------- JSON import/export */
  // #777: a plain download, not a copy-to-clipboard box — the same file a
  // person would keep as a backup or hand to someone else.
  async function exportRule(format: CustomFormatItem) {
    setBusy(`export:${format.id}`);
    try {
      const body = await fetchJson<unknown>(`/api/custom-formats/${format.id}/export`);
      downloadJson(body, `${slugForFileName(format.name)}.json`);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not export this rule.");
    } finally {
      setBusy(null);
    }
  }

  async function exportAllRules() {
    setBusy("export-all");
    try {
      const body = await fetchJson<unknown>("/api/custom-formats/export");
      downloadJson(body, "deluno-custom-formats.json");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not export the custom formats.");
    } finally {
      setBusy(null);
    }
  }

  const [importText, setImportText] = useState("");
  const [importResults, setImportResults] = useState<CustomFormatImportResult[] | null>(null);
  const [importState, setImportState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [importMessage, setImportMessage] = useState<string | null>(null);

  function openImportDrawer() {
    setImportText("");
    setImportResults(null);
    setImportState(undefined);
    setImportMessage(null);
    setDrawer({ kind: "import" });
  }

  async function readImportFile(file: File) {
    setImportText(await file.text());
  }

  async function runImport() {
    let payload: unknown;
    try {
      payload = JSON.parse(importText);
    } catch {
      setImportState("error");
      setImportMessage("That is not valid JSON. Paste what Export produced, unchanged.");
      return;
    }

    setImportState("saving");
    setImportMessage(null);
    setImportResults(null);
    try {
      const response = await authedFetch("/api/custom-formats/import", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      if (!response.ok) {
        const detail = await response.text().catch(() => "");
        throw new Error(readErrorMessage(detail, response.url || "/api/custom-formats/import", response.status));
      }
      const results = (await response.json()) as CustomFormatImportResult[];
      setImportResults(results);
      const imported = results.filter((result) => result.imported).length;
      setImportState(imported === results.length ? "saved" : "error");
      setImportMessage(
        `Imported ${imported} of ${results.length} ${results.length === 1 ? "rule" : "rules"}.`
        + (results.some((result) => result.droppedConditions.length) ? " Some conditions could not be understood — see below." : "")
      );
      revalidator.revalidate();
    } catch (error) {
      setImportState("error");
      setImportMessage(error instanceof Error ? error.message : "Could not import.");
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

  /**
   * A "Movies and TV" preset used to count and add only movie rules, and a TV
   * preset reused the movie ids. Each format is now added for each media type
   * it serves, under that media type's own id.
   */
  /* ------------------------------------------- old catalogue rule move */
  // Rules made from the catalogue Deluno shipped before genuine TRaSH formats.
  // Preselected unless the owner changed a rule's conditions, which a move
  // would replace.
  const moveItems = ruleMigration?.items ?? [];
  const [moveSelection, setMoveSelection] = useState<string[]>([]);
  useEffect(() => {
    setMoveSelection((ruleMigration?.items ?? []).filter((item) => !item.conditionsWereEdited).map((item) => item.ruleId));
  }, [ruleMigration]);
  const [moveState, setMoveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [moveMessage, setMoveMessage] = useState<string | null>(null);

  async function applyGuideMove() {
    if (!ruleMigration || moveSelection.length === 0) return;
    setBusy("guide-move");
    setMoveState("saving");
    setMoveMessage(null);
    try {
      const result = await applyGuideRuleMigration(ruleMigration.previewHash, moveSelection);
      const notMoved = result.outcomes.filter((outcome) => !outcome.moved);
      setMoveState(notMoved.length ? "error" : "saved");
      setMoveMessage(
        `Moved ${result.moved} ${result.moved === 1 ? "rule" : "rules"}; each move is recorded in Activity.`
        + (notMoved.length ? ` Not moved: ${notMoved.map((outcome) => `${outcome.name} (${outcome.message})`).join("; ")}` : "")
      );
      revalidator.revalidate();
    } catch (error) {
      setMoveState("error");
      setMoveMessage(error instanceof Error ? error.message : "The rules could not be moved.");
    } finally {
      setBusy(null);
    }
  }

  function presetProgress(bundle: GuideFormatBundle) {
    const targets = guideBundleTargets(bundle.includes, bundle.mediaType, guide);
    const applied = targets.filter((target) => appliedTrashIds.has(`${target.mediaType}:${target.format.trashId}`));
    return { targets, applied: applied.length, total: targets.length };
  }

  async function applyPreset(bundle: GuideFormatBundle) {
    const { targets, applied } = presetProgress(bundle);
    const missing = targets.filter((target) => !appliedTrashIds.has(`${target.mediaType}:${target.format.trashId}`));
    if (missing.length === 0) {
      setPresetState("saved");
      setPresetMessage("Every rule in this preset is already applied.");
      return;
    }
    setBusy(`preset:${bundle.id}`);
    setPresetState("saving");
    setPresetMessage(null);
    try {
      for (const target of missing) {
        try {
          await createRuleFromGuide(target.format, target.mediaType, target.score);
        } catch (error) {
          throw new Error(`${friendlyRuleName(target.format.name)} (${mediaTypeLabel(target.mediaType)}) could not be added: ${error instanceof Error ? error.message : "unknown error"}`);
        }
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

  const safeguardFooter: DrawerSaveState = resolveDrawerSaveState(safeguardState, safeguardsDirty);
  useUnsavedChanges(safeguardsDirty || Boolean(ruleDirty));
  useEffect(() => {
    if (shouldClearSaveState(safeguardState, safeguardsDirty)) setSafeguardState(undefined);
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
  // What a rule is matched by when this page cannot edit it: a guide choice
  // not yet created (the server writes its conditions), or a rule stored with
  // TRaSH or Radarr/Sonarr specifications.
  const lockedDefinition: RuleSpecification[] | null = form.specifications
    ? readSpecifications(form.specifications)
    : !editing && selectedGuide
      ? isGenuineGuideFormat(selectedGuide)
        ? guideSpecifications(selectedGuide)
        : selectedGuide.patterns.map((pattern) => ({ name: "", implementation: "ReleaseTitleSpecification", negate: false, required: false, value: pattern }))
      : null;
  return (
    <form onSubmit={submitSafeguards} className="flex flex-col gap-(--page-gap)" noValidate>
      <PageToolbar
        tabs={TABS}
        actions={<PageToolbarAction onClick={() => openRule(null)}>New custom rule</PageToolbarAction>}
      />

      {moveItems.length ? (
        <ListCard title="Rules from the old guide catalogue" count={`${moveItems.length} to review`}>
          <div className="flex flex-wrap items-center justify-between gap-3 p-(--card-pad-x)">
            <p className="max-w-3xl text-(length:--type-body-sm) text-muted-foreground">
              {moveItems.length === 1 ? "One rule was" : `${moveItems.length} rules were`} made from Deluno's earlier guide catalogue, whose ids were not TRaSH's and whose rules matched by Deluno's own patterns. Moving {moveItems.length === 1 ? "it" : "them"} onto TRaSH's formats keeps every score and every profile answer.
            </p>
            <Button type="button" onClick={() => { setMoveState(undefined); setMoveMessage(null); setDrawer({ kind: "guide-move" }); }}>
              Review the move
            </Button>
          </div>
        </ListCard>
      ) : null}

      <ListCard title="Guide presets" count="Start with a goal instead of building rules yourself">
        <ListTable
          listId="custom-format-presets"
          items={guide.bundles}
          columns={[
            { id: "preset", label: "Preset", sort: (bundle) => bundle.name.toLowerCase() },
            { id: "bestFor", label: "Best for", width: "minmax(0,1.6fr)", sort: (bundle) => bundle.bestFor.toLowerCase() },
            { id: "rules", label: "Rules", sort: (bundle) => presetProgress(bundle).applied },
            { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (bundle) => { const progress = presetProgress(bundle); return progress.total > 0 && progress.applied === progress.total ? 2 : progress.applied ? 1 : 0; } }
          ]}
        >
          {(rows) => rows.map((bundle) => {
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
        <div className="grid gap-3 p-(--card-pad-x)">
          <SwitchRow
            label="Check upstream guide changes weekly"
            description="Off by default. When enabled, Deluno compares the public TRaSH Git tree with the exact guide files behind your saved rules. A detected update can be staged, reviewed, and explicitly synced; it never changes a local rule or plan automatically."
            checked={updateCheck.isEnabled}
            onCheckedChange={(checked) => void setGuideUpdateCheckEnabled(checked)}
            disabled={updateCheckBusy !== null}
          />
          <div className="flex flex-wrap items-center justify-between gap-2 rounded-[10px] border border-hairline bg-surface-2 px-3 py-2">
            <div className="min-w-0">
              <p className="text-(length:--type-body-sm) font-medium text-foreground">{guideUpdateCheckLabel(updateCheck.status)}</p>
              <p className="mt-0.5 text-(length:--type-caption) text-muted-foreground">
                {(updateCheck.error ? localizeResetTimes(updateCheck.error) : null) ?? updateCheck.report?.summary ?? (updateCheck.isEnabled ? "No guide check has run yet." : "Enable checks to allow an outbound TRaSH Guides comparison.")}
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
              <p className="text-(length:--type-caption) text-muted-foreground">
                {updateCheck.report.changes.filter((change) => change.isInUse).length} changed source item(s) affect saved custom formats. Review these before changing any guide package.
              </p>
              {updateCheck.report.changes.map((change) => (
                <div key={`${change.kind}:${change.id}`} className="rounded-[10px] border border-hairline px-3 py-2">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="text-(length:--type-body-sm) font-medium text-foreground">{change.name}</span>
                    <Chip tone={change.changeType === "removed" || change.isInUse ? "warn" : "info"}>{change.changeType === "removed" ? "Removed upstream" : "Changed upstream"}</Chip>
                  </div>
                  <p className="mt-1 text-(length:--type-caption) text-muted-foreground">
                    {change.mediaType === "tv" ? "TV" : "Movies"} · {change.kind} · {change.isInUse ? `used by ${change.inUseCustomFormatIds.length} saved custom format${change.inUseCustomFormatIds.length === 1 ? "" : "s"}` : "not used by a saved custom format"}
                  </p>
                </div>
              ))}
              {updateCheck.report.addedSources.map((source) => (
                <p key={source.sourcePath} className="text-(length:--type-caption) text-muted-foreground">
                  New upstream {source.mediaType === "tv" ? "TV" : "movie"} {source.kind}: <span className="font-mono">{source.sourcePath}</span>
                </p>
              ))}
              <div className="mt-3 flex flex-wrap items-center justify-between gap-2 rounded-[10px] border border-hairline bg-surface-2 px-3 py-2">
                <p className="max-w-2xl text-(length:--type-caption) text-muted-foreground">
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
                      <p className="text-(length:--type-body-sm) font-medium text-foreground">
                        {guideSyncPreview.canApply ? `Ready to sync guide package v${guideSyncPreview.proposed.version}` : "Sync preview needs attention"}
                      </p>
                      <p className="mt-0.5 text-(length:--type-caption) text-muted-foreground">
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
                  {guideSyncPreview.errors.map((error) => <p key={error} className="mt-2 text-(length:--type-caption) text-destructive">{error}</p>)}
                  {guideSyncPreview.warnings.map((warning) => <p key={warning} className="mt-2 text-(length:--type-caption) text-warning">{warning}</p>)}
                  {/*
                    The diff was computed and then reported as a count. "Three
                    profiles have a diff" is not a readable plan diff - it is
                    the number of readable plan diffs Deluno declined to show
                    (#350).
                  */}
                  {guideSyncPreview.profileDiffs.some((diff) => diff.changes.length > 0) ? (
                    <div className="mt-2 grid gap-2" aria-label="Guide profile plan diff">
                      <p className="text-(length:--type-caption) text-muted-foreground">
                        These guide profiles compile differently under the new snapshot. Syncing does not change them; apply each plan change yourself.
                      </p>
                      {guideSyncPreview.profileDiffs
                        .filter((diff) => diff.changes.length > 0)
                        .map((diff) => (
                          <div key={diff.profileId} className="rounded-[10px] border border-hairline px-3 py-2">
                            <div className="flex flex-wrap items-center justify-between gap-2">
                              <span className="text-(length:--type-body-sm) font-medium text-foreground">{diff.profileName}</span>
                              <span className="text-(length:--type-caption) text-muted-foreground">
                                {diff.currentAdvancedRuleCount} → {diff.proposedAdvancedRuleCount} rule{diff.proposedAdvancedRuleCount === 1 ? "" : "s"} needing review
                              </span>
                            </div>
                            <ul className="mt-1 grid gap-0.5 text-(length:--type-caption) text-muted-foreground">
                              {diff.changes.map((change) => <li key={change}>{change}</li>)}
                            </ul>
                            {diff.warnings.map((warning) => (
                              <p key={warning} className="mt-1 text-(length:--type-caption) text-warning">{warning}</p>
                            ))}
                            <p className="mt-1 font-mono text-(length:--type-caption) text-muted-foreground">
                              {(diff.currentPlanHash ?? "none").slice(0, 12)} → {(diff.proposedPlanHash ?? "none").slice(0, 12)}
                            </p>
                          </div>
                        ))}
                    </div>
                  ) : (
                    <p className="mt-2 text-(length:--type-caption) text-muted-foreground">
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
                      <p className="text-(length:--type-body-sm) font-medium text-foreground">
                        Version {stored.package.version}
                        {stored.isActive ? " · in use" : ""}
                      </p>
                      <p className="mt-0.5 font-mono text-(length:--type-caption) text-muted-foreground">
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

      <ListCard
        title="Advanced release rules"
        // Testing, importing and exporting act on these rules, so they sit with them; the title line keeps "New custom
        // rule" and the tabs keep their room (James, 2 Oct 2026).
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
            <Button type="button" variant="outline" size="sm" onClick={openTestDrawer}>
              <FlaskConical className="h-3.5 w-3.5" />
              Test a release
            </Button>
            {/* #777: JSON import/export, separate from a TRaSH guide sync. */}
            <Button type="button" variant="outline" size="sm" onClick={openImportDrawer}>
              Import
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={() => void exportAllRules()} disabled={busy === "export-all" || customFormats.length === 0}>
              {busy === "export-all" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
              Export all
            </Button>
          </>
        }
        count={customFormats.length ? `${customFormats.length} ${customFormats.length === 1 ? "rule" : "rules"} · full TRaSH and custom controls` : undefined}>
        {customFormats.length === 0 ? (
          <ListEmpty
            title="No advanced release rules yet"
            description="Most people can choose release preferences from a Library Profile. Use this area when you want the full guide catalogue, advanced matching, or your own rule."
            actions={<Button type="button" variant="outline" onClick={() => openRule(null)}><Plus className="h-4 w-4" />New custom rule</Button>}
          />
        ) : (
          <ListTable listId="custom-format-rules" columns={RULE_COLUMNS}>
            {split.groups.flatMap((group) => {
              // Each description once per group (#1138): a second rule that reads the same says so instead of repeating it.
              const shownDescriptions = new Set<string>();
              return [
              split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
              ...group.items.map((format) => {
                const specifications = readSpecifications(format.conditions);
                const conditions = specifications ? [] : parseConditions(format.conditions);
                const conditionCount = specifications?.length ?? conditions.length;
                const guideFormat = format.trashId ? guide.customFormats.find((candidate) => candidate.trashId === format.trashId) : undefined;
                const usedBy = librariesByFormat.get(format.id) ?? [];
                const description = guideFormat?.description ?? (conditions.length ? conditionSummary(conditions) : specifications ? specificationKind(specifications[0]!.implementation) : "No conditions");
                const repeatsAbove = shownDescriptions.has(description);
                shownDescriptions.add(description);
                // The guide's own names are its filenames: "WEB Tier 01",
                // "Repack2", "BR-DISK". The column your eye lands on first
                // should be English, with the guide's name kept underneath
                // because that is what you would search the guide for.
                // A rule the owner renamed is shown by their name.
                const shownName = guideFormat && format.name === guideFormat.name ? friendlyRuleName(guideFormat.name) : format.name;
                return (
                  <ListRow key={format.id} onClick={() => openRule(format)} selected={drawer?.kind === "rule" && drawer.id === format.id}>
                    <ListNameCell
                      name={shownName}
                      sub={guideFormat
                        ? `${shownName === guideFormat.name ? "" : `${guideFormat.name} · `}${isGenuineGuideFormat(guideFormat) ? "TRaSH format" : "Deluno guide format"}`
                        : specifications ? "Imported specifications" : "Written here"}
                    />
                    {/* Guide rules are raw regex — lead with what they mean, not the pattern. */}
                    <ListCell
                      primary={repeatsAbove ? "Reads the same as the rule above" : description}
                      secondary={conditionCount ? `${conditionCount} ${conditionCount === 1 ? "condition" : "conditions"}` : "never matches anything"}
                    />
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
              ];
            })}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Safeguards" count="The final guardrails applied after quality and legacy inputs are worked out">
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <Field
            label="How Deluno picks a release"
            help="Hybrid is the normal choice. Rules only makes your configured policy the sole decision-maker."
            className="max-w-md"
          >
            {/* Its own box keeps the chips from asking the field to be as wide as all three: they scroll inside it on a phone. */}
            <div className="min-w-0 overflow-hidden">
              <FilterChips
                label="How Deluno picks a release"
                value={safeguards.scoringMode}
                onValueChange={(scoringMode) => setSafeguards((current) => ({ ...current, scoringMode }))}
                options={[
                  { value: "hybrid", label: "Rules and ranking together" },
                  { value: "rules-only", label: "Rules only" },
                  { value: "ml-only", label: "Ranking only" }
                ]}
              />
            </div>
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
        // The same name the list showed. Opening "Top-tier WEB groups" and
        // landing on a panel headed "WEB Tier 01" reads as the wrong rule.
        title={editing ? (selectedGuide && editing.name === selectedGuide.name ? friendlyRuleName(selectedGuide.name) : editing.name) : "New custom release rule"}
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
                  const entries = guide.customFormats.filter((cf) => cf.category === category && !cf.bundleOnly && guideFormatAppliesTo(cf, form.mediaType));
                  if (!entries.length) return null;
                  return (
                    <optgroup key={category} label={GUIDE_CATEGORY_LABELS[category] ?? category}>
                      {entries.map((cf) => (
                        <option key={cf.trashId} value={cf.trashId}>
                          {/* #781: marks the release-group tiers and Anime Versions that a
                              fresh anime profile already gets, so this reads as "already on
                              for anime" rather than one more choice to hunt for. */}
                          {friendlyRuleName(cf.name)}{isAnimeDefaultFormat(cf, guide) ? " · Default for anime profiles" : ""}
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
              <p className="text-(length:--type-body-sm) font-medium text-foreground">{friendlyRuleName(selectedGuide.name)}</p>
              <p className="text-(length:--type-caption) text-muted-foreground">{selectedGuide.description}</p>
              <p className="text-(length:--type-caption) text-muted-foreground">{isGenuineGuideFormat(selectedGuide) ? "TRaSH's own format, with TRaSH's id and conditions." : "Deluno's own format: TRaSH does not publish one."}</p>
            </div>
          ) : null}
          <Field label="Name" help="What this rule is looking for, in your own words.">
            <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} placeholder="e.g. Dolby Vision" />
          </Field>
          <Field label="Applies to">
            <SegmentedControl<"movies" | "tv">
              aria-label="Applies to"
              value={form.mediaType}
              onValueChange={changeRuleMediaType}
              options={[
                { value: "movies", label: "Movies" },
                { value: "tv", label: "TV" }
              ]}
            />
          </Field>
          {/*
            Full width and on its own row. It used to share a two-column row
            with "Applies to", which is what pushed it into a dropdown in the
            first place: five options with their consequences do not fit in
            half a drawer.
          */}
          <Field label="Intent" help="What Deluno should do when a release matches this rule.">
            <ChoiceRows
              value={scoreIntent(form.score)}
              onValueChange={(next) => {
                if (next === "custom") return;
                setForm((current) => ({ ...current, score: scoreForIntent(next) }));
              }}
              options={INTENT_OPTIONS.filter((option) =>
                // "Custom" is not a thing you choose; it is what an imported
                // value that fits none of the five is called. Offer it only
                // when the rule is already in that state.
                option.value !== "custom" || scoreIntent(form.score) === "custom")}
            />
          </Field>
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

        {lockedDefinition ? (
          <DrawerSection title="Matching" aside={`${lockedDefinition.length} ${lockedDefinition.length === 1 ? "condition" : "conditions"}`}>
            <p className="text-(length:--type-caption) text-muted-foreground">
              {lockedDefinition.some((specification) => specification.implementation !== "ReleaseTitleSpecification" || specification.required)
                ? "Matched exactly as TRaSH defines it: every required condition must hold, and at least one condition of each kind. These are TRaSH's, so they are not edited here."
                : "Matches a release title containing any one of these patterns."}
            </p>
            <SpecificationList specifications={lockedDefinition} />
          </DrawerSection>
        ) : (
        <DrawerSection title="Matching" aside={selectedGuide ? "Guide-backed" : form.conditions.length ? `${form.conditions.length} criteria` : "Set up below"}>
          <p className="text-(length:--type-caption) text-muted-foreground">
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
              <p className="text-(length:--type-caption) text-muted-foreground">Add at least one matching criterion before saving this custom rule.</p>
            ) : null}
            {form.conditions.map((condition, index) => (
              <div key={index} className="grid gap-2 rounded-[10px] border border-hairline bg-surface-2/40 p-3">
                <FieldRow>
                  <Field label="Match on">
                    <Select
                      value={condition.type}
                      onChange={(event) => updateCondition(setForm, index, { type: event.target.value as ConditionType, value: "" })}
                      options={CONDITION_TYPES
                        // #776: Release Type only makes sense for a TV rule.
                        .filter((type) => type.value !== "releaseType" || form.mediaType === "tv")
                        .map((type) => ({ value: type.value, label: type.label }))}
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
                {condition.type === "year" || condition.type === "size" ? (
                  // #775: a min/max range, not a text search. Either side can
                  // be left blank for an open-ended range ("2020 or later").
                  <FieldRow>
                    <Field label={condition.type === "year" ? "From year" : "Minimum size (GB)"} optional>
                      <Input
                        type="number"
                        value={parseRange(condition.value).min}
                        onChange={(event) => updateCondition(setForm, index, { value: formatRange(event.target.value, parseRange(condition.value).max) })}
                        placeholder="No minimum"
                      />
                    </Field>
                    <Field label={condition.type === "year" ? "To year" : "Maximum size (GB)"} optional>
                      <Input
                        type="number"
                        value={parseRange(condition.value).max}
                        onChange={(event) => updateCondition(setForm, index, { value: formatRange(parseRange(condition.value).min, event.target.value) })}
                        placeholder="No maximum"
                      />
                    </Field>
                  </FieldRow>
                ) : condition.type === "releaseType" ? (
                  <Field label="Release type">
                    <Select
                      value={condition.value}
                      onChange={(event) => updateCondition(setForm, index, { value: event.target.value })}
                      placeholder="Choose a release type"
                      options={RELEASE_TYPE_OPTIONS.map((option) => ({ value: option.value, label: option.label }))}
                    />
                  </Field>
                ) : condition.type === "indexerFlag" ? (
                  // #773: a picker of the flags an indexer actually reports,
                  // not free text — the same known-flag list the indexer form
                  // itself offers (KNOWN_INDEXER_FLAGS), with "Other" for a
                  // site's own unlisted flag.
                  <Field label="Flag to match" help="Matched against what the indexer itself reports for a release, never against the release name.">
                    <PresetField
                      value={condition.value}
                      onChange={(value) => updateCondition(setForm, index, { value })}
                      options={KNOWN_INDEXER_FLAGS.map((flag) => ({ value: flag, label: flag }))}
                      customLabel="Other flag"
                      customPlaceholder="Flag name"
                      placeholder="Choose a flag"
                    />
                  </Field>
                ) : (
                  <Field label="Words to match" help="Plain text is enough. Deluno also accepts a pattern for advanced matching.">
                    <Input
                      value={condition.value}
                      onChange={(event) => updateCondition(setForm, index, { value: event.target.value })}
                      placeholder="e.g. WEB-DL, HDR, or GROUP"
                      className={selectedGuide ? "font-mono" : undefined}
                    />
                  </Field>
                )}
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
        )}

        {editing ? (
          <DrawerSection title="Library impact" aside={librariesByFormat.get(editing.id)?.length ? `${librariesByFormat.get(editing.id)!.length} libraries` : "Not assigned"}>
            <p className="text-(length:--type-caption) text-muted-foreground">This rule only changes releases for libraries whose Library Profile includes it.</p>
            <LibraryImpactLinks libraries={librariesByFormat.get(editing.id) ?? []} emptyLabel="No Library Profile uses this rule yet." />
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection title="Share or back up this rule">
            <p className="text-(length:--type-caption) text-muted-foreground">Download this rule as JSON to back it up or hand it to someone else. Import it back here, or on another Deluno install, from Import above.</p>
            <div>
              <Button type="button" variant="outline" size="sm" onClick={() => void exportRule(editing)} disabled={busy !== null}>
                {busy === `export:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                Export this rule
              </Button>
            </div>
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

      {/* ----------------------------------------------- guide move drawer */}
      <Drawer
        open={drawer?.kind === "guide-move"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title="Move rules onto TRaSH's formats"
        description="Each rule keeps its id, score and profile answers"
        footer={
          <DrawerFooter
            state={moveState ?? "clean"}
            message={moveMessage}
            saveLabel={`Move ${moveSelection.length} ${moveSelection.length === 1 ? "rule" : "rules"}`}
            saveType="button"
            saveEnabled={moveSelection.length > 0}
            onSave={() => void applyGuideMove()}
            onCancel={() => setDrawer(null)}
            disabled={busy !== null}
          />
        }
      >
        <DrawerSection title="What each rule becomes" aside={moveItems.length ? `${moveSelection.length} of ${moveItems.length} chosen` : "Nothing left to move"}>
          <div className="grid gap-2">
            {moveItems.map((item) => {
              const checked = moveSelection.includes(item.ruleId);
              return (
                <label key={item.ruleId} className="grid cursor-pointer grid-cols-[auto_minmax(0,1fr)] gap-3 rounded-[10px] border border-hairline px-3 py-2 hover:bg-surface-2/40">
                  <Checkbox
                    className="mt-0.5"
                    checked={checked}
                    aria-label={`Move ${item.currentName} (${mediaTypeLabel(item.mediaType)})`}
                    onCheckedChange={(next) => setMoveSelection((current) => next ? [...new Set([...current, item.ruleId])] : current.filter((id) => id !== item.ruleId))}
                  />
                  <span className="grid min-w-0 gap-0.5">
                    <span className="text-(length:--type-body-sm) font-medium text-foreground">
                      {item.currentName} → {friendlyRuleName(item.newName)}
                      <span className="font-normal text-muted-foreground"> · {mediaTypeLabel(item.mediaType)}</span>
                    </span>
                    <span className="text-(length:--type-caption) text-muted-foreground">{item.explanation}</span>
                    {item.profileNames.length ? (
                      <span className="text-(length:--type-caption) text-muted-foreground">Used by {item.profileNames.join(", ")}</span>
                    ) : null}
                    {item.conditionsWereEdited ? (
                      <span><Chip tone="warn">Your conditions differ from what Deluno shipped</Chip></span>
                    ) : null}
                  </span>
                </label>
              );
            })}
          </div>
        </DrawerSection>
      </Drawer>

      {/* --------------------------------------------------- import drawer */}
      <Drawer
        open={drawer?.kind === "import"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title="Import custom formats"
        description="Paste or upload what Export produced — a single rule or a whole file"
        footer={
          <DrawerFooter
            state={importState ?? "clean"}
            message={importMessage}
            saveLabel="Import"
            saveType="button"
            saveEnabled={importText.trim().length > 0}
            onSave={() => void runImport()}
            onCancel={() => setDrawer(null)}
            disabled={importState === "saving"}
          />
        }
      >
        <DrawerSection title="JSON to import">
          <Field label="File" optional help="Or paste the JSON below directly.">
            <Input
              type="file"
              accept=".json,application/json"
              onChange={(event) => {
                const file = event.target.files?.[0];
                if (file) void readImportFile(file);
              }}
            />
          </Field>
          <Field label="Custom format JSON" hideLabel>
            <Textarea
              value={importText}
              onChange={(event) => setImportText(event.target.value)}
              placeholder='{"name": "...", "mediaType": "movies", "conditions": [...] }  or  [ {...}, {...} ]'
              rows={10}
              className="font-mono"
            />
          </Field>
        </DrawerSection>

        {importResults ? (
          <DrawerSection title="Result" aside={`${importResults.filter((result) => result.imported).length} of ${importResults.length} imported`}>
            <div className="grid gap-1.5">
              {importResults.map((result, index) => (
                <div key={`${result.name ?? "unnamed"}-${index}`} className="grid gap-1 border-b border-hairline py-2 last:border-b-0">
                  <div className="flex items-center justify-between gap-3">
                    <span className="min-w-0 truncate text-(length:--type-body-sm) text-foreground">{result.name ?? "(unnamed)"}</span>
                    <Chip tone={result.imported ? "ok" : "bad"}>{result.imported ? "Imported" : "Not imported"}</Chip>
                  </div>
                  {result.error ? <p className="text-(length:--type-caption) text-destructive">{result.error}</p> : null}
                  {result.droppedConditions.length ? (
                    <p className="text-(length:--type-caption) text-warning">
                      Could not understand: {result.droppedConditions.join(", ")}
                    </p>
                  ) : null}
                </div>
              ))}
            </div>
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
              <p className="text-(length:--type-body-sm) text-muted-foreground">{presetDetail.description}</p>
              <p className="text-(length:--type-body-sm) text-foreground">
                <span className="font-medium">Best for:</span> {presetDetail.bestFor}
              </p>
              {presetDetail.warnings?.length ? (
                <div className="grid gap-1">
                  {presetDetail.warnings.map((warning) => (
                    <p key={warning} className="text-(length:--type-caption) text-warning">
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
                {presetProgress(presetDetail).targets.map(({ format: cf, mediaType, score }, index, all) => {
                  // A rule a preset adds to both Movies and TV reads the same twice: its description is said on the first line only (#1168).
                  const describedAbove = all.slice(0, index).some((other) => other.format.description === cf.description);
                  const already = appliedTrashIds.has(`${mediaType}:${cf.trashId}`);
                  return (
                    <div key={`${mediaType}:${cf.trashId}`} className="flex items-center justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0">
                      <span className="min-w-0">
                        <span className="block truncate text-(length:--type-body-sm) text-foreground">
                          {friendlyRuleName(cf.name)}
                          {presetDetail.mediaType === "all" ? <span className="text-muted-foreground"> · {mediaTypeLabel(mediaType)}</span> : null}
                        </span>
                        {describedAbove ? null : <span className="block truncate text-(length:--type-caption) text-muted-foreground" title={cf.description}>{cf.description}</span>}
                      </span>
                      <span className="flex shrink-0 items-center gap-2">
                        <span className="text-(length:--type-caption) text-muted-foreground">{ruleIntent(score)}</span>
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
          <Field label="Release preferences to test against" optional help="The same release preferences search and import use. Leave it blank to check only against the older rules.">
            <Select
              value={testProfileId}
              onChange={(event) => setTestProfileId(event.target.value)}
              placeholder={testProfiles.length ? "Choose a Quality Profile" : "No Quality Profiles available"}
              options={testProfiles.map((profile) => ({ value: profile.id, label: `${profile.name} · ${profile.mediaType === "tv" ? "TV" : "Movies"}` }))}
              disabled={!testProfiles.length}
            />
          </Field>
          {customFormats.length === 0 ? (
            <p className="text-(length:--type-caption) text-warning">You have no rules yet, so nothing can match. Apply a preset first.</p>
          ) : null}
        </DrawerSection>

        {results ? (
          <DrawerSection
            title="Result"
            aside={`${results.filter((result) => result.isMatch).length} of ${results.length} matched`}
          >
            {results.length === 0 ? (
              <p className="text-(length:--type-caption) text-muted-foreground">Nothing to report — the test could not be run.</p>
            ) : (
              <div className="grid gap-1.5">
                {[...results].sort((a, b) => Number(b.isMatch) - Number(a.isMatch)).map((result) => (
                  <div key={result.formatId} className="grid gap-1 border-b border-hairline py-2 last:border-b-0">
                    <div className="flex items-center justify-between gap-3">
                      <span className={`min-w-0 truncate text-(length:--type-body-sm) ${result.isMatch ? "text-foreground" : "text-muted-foreground"}`}>
                        {result.formatName}
                      </span>
                      <span className="flex shrink-0 items-center gap-2">
                        <span className="text-(length:--type-caption) text-muted-foreground">{result.isMatch ? ruleIntent(result.score) : "No intent"}</span>
                        <Chip tone={result.isMatch ? "ok" : "idle"}>{result.isMatch ? "Matched" : "No match"}</Chip>
                      </span>
                    </div>
                    {result.isMatch && result.matchedConditions.length ? (
                      <span className="block truncate text-(length:--type-caption) text-muted-foreground">
                        {result.matchedConditions.map(describeMatchedCondition).join(" · ")}
                      </span>
                    ) : null}
                  </div>
                ))}
              </div>
            )}
          </DrawerSection>
        ) : null}

        {typedPreviewError ? (
          <DrawerSection title="How this release is judged">
            <p role="alert" className="text-(length:--type-caption) text-destructive">{typedPreviewError}</p>
          </DrawerSection>
        ) : null}

        {typedPreview ? (
          <DrawerSection title="How this release is judged" aside={typedPreview.comparison ? typedComparisonLabel(typedPreview.comparison.status) : typedStatusLabel(typedPreview.candidateEvaluation.status)}>
            <div className="grid gap-3">
              <div className="flex flex-wrap items-center gap-2">
                <Chip tone={typedPreview.comparison?.status === "rejected" ? "bad" : typedPreview.comparison?.status === "needsReview" ? "warn" : typedPreview.comparison?.status === "currentBetter" || typedPreview.comparison?.status === "equivalent" ? "info" : typedPreview.candidateEvaluation.status === "meetsPlan" ? "ok" : "info"}>
                  {typedPreview.comparison ? typedComparisonLabel(typedPreview.comparison.status) : typedStatusLabel(typedPreview.candidateEvaluation.status)}
                </Chip>
                <span className="text-(length:--type-caption) text-muted-foreground">Plan {typedPreview.planVersion}</span>
              </div>
              {typedPreview.comparison?.reasons.length ? (
                <ul className="grid gap-1 text-(length:--type-body-sm) text-muted-foreground" aria-label="Typed comparison reasons">
                  {typedPreview.comparison.reasons.map((reason) => <li key={reason}>{reason}</li>)}
                </ul>
              ) : null}
              {typedPreview.candidateEvaluation.reasons.length ? (
                <ul className="grid gap-1 text-(length:--type-caption) text-muted-foreground" aria-label="Typed evaluation reasons">
                  {typedPreview.candidateEvaluation.reasons.map((reason) => <li key={reason}>{reason}</li>)}
                </ul>
              ) : null}
              <div className="grid gap-1.5" aria-label="Typed family outcomes">
                {typedPreview.candidateEvaluation.families.map((family) => (
                  <div key={family.familyId} className="flex flex-wrap items-start justify-between gap-2 rounded-[10px] border border-hairline px-3 py-2">
                    <span className="min-w-0 text-(length:--type-body-sm) text-foreground">{humanizeTypedName(family.familyId)}</span>
                    <span className="max-w-full text-right text-(length:--type-caption) text-muted-foreground">{family.explanation}</span>
                  </div>
                ))}
              </div>
              <p className="text-(length:--type-caption) text-muted-foreground">Detected {typedPreview.candidateFacts.length} typed evidence item{typedPreview.candidateFacts.length === 1 ? "" : "s"}. The plan hash is {typedPreview.planHash.slice(0, 12)}…</p>
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

/** One imported format's outcome (#777, #774) from POST /api/custom-formats/import. */
interface CustomFormatImportResult {
  name: string | null;
  imported: boolean;
  id: string | null;
  droppedConditions: string[];
  error: string | null;
}

/** Triggers a browser download of `data` as pretty-printed JSON (#777). */
function downloadJson(data: unknown, fileName: string) {
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  link.click();
  URL.revokeObjectURL(url);
}

export function slugForFileName(name: string) {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "custom-format";
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

/**
 * A year/size condition's value is stored as "min-max" (either side
 * optional), matching what the matcher and an imported Radarr
 * YearSpecification/SizeSpecification both use (#775).
 */
export function parseRange(value: string): { min: string; max: string } {
  const separator = value.indexOf("-");
  if (separator < 0) return { min: value, max: "" };
  return { min: value.slice(0, separator), max: value.slice(separator + 1) };
}

export function formatRange(min: string, max: string): string {
  return `${min.trim()}-${max.trim()}`;
}

function updateCondition(setForm: React.Dispatch<React.SetStateAction<RuleForm>>, index: number, patch: Partial<Condition>) {
  setForm((current) => ({
    ...current,
    conditions: current.conditions.map((condition, i) => (i === index ? { ...condition, ...patch } : condition))
  }));
}

function emptyRule(): RuleForm {
  return { name: "", mediaType: "movies", score: "100", conditions: [], trashId: null, specifications: null };
}

function ruleFrom(format: CustomFormatItem): RuleForm {
  return {
    name: format.name,
    mediaType: format.mediaType === "tv" ? "tv" : "movies",
    score: String(format.score),
    conditions: readSpecifications(format.conditions) ? [] : parseConditions(format.conditions),
    trashId: format.trashId ?? null,
    specifications: readSpecifications(format.conditions) ? format.conditions : null
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

/** TRaSH's specifications as TRaSH names them, shown rather than edited. */
function SpecificationList({ specifications }: { specifications: RuleSpecification[] }) {
  return (
    <ul className="grid gap-1.5" aria-label="Matching conditions">
      {specifications.map((specification, index) => (
        <li key={index} className="grid gap-0.5 rounded-[10px] border border-hairline bg-surface-2/40 px-3 py-2">
          <span className="flex flex-wrap items-center gap-2 text-(length:--type-body-sm) text-foreground">
            {specificationKind(specification.implementation)}
            {specification.name ? <span className="text-muted-foreground">{specification.name}</span> : null}
            {specification.negate ? <Chip tone="warn">must not match</Chip> : null}
            {specification.required ? <Chip tone="info">required</Chip> : null}
          </span>
          <span className="break-all font-mono text-(length:--type-caption) text-muted-foreground">{specification.value}</span>
        </li>
      ))}
    </ul>
  );
}

function conditionSummary(conditions: Condition[]) {
  const first = conditions[0]!;
  const label = CONDITION_TYPES.find((type) => type.value === first.type)?.label ?? first.type;
  // Patterns can be long regex; the drawer shows them in full.
  const value = first.value.length > 32 ? `${first.value.slice(0, 32)}…` : first.value;
  return `${label} ${first.negate ? "without" : "with"} “${value}”`;
}

/**
 * The matcher's raw "type:value" label (and "NOT type:value" when negated),
 * turned into a plain phrase for the test-a-release result. For "language"
 * specifically (#758), this is what shows which parsed language actually
 * matched — never a raw text location, which Deluno does not track.
 */
export function describeMatchedCondition(label: string): string {
  const negated = label.startsWith("NOT ");
  const body = negated ? label.slice(4) : label;
  const separator = body.indexOf(":");
  if (separator < 0) return label;
  const type = body.slice(0, separator);
  const value = body.slice(separator + 1);
  const friendly = CONDITION_TYPES.find((candidate) => candidate.value.toLowerCase() === type.toLowerCase())?.label ?? type;
  const phrase = type.toLowerCase() === "language" ? `Language matched in the release name: ${value}` : `${friendly}: ${value}`;
  return negated ? `Not ${phrase.charAt(0).toLowerCase()}${phrase.slice(1)}` : phrase;
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
