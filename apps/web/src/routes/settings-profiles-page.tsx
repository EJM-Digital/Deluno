/**
 * Quality Profiles — advanced list → drawer.
 *
 *   PageToolbar (Library Profiles tabs · New quality profile)
 *   ListCard  (name · allowed tiers · stops at · used by · status · ›)
 *   Drawer    (Start from · Basics · Quality tiers · Formats [Fine-tune] · Used by · Delete)
 *
 * Tier vocabulary comes from /api/quality-model — the same names Size Rules and
 * the decision engine use — so a profile's allowed/cutoff values always resolve.
 *
 * Contracts: GET/POST /api/quality-profiles, PUT/DELETE /api/quality-profiles/{id},
 * POST /api/custom-formats (when a preset needs a format that doesn't exist yet).
 */
import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { ArrowDown, ArrowUp, Plus, X } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { ListGroupHeader, MediaTypeFilter, useMediaTypeSplit } from "../components/ui/media-type-split";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { configurationNavAreas } from "../components/app/settings-shell";
import { QualityBuildSteps, guideIntentFor } from "../components/app/quality-build-steps";
import { ProfileSizeSteps } from "../components/app/profile-size-steps";
import { QUALITY_STEPS, type QualityStep } from "../lib/quality-steps";
import { createRuleFromGuide, guideFormatAppliesTo } from "../lib/guide-formats";
import { friendlyRuleName } from "../lib/guide-names";
import {
  CUTOFF_LABELS,
  GENERIC_MIN_FORMAT_SCORE,
  GENERIC_CUTOFF_FORMAT_SCORE,
  GENERIC_MIN_UPGRADE_FORMAT_SCORE,
  LANGUAGE_LABELS,
  UPGRADE_GAIN_LABELS,
  mapCutoffScore,
  mapMinScore,
  mapUpgradeGainScore,
  nearestCutoffChoice,
  nearestUpgradeGainChoice,
  type QualityCutoffChoice,
  type QualityUpgradeGainChoice
} from "../lib/quality-limits";
import {
  compileQualityProfilePreferences,
  fetchTrashGuidePackage,
  fetchJson,
  readValidationProblem,
  type CustomFormatItem,
  type GuidePackage,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityModelSnapshot,
  type QualityProfileItem,
  type QualityTierDefinition,
  type QualityUpgradeStopPolicy,
  type ProfileSizeRule,
  type ProfileAcquisitionRules,
  type ProfileScoreLimits,
  type ProfileLanguagePreference,
  type ReleasePreferencePlanCompilation
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

const TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  settings: PlatformSettingsSnapshot;
  qualityModel: QualityModelSnapshot;
  guide: GuidePackage;
}

interface ProfileForm {
  name: string;
  mediaType: "movies" | "tv";
  /** Allowed tiers in preference order, least → most preferred (matches storage). */
  allowed: string[];
  cutoff: string;
  customFormatIds: string[];
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
  sizeRules: ProfileSizeRule[];
  requireFormatGain: boolean;
  formatIntents: Record<string, string>;
  acquisition: ProfileAcquisitionRules;
  /** "Never grab copies TRaSH rates as bad." */
  neverGrabBad: boolean;
  /** "Stop upgrading once a copy is..." */
  cutoffChoice: QualityCutoffChoice;
  /** "Only replace a file when the new copy is..." */
  upgradeGainChoice: QualityUpgradeGainChoice;
  /** Preferred languages, best-first. Empty means no language opinion. */
  languageCodes: string[];
  /** "Require" the most preferred language rather than merely prefer it. */
  requireLanguage: boolean;
  /**
   * The numbers behind the three plain choices above, editable only under
   * "Show the numbers" (#938). Null means "use the plain choice's own
   * mapping" - a person who never opens that section never sets these by
   * hand.
   */
  minFormatScore: number | null;
  cutoffFormatScore: number | null;
  minUpgradeFormatScore: number | null;
}

const EMPTY_ACQUISITION: ProfileAcquisitionRules = {
  preferredProtocol: "any",
  usenetDelayMinutes: 0,
  torrentDelayMinutes: 0,
  mustContain: "",
  mustNotContain: ""
};

const QUALITY_LIMITS_DEFAULTS = {
  neverGrabBad: false,
  cutoffChoice: "best" as QualityCutoffChoice,
  upgradeGainChoice: "aLittle" as QualityUpgradeGainChoice,
  languageCodes: [] as string[],
  requireLanguage: false,
  minFormatScore: null as number | null,
  cutoffFormatScore: null as number | null,
  minUpgradeFormatScore: null as number | null
};

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsProfilesLoader(): Promise<LoaderData> {
  const [overview, customFormats, qualityModel, guide] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchJson<QualityModelSnapshot>("/api/quality-model"),
    fetchTrashGuidePackage()
  ]);
  return { ...overview, customFormats, qualityModel, guide };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["QualityProfileChanged", "LibraryChanged"], areas: [ConfigAreas.customFormats, ConfigAreas.qualityModel] };

export function SettingsProfilesPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { libraries, qualityProfiles, customFormats, qualityModel, guide } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();

  const tiers = useMemo(() => [...qualityModel.tiers].sort((a, b) => b.rank - a.rank), [qualityModel.tiers]);
  const profileStarters = useMemo(() => guideStarters(guide, tiers), [guide, tiers]);
  const sorted = useMemo(() => [...qualityProfiles].sort((a, b) => a.mediaType.localeCompare(b.mediaType) || a.name.localeCompare(b.name)), [qualityProfiles]);
  const split = useMediaTypeSplit(sorted, (profile) => profile.mediaType);

  const librariesByProfile = useMemo(() => {
    const map = new Map<string, LibraryItem[]>();
    for (const library of libraries) {
      if (!library.qualityProfileId) continue;
      map.set(library.qualityProfileId, [...(map.get(library.qualityProfileId) ?? []), library]);
    }
    return map;
  }, [libraries]);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<ProfileForm>(() => emptyForm());
  const [initialForm, setInitialForm] = useState<ProfileForm>(() => emptyForm());
  const [starterId, setStarterId] = useState("");
  const [addRecommended, setAddRecommended] = useState(true);
  const [fineTuneOpen, setFineTuneOpen] = useState(false);
  const [showNumbersOpen, setShowNumbersOpen] = useState(false);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<{ name?: string; allowed?: string; cutoff?: string }>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);
  const [planCompilation, setPlanCompilation] = useState<ReleasePreferencePlanCompilation | null>(null);
  const [planSourcesOpen, setPlanSourcesOpen] = useState(false);
  const [planLoading, setPlanLoading] = useState(false);
  const [planError, setPlanError] = useState<string | null>(null);
  const [advancedPlanOpen, setAdvancedPlanOpen] = useState(false);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? qualityProfiles.find((profile) => profile.id === mode.id) ?? null : null;
  const dirty = useMemo(() => isOpen && !sameForm(form, initialForm), [isOpen, form, initialForm]);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  useEffect(() => {
    if (!editing) {
      setPlanCompilation(null);
      setPlanError(null);
      setPlanLoading(false);
      return;
    }

    let cancelled = false;
    setPlanLoading(true);
    setPlanError(null);
    void compileQualityProfilePreferences(editing.id)
      .then((compilation) => {
        if (!cancelled) setPlanCompilation(compilation);
      })
      .catch((error) => {
        if (!cancelled) {
          setPlanCompilation(null);
          setPlanError(error instanceof Error ? error.message : "The typed preference plan could not be loaded.");
        }
      })
      .finally(() => {
        if (!cancelled) setPlanLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [editing?.id]);

  const availableFormats = useMemo(() => customFormats.filter((format) => format.mediaType === form.mediaType), [customFormats, form.mediaType]);
  const unusedTiers = useMemo(() => tiers.filter((tier) => !form.allowed.includes(tier.name)), [tiers, form.allowed]);
  /** Most-preferred first for display; storage keeps the raw order. */
  const allowedForDisplay = useMemo(() => [...form.allowed].reverse(), [form.allowed]);

  function openCreate() {
    const next = defaultForm(profileStarters, "movies", guide, customFormats);
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    setStarterId("");
    setFineTuneOpen(false);
    setSaveState(undefined);
    setErrors({});
    setPlanCompilation(null);
    setPlanError(null);
    setAdvancedPlanOpen(false);
  }
  function openEdit(profile: QualityProfileItem) {
    const next = formFrom(profile);
    setMode({ kind: "edit", id: profile.id });
    setForm(next);
    setInitialForm(next);
    setStarterId("");
    setFineTuneOpen(false);
    setSaveState(undefined);
    setErrors({});
    setPlanCompilation(null);
    setPlanError(null);
    setAdvancedPlanOpen(false);
  }
  /**
   * A copy to change, leaving the original as it is.
   *
   * Opens as a new profile filled in from this one - tiers, sizes, rules and
   * every answer - and nothing exists until it is saved, so closing it leaves
   * no half-made profile behind. The starter's recommended rules are not added
   * again: the copy already has exactly the rules the original has.
   *
   * Takes the drawer's own form, not `editing` (the loader's copy of this
   * profile). Save writes the freshly-saved answers into `form` synchronously,
   * but only starts `revalidator.revalidate()` for `qualityProfiles` without
   * awaiting it, and the Duplicate button re-enables as soon as `busy` clears -
   * before that background refetch lands. Reading `editing` here raced it: rare
   * with nothing else competing for the network, routine under a full suite,
   * where the loader's four requests lose to one click, and the copy opened
   * with the answers from before the save.
   */
  function openDuplicate(current: ProfileForm) {
    const taken = new Set(qualityProfiles.map((item) => item.name.trim().toLowerCase()));
    let name = `Copy of ${current.name}`;
    for (let n = 2; taken.has(name.toLowerCase()); n += 1) name = `Copy of ${current.name} (${n})`;
    const next = { ...current, name };
    setMode({ kind: "create" });
    setForm(next);
    // Against an empty form, so the copy reads as unsaved and Save is offered at once.
    setInitialForm(emptyForm());
    setStarterId("");
    setAddRecommended(false);
    setFineTuneOpen(false);
    setSaveState(undefined);
    setErrors({});
    setPlanCompilation(null);
    setPlanError(null);
    setAdvancedPlanOpen(false);
  }

  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  function applyStarter(id: string) {
    setStarterId(id);
    const starter = profileStarters.find((item) => item.id === id);
    if (!starter) {
      setForm(emptyForm());
      return;
    }
    setAddRecommended(true);
    setForm((current) => ({
      ...emptyForm(),
      name: current.name.trim() ? current.name : `${starter.mediaType === "tv" ? "TV Shows" : "Movies"} / ${starter.label}`,
      mediaType: starter.mediaType,
      allowed: starter.allowed.filter((name) => tiers.some((tier) => tier.name === name)),
      cutoff: starter.cutoff,
      // A TRaSH-based plan sets the plain choices to what TRaSH recommends
      // (#938): the numbers stay hidden, but they are not blank - they are
      // this profile's guide answer, round-tripped through the same mapping
      // "Show the numbers" would show.
      neverGrabBad: starter.minFormatScore > 0,
      cutoffChoice: nearestCutoffChoice(starter.cutoffFormatScore, starter.minFormatScore, starter.cutoffFormatScore),
      upgradeGainChoice: nearestUpgradeGainChoice(starter.minUpgradeFormatScore, GENERIC_MIN_UPGRADE_FORMAT_SCORE),
      languageCodes: languageCodeFromGuideLanguage(starter.language),
      requireLanguage: false,
      // The guide's own numbers, so they save as this profile's real answer
      // (visible, and editable, only under "Show the numbers") rather than
      // being re-derived from the generic defaults on save.
      minFormatScore: starter.minFormatScore > 0 ? starter.minFormatScore : null,
      cutoffFormatScore: starter.cutoffFormatScore,
      minUpgradeFormatScore: starter.minUpgradeFormatScore
    }));
  }

  function setAcquisition(patch: Partial<ProfileAcquisitionRules>) {
    setForm((current) => ({ ...current, acquisition: { ...current.acquisition, ...patch } }));
  }

  function setMediaType(mediaType: "movies" | "tv") {
    if (mediaType === form.mediaType) return;
    setForm((current) => {
      // A movie ladder is not a TV ladder, and the formats belong to one type
      // or the other. On a profile that has not been named yet, switching type
      // re-answers step 1 for the new one rather than leaving it holding tiers
      // from the wrong media - which would be the blank step this redesign
      // exists to avoid, arrived at sideways.
      const untouched = current.allowed.length === 0
        || defaultForm(profileStarters, current.mediaType, guide, customFormats).allowed.join("|")
          === current.allowed.join("|");
      const next = untouched ? defaultForm(profileStarters, mediaType, guide, customFormats) : current;
      return { ...next, name: current.name, mediaType, customFormatIds: [] };
    });
  }

  function addTier(name: string) {
    if (!name) return;
    setErrors((current) => ({ ...current, allowed: undefined }));
    setForm((current) => {
      // Keep storage order least → most preferred, by rank.
      const next = [...current.allowed, name].sort((a, b) => rankOf(tiers, a) - rankOf(tiers, b));
      return { ...current, allowed: next, cutoff: current.cutoff || name };
    });
  }
  function removeTier(name: string) {
    setForm((current) => {
      const allowed = current.allowed.filter((tier) => tier !== name);
      return { ...current, allowed, cutoff: current.cutoff === name ? allowed[allowed.length - 1] ?? "" : current.cutoff };
    });
  }
  /** Move within the displayed (most-preferred-first) order. */
  function moveTier(name: string, direction: -1 | 1) {
    setForm((current) => {
      const display = [...current.allowed].reverse();
      const index = display.indexOf(name);
      const target = index + direction;
      if (index < 0 || target < 0 || target >= display.length) return current;
      [display[index], display[target]] = [display[target]!, display[index]!];
      return { ...current, allowed: display.reverse() };
    });
  }
  function toggleFormat(id: string) {
    setForm((current) => ({ ...current, customFormatIds: current.customFormatIds.includes(id) ? current.customFormatIds.filter((item) => item !== id) : [...current.customFormatIds, id] }));
  }

  /* ---------------------------------------------------------- saving */
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    const nextErrors: typeof errors = {};
    if (!form.name.trim()) nextErrors.name = "Give this quality profile a name.";
    if (!form.allowed.length) nextErrors.allowed = "Allow at least one quality tier.";
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) return;

    setBusy(true);
    setSaveState("saving");
    try {
      const starterFormats = mode.kind === "create" && addRecommended ? await ensureRecommendedFormats(formatsStarterId, form.mediaType, customFormats, guide) : [];
      const starterFormatIds = starterFormats.map((format) => format.id);
      const formatIds = [...new Set([...form.customFormatIds, ...starterFormatIds])];
      // Choosing a starter with its recommended rules is choosing its answers,
      // so they are saved as the profile's own, where they can be seen and
      // changed. A rule with no saved answer is off, and nothing infers one
      // later, so these would otherwise arrive switched off.
      const starterIntents = Object.fromEntries(starterFormats.map((format) => [format.id, guideIntentFor(format.score)]));
      const payload = {
        name: form.name.trim(),
        mediaType: form.mediaType,
        cutoffQuality: form.cutoff || form.allowed[form.allowed.length - 1],
        allowedQualities: form.allowed.join(", "),
        customFormatIds: formatIds.join(", "),
        upgradeUntilCutoff: form.upgradeUntilCutoff,
        upgradeUnknownItems: form.upgradeUnknownItems,
        // Only the tiers this profile still allows. Removing a tier from step 1
        // should take its size answer with it, rather than leaving a rule for a
        // tier the profile no longer accepts.
        sizeRules: form.sizeRules.filter((rule) =>
          form.allowed.some((quality) => quality.toLowerCase() === rule.quality.toLowerCase())),
        upgradeStop: {
          // One answer, two stages. "Upgrade until cutoff" is the owner's
          // words for it; stopping once the cutoff is met is the same
          // sentence read from the other end.
          stopWhenCutoffMet: form.upgradeUntilCutoff,
          requireCustomFormatGainForSameQuality: form.requireFormatGain
        },
        // Only for preferences still selected, so deselecting one takes its
        // answer with it rather than leaving an orphan.
        formatIntents: Object.fromEntries(
          Object.entries({ ...starterIntents, ...form.formatIntents }).filter(([id]) => formatIds.includes(id))),
        acquisition: form.acquisition,
        scoreLimits: scoreLimitsFromForm(form),
        languagePreference: {
          preferredLanguages: form.languageCodes,
          requireLanguage: form.requireLanguage
        } satisfies ProfileLanguagePreference
      };
      const response = await authedFetch(mode.kind === "edit" ? `/api/quality-profiles/${mode.id}` : "/api/quality-profiles", { method: mode.kind === "edit" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      if (!response.ok) {
        const problem = await readValidationProblem(response.clone());
        const validationMessage = problem?.errors?.allowedQualities?.[0] ?? problem?.errors?.cutoffQuality?.[0];
        if (problem?.errors?.allowedQualities?.[0] || problem?.errors?.cutoffQuality?.[0]) {
          setErrors((current) => ({
            ...current,
            allowed: problem.errors?.allowedQualities?.[0] ?? current.allowed,
            cutoff: problem.errors?.cutoffQuality?.[0] ?? current.cutoff
          }));
        }
        throw new Error(validationMessage ?? (mode.kind === "edit" ? "Profile could not be saved." : "Profile could not be created."));
      }
      const saved = (await response.json().catch(() => null)) as QualityProfileItem | null;
      if (mode.kind === "create" && saved) setMode({ kind: "edit", id: saved.id });
      const settled = saved ? formFrom(saved) : { ...form, name: payload.name };
      setForm((current) => settleFormAfterSave(current, form, settled));
      setInitialForm(settled);
      setSaveState("saved");
      setSaveMessage(mode.kind === "create" ? "Profile created" : "Saved just now");
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
      const response = await authedFetch(`/api/quality-profiles/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Profile could not be removed.");
      toast.success(`${editing?.name ?? "Profile"} removed`);
      setConfirmRemove(false);
      setInitialForm(form);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Profile could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  /* ---------------------------------------------------------- render */
  const usedBy = editing ? librariesByProfile.get(editing.id) ?? [] : [];
  // A custom profile starts from the same baseline formats as the guide's default template for its media type.
  const baselineStarter = baselineStarterFor(profileStarters, form.mediaType);
  const formatsStarterId = starterId || baselineStarter?.id || "";
  const recommendedCount = recommendedFormatsFor(formatsStarterId, guide).length;

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        tabs={TABS}
        actions={<PageToolbarAction onClick={openCreate}>New quality profile</PageToolbarAction>}
      />


      <ListCard title={qualityProfiles.length ? `${qualityProfiles.length} ${qualityProfiles.length === 1 ? "profile" : "profiles"}` : "0 profiles"} titleCase={false} actions={<MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />}>
        {qualityProfiles.length === 0 ? (
          <ListEmpty
            title="No quality profiles yet"
            description="A Quality Profile defines the quality, size, release preferences, exclusions, and upgrade point for a library. Library Profiles attach it to the searches and upgrades that use it."
            actions={
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus className="h-3.5 w-3.5" />
                New quality profile
              </Button>
            }
          />
        ) : (
          <ListTable listId="quality-profiles" columns={[{ label: "Name" }, { label: "Allowed", width: "minmax(0,1.4fr)" }, { label: "Stops at" }, { label: "Used by" }, { label: "Formats", width: LIST_TRACK.status, mobile: true }]}>
            {split.groups.flatMap((group) => [
              split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
              ...group.items.map((profile) => {
              const allowed = splitCsv(profile.allowedQualities);
              const formats = splitCsv(profile.customFormatIds);
              const used = librariesByProfile.get(profile.id) ?? [];
              const unknownTiers = allowed.filter((name) => !tiers.some((tier) => tier.name === name));
              return (
                <ListRow key={profile.id} onClick={() => openEdit(profile)} selected={mode.kind === "edit" && mode.id === profile.id}>
                  <ListNameCell name={profile.name} sub={profile.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={[...allowed].reverse().join(" → ") || <span className="text-muted-foreground">None</span>} secondary={unknownTiers.length ? `${unknownTiers.length} tier${unknownTiers.length === 1 ? "" : "s"} not in the quality model` : `${allowed.length} tier${allowed.length === 1 ? "" : "s"}`} />
                  <ListCell primary={profile.cutoffQuality} secondary={profile.upgradeUntilCutoff ? "Upgrades until this tier" : "No upgrades"} />
                  <ListCell numeric primary={used.length ? `${used.length} ${used.length === 1 ? "library" : "libraries"}` : <span className="text-muted-foreground">Not assigned</span>} secondary={used.map((library) => library.name).join(", ") || "Assigned from a library or plan"} />
                  <ListCell mobile>
                    <Chip tone={unknownTiers.length ? "warn" : formats.length ? "info" : "idle"}>{unknownTiers.length ? "Check tiers" : formats.length ? `${formats.length} format${formats.length === 1 ? "" : "s"}` : "No formats"}</Chip>
                  </ListCell>
                </ListRow>
              );
            })
            ])}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={isOpen}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={mode.kind === "create" ? "New quality profile" : editing?.name ?? form.name}
        description={mode.kind === "create" ? "Set the quality you want Deluno to accept, prefer, upgrade to, or reject." : `Quality profile · ${form.mediaType === "tv" ? "TV" : "Movies"} · stops at ${form.cutoff || "—"}`}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create quality profile" : "Save quality profile"} onCancel={requestClose} saveEnabled={mode.kind === "create" ? true : undefined} disabled={busy} />}
      >
        {mode.kind === "create" ? (
          <DrawerSection title="Template">
            <Field label="Choose a template" help="A predefined starting point based on TRaSH Guide recommendations. Everything below stays editable.">
              <Select value={starterId} onChange={(event) => applyStarter(event.target.value)} options={[{ value: "", label: "Create a custom Quality Profile" }, ...profileStarters.map((starter) => ({ value: starter.id, label: `${starter.label} · ${starter.mediaType === "tv" ? "TV" : "Movies"}` }))]} />
            </Field>
            {starterId ? <p className="-mt-1 text-(length:--type-caption) text-muted-foreground">{profileStarters.find((starter) => starter.id === starterId)?.summary}</p> : null}
            {recommendedCount ? (
              <SwitchRow
                label="Add the recommended formats"
                description={`${recommendedCount} TRaSH-guide preference rule${recommendedCount === 1 ? "" : "s"} — repack/proper preferences, codec and upscale handling${starterId ? "" : `, the baseline every custom ${form.mediaType === "tv" ? "TV" : "movie"} profile starts with (from "${baselineStarter?.label}")`}. Created on save if they don't exist yet.`}
                checked={addRecommended}
                onCheckedChange={setAddRecommended}
              />
            ) : null}
          </DrawerSection>
        ) : null}

        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Profile name" error={errors.name}>
              <Input value={form.name} onChange={(event) => { setErrors((current) => ({ ...current, name: undefined })); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Name this quality profile" autoComplete="off" />
            </Field>
            <Field label="Media type">
              <SegmentedControl<"movies" | "tv"> value={form.mediaType} onValueChange={setMediaType} options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV shows" }]} />
            </Field>
          </FieldRow>
        </DrawerSection>

        <DrawerSection title="Build this profile">
          <p className="text-(length:--type-caption) text-muted-foreground">
            Seven questions. Each one already has an answer, so straight through gives you a
            good profile — open the ones you want to change.
          </p>
          <QualityBuildSteps
            mediaType={form.mediaType}
            name={form.name}
            allowed={form.allowed}
            cutoff={form.cutoff}
            customFormatIds={form.customFormatIds}
            upgradeUntilCutoff={form.upgradeUntilCutoff}
            upgradeUnknownItems={form.upgradeUnknownItems}
            customFormats={customFormats}
            guide={guide}
            formatIntents={form.formatIntents}
            onCustomFormatIdsChange={(customFormatIds) => setForm((current) => ({ ...current, customFormatIds }))}
            onFormatIntentChange={(formatId, intent) =>
              setForm((current) => ({ ...current, formatIntents: { ...current.formatIntents, [formatId]: intent } }))}
            renderQualityControls={() => (
              <div className="grid gap-(--grid-gap)">
          {allowedForDisplay.length ? (
            <ul className="grid gap-2">
              {allowedForDisplay.map((name, index) => {
                const tier = tiers.find((candidate) => candidate.name === name);
                return (
                  <li key={name} className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-(--field-pad-x)">
                    <span className="w-5 shrink-0 text-(length:--type-caption) tabular-nums text-muted-foreground">{index + 1}</span>
                    <span className="min-w-0 flex-1 truncate text-(length:--type-body-sm) font-medium text-foreground">
                      {name}
                      {tier ? <span className="ml-2 font-normal text-muted-foreground">rank {tier.rank}</span> : <span className="ml-2 font-normal text-warning">not in the quality model</span>}
                    </span>
                    <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Move ${name} up`} disabled={index === 0} onClick={() => moveTier(name, -1)}>
                      <ArrowUp className="h-3.5 w-3.5" />
                    </Button>
                    <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Move ${name} down`} disabled={index === allowedForDisplay.length - 1} onClick={() => moveTier(name, 1)}>
                      <ArrowDown className="h-3.5 w-3.5" />
                    </Button>
                    <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove ${name}`} onClick={() => removeTier(name)}>
                      <X className="h-3.5 w-3.5" />
                    </Button>
                  </li>
                );
              })}
            </ul>
          ) : null}
          {errors.allowed ? <p role="alert" className="text-(length:--type-caption) text-destructive">{errors.allowed}</p> : null}
          <FieldRow>
            <Field label="Add a tier" help="Most preferred at the top.">
              <Select value="" onChange={(event) => addTier(event.target.value)} placeholder={unusedTiers.length ? "Choose a tier" : "All tiers allowed"} options={unusedTiers.map((tier) => ({ value: tier.name, label: `${tier.name} · rank ${tier.rank}` }))} disabled={!unusedTiers.length} />
            </Field>
            <Field label="Stop upgrading at" error={errors.cutoff} help="Deluno stops replacing files once this tier is reached.">
              <Select value={form.cutoff} onChange={(event) => { setErrors((current) => ({ ...current, cutoff: undefined })); setForm((current) => ({ ...current, cutoff: event.target.value })); }} placeholder="Choose a tier" options={allowedForDisplay.map((name) => ({ value: name, label: name }))} disabled={!form.allowed.length} />
            </Field>
          </FieldRow>
          <SwitchRow label="Upgrade until cutoff" description="On: Deluno chases better copies until the tier you stop at, then leaves the file alone. Off: it takes what it finds and never looks again." checked={form.upgradeUntilCutoff} onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeUntilCutoff: checked }))} />

          {/*
            #938: rules plus TRaSH points, behind plain choices. The numbers
            these three controls stand for stay hidden unless "Show the
            numbers" is opened below - James's direction in #938 is that no
            one should need to know what a custom-format score is to use
            Deluno.
          */}
          <FieldRow>
            <Field label="Stop upgrading once a copy is" help="Once the installed file reaches this, Deluno stops looking for something better.">
              <Select
                value={form.cutoffChoice}
                onChange={(event) => setForm((current) => ({ ...current, cutoffChoice: event.target.value as QualityCutoffChoice, cutoffFormatScore: null }))}
                options={(Object.keys(CUTOFF_LABELS) as QualityCutoffChoice[]).map((choice) => ({ value: choice, label: CUTOFF_LABELS[choice] }))}
              />
            </Field>
            <Field label="Only replace a file when the new copy is" help="Below this, a same-quality replacement is not grabbed.">
              <Select
                value={form.upgradeGainChoice}
                onChange={(event) => setForm((current) => ({ ...current, upgradeGainChoice: event.target.value as QualityUpgradeGainChoice, minUpgradeFormatScore: null }))}
                options={(Object.keys(UPGRADE_GAIN_LABELS) as QualityUpgradeGainChoice[]).map((choice) => ({ value: choice, label: UPGRADE_GAIN_LABELS[choice] }))}
              />
            </Field>
          </FieldRow>
          <SwitchRow
            label="Never grab copies TRaSH rates as bad"
            description="Refuses a release outright when TRaSH's own guide calls it a bad copy, whatever else it offers."
            checked={form.neverGrabBad}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, neverGrabBad: checked, minFormatScore: null }))}
          />
          <Field label="Preferred language" help="Ranks a release in this language above one that names a different language. Does not refuse an untagged release.">
            <Select
              value={form.languageCodes[0] ?? ""}
              onChange={(event) => setForm((current) => ({ ...current, languageCodes: event.target.value ? [event.target.value] : [] }))}
              placeholder="No preference"
              options={Object.entries(LANGUAGE_LABELS).map(([code, label]) => ({ value: code, label }))}
            />
          </Field>
          <SwitchRow
            label="Require this language"
            description="Refuses a release explicitly tagged as a different language, rather than merely ranking it lower."
            checked={form.requireLanguage}
            disabled={form.languageCodes.length === 0}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, requireLanguage: checked }))}
          />
          <Disclosure
            title="Show the numbers"
            summary="TRaSH custom-format scores behind the choices above"
            open={showNumbersOpen}
            onOpenChange={setShowNumbersOpen}
          >
            <FieldRow>
              <Field label="Minimum score to grab" help="Blank uses the plain choice above.">
                <Input
                  type="number"
                  aria-label="Minimum custom-format score to grab"
                  value={form.minFormatScore ?? ""}
                  placeholder={String(GENERIC_MIN_FORMAT_SCORE)}
                  onChange={(event) => setForm((current) => ({ ...current, minFormatScore: event.target.value === "" ? null : Number(event.target.value) }))}
                />
              </Field>
              <Field label="Cutoff score to stop upgrading" help="Blank uses the plain choice above.">
                <Input
                  type="number"
                  aria-label="Cutoff custom-format score to stop upgrading"
                  value={form.cutoffFormatScore ?? ""}
                  placeholder={String(GENERIC_CUTOFF_FORMAT_SCORE)}
                  onChange={(event) => setForm((current) => ({ ...current, cutoffFormatScore: event.target.value === "" ? null : Number(event.target.value) }))}
                />
              </Field>
              <Field label="Minimum score gain to upgrade" help="Blank uses the plain choice above.">
                <Input
                  type="number"
                  aria-label="Minimum custom-format score gain required for an upgrade"
                  value={form.minUpgradeFormatScore ?? ""}
                  placeholder={String(GENERIC_MIN_UPGRADE_FORMAT_SCORE)}
                  onChange={(event) => setForm((current) => ({ ...current, minUpgradeFormatScore: event.target.value === "" ? null : Number(event.target.value) }))}
                />
              </Field>
            </FieldRow>
          </Disclosure>
              </div>
            )}
            renderSizeControls={() => (
              <ProfileSizeSteps
                mediaType={form.mediaType}
                allowed={form.allowed}
                rules={form.sizeRules}
                tiers={tiers}
                onChange={(sizeRules) => setForm((current) => ({ ...current, sizeRules }))}
              />
            )}
            renderStepExtras={(step: QualityStep) =>
              step.id === "groups" ? (
                <div className="grid gap-(--grid-gap)">
                  <Field
                    label="Only take releases naming"
                    help="Any one of these words must appear in the release name. Separate with commas. A pattern between slashes, like /NTb|FLUX/i, works too."
                  >
                    <Input
                      value={form.acquisition.mustContain}
                      placeholder="e.g. NTb, FLUX"
                      onChange={(event) => setAcquisition({ mustContain: event.target.value })}
                    />
                  </Field>
                  <Field
                    label="Prefer this way of fetching"
                    help="Which protocol wins a close call. Deluno still takes the other when it is clearly better."
                  >
                    <SegmentedControl<string>
                      value={form.acquisition.preferredProtocol}
                      onValueChange={(preferredProtocol) => setAcquisition({ preferredProtocol })}
                      options={[
                        { value: "any", label: "No preference" },
                        { value: "usenet", label: "Usenet" },
                        { value: "torrent", label: "Torrent" }
                      ]}
                    />
                  </Field>
                  <FieldRow>
                    <Field label="Wait before taking a usenet release" help="Minutes. Gives a bad release time to be replaced.">
                      <Input
                        type="number"
                        min={0}
                        value={form.acquisition.usenetDelayMinutes}
                        onChange={(event) => setAcquisition({ usenetDelayMinutes: Number(event.target.value) || 0 })}
                      />
                    </Field>
                    <Field label="Wait before taking a torrent" help="Minutes. Zero means take it as soon as it is found.">
                      <Input
                        type="number"
                        min={0}
                        value={form.acquisition.torrentDelayMinutes}
                        onChange={(event) => setAcquisition({ torrentDelayMinutes: Number(event.target.value) || 0 })}
                      />
                    </Field>
                  </FieldRow>
                </div>
              ) : step.id === "never" ? (
                <Field
                  label="Never take releases naming"
                  help="Any one of these words refuses the release outright, whatever else it offers. Separate with commas."
                >
                  <Input
                    value={form.acquisition.mustNotContain}
                    placeholder="e.g. HDTV, CAM"
                    onChange={(event) => setAcquisition({ mustNotContain: event.target.value })}
                  />
                </Field>
              ) : null
            }
            renderAdvanced={(step: QualityStep) =>
              step.id === "quality" ? (
                <Disclosure
                  title="Fine-tune"
                  summary="Unknown-quality handling"
                  open={fineTuneOpen}
                  onOpenChange={setFineTuneOpen}
                >
                  <SwitchRow
                    label="Upgrade files of unknown quality"
                    description="Replace files Deluno can't identify when a matching release appears."
                    checked={form.upgradeUnknownItems}
                    onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeUnknownItems: checked }))}
                  />
                  {/* "When to stop" is this step's own question, so its finer
                      half belongs here rather than on a page of its own.

                      There is deliberately no second switch for "stop once the
                      cutoff is met": that is the same answer as "upgrade until
                      cutoff" read from the other end, and asking it twice lets
                      somebody say two incompatible things. The engine keeps two
                      flags because they act at different stages - one decides
                      whether a file below the cutoff is chased, the other
                      whether a candidate is refused once it is met - but the
                      person answers once, above. */}
                  <SwitchRow
                    label="Only replace a same-quality file that is genuinely better"
                    description="Stops Deluno swapping a file for another of the same quality that offers nothing new."
                    checked={form.requireFormatGain}
                    disabled={!form.upgradeUntilCutoff}
                    onCheckedChange={(checked) => setForm((current) => ({ ...current, requireFormatGain: checked }))}
                  />
                </Disclosure>
              ) : null
            }
          />
        </DrawerSection>

        {editing ? (
          <DrawerSection
            title="Effective release preferences"
            aside={planLoading ? "Loading…" : planCompilation ? `${planCompilation.plan.families.length} typed families` : undefined}
          >
            {planLoading ? (
              <p className="text-(length:--type-caption) text-muted-foreground">Compiling the saved profile into the typed decision plan…</p>
            ) : planError ? (
              <p role="alert" className="text-(length:--type-caption) text-destructive">{planError}</p>
             ) : planCompilation ? (
               <div className="grid gap-3">
                 {editing.releasePreferencePlan ? (
                   <div className="rounded-[10px] border border-info/30 bg-info/5 px-3 py-2 text-(length:--type-caption)">
                     <div className="flex flex-wrap items-center justify-between gap-2">
                       <span className="font-medium text-foreground">Release preferences in use</span>
                       <Chip tone="info">{editing.releasePreferencePlan.version}</Chip>
                     </div>
                     <p className="mt-1 text-muted-foreground">
                       This profile is judged by the release preferences it was set up from. Changing its quality rules unlinks them until the profile is set up again.
                     </p>
                     <p className="mt-1 truncate font-mono text-(length:--type-micro) text-muted-foreground" title={editing.releasePreferencePlan.planHash}>
                       {editing.releasePreferencePlan.planId} · {editing.releasePreferencePlan.planHash}
                     </p>
                   </div>
                 ) : null}
                 {dirty ? (
                   <p className="rounded-[10px] border border-warning/30 px-3 py-2 text-(length:--type-caption) text-warning">
                     This preview reflects the last saved profile. Save the current edits to recompile it.
                  </p>
                ) : null}
                <div className="grid gap-2 sm:grid-cols-3">
                  <PlanSummary label="Hard gates" value={planCompilation.plan.requiredTraitIds?.length ?? 0} detail="required traits" />
                  <PlanSummary label="Forbidden" value={planCompilation.plan.forbiddenTraitIds?.length ?? 0} detail="blocked traits" />
                  <PlanSummary label="Review" value={planCompilation.advancedRules.length} detail={planCompilation.requiresReview ? "needs attention" : "no open review"} tone={planCompilation.requiresReview ? "warn" : "ok"} />
                </div>
                {planCompilation.plan.families.length ? (
                  <div className="grid gap-2" aria-label="Typed preference families">
                    {planCompilation.plan.families.map((family) => (
                      <div key={family.id} className="rounded-[10px] border border-hairline px-3 py-2">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <span className="text-(length:--type-body-sm) font-medium text-foreground">{humanizePreferenceFamily(family.dimension)}</span>
                          <span className="text-(length:--type-caption) text-muted-foreground">{preferenceIntentLabel(family.intent)}{family.upgradeDriving ? " · upgrade-driving" : " · tie-break"}</span>
                        </div>
                        <p className="mt-1 text-(length:--type-caption) text-muted-foreground">
                          {/*
                            A quality family holds every tier at or above the
                            cutoff so a file better than the profile's allowed
                            list can still be placed, which can be twenty-six
                            of them. Printing all of them turns one line into a
                            paragraph nobody reads, so the best few are shown
                            and the rest are counted.
                          */}
                          {describeFamilyLevels(family)}
                          {family.targetLevelId ? ` · stops at ${humanizeLevel(family, family.targetLevelId)}` : ""}
                        </p>
                      </div>
                    ))}
                  </div>
                ) : (
                  <p className="text-(length:--type-caption) text-muted-foreground">No typed preference families are active yet. Add formats or start from a guide template.</p>
                )}
                {planCompilation.advancedRules.length ? (
                  <Disclosure title="Advanced review" summary={`${planCompilation.advancedRules.length} compatibility rule${planCompilation.advancedRules.length === 1 ? "" : "s"} retained with provenance`} open={advancedPlanOpen} onOpenChange={setAdvancedPlanOpen}>
                    <div className="grid gap-2">
                      <p className="text-(length:--type-caption) text-muted-foreground">These rules remain available for compatibility and migration review. They are not the normal typed decision value.</p>
                      {planCompilation.advancedRules.map((rule) => (
                        <div key={rule.ruleId} className="rounded-[10px] border border-hairline px-3 py-2">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <span className="text-(length:--type-body-sm) font-medium text-foreground">{rule.name}</span>
                            <Chip tone={rule.requiresReview ? "warn" : "idle"}>{rule.requiresReview ? "Review" : "Mapped"}</Chip>
                          </div>
                          <p className="mt-1 text-(length:--type-caption) text-muted-foreground">{rule.explanation}</p>
                          {/*
                            The rule and the value it carried, not just its
                            name. "Retained with provenance" is only true if
                            you can read the provenance.
                          */}
                          <dl className="mt-2 grid gap-x-3 gap-y-1 text-(length:--type-caption) sm:grid-cols-[auto_minmax(0,1fr)]">
                            <dt className="text-muted-foreground">Legacy value</dt>
                            <dd className="font-mono text-foreground">{rule.originalScore}</dd>
                            <dt className="text-muted-foreground">Rule</dt>
                            <dd className="font-mono break-all text-foreground">{rule.trashId ?? rule.ruleId}</dd>
                            {rule.conditions ? (
                              <>
                                <dt className="text-muted-foreground">Matcher</dt>
                                <dd className="font-mono break-all text-foreground">{rule.conditions}</dd>
                              </>
                            ) : null}
                            <dt className="text-muted-foreground">Classification</dt>
                            <dd className="text-foreground">{legacyRuleKindLabel(rule.kind)}</dd>
                          </dl>
                        </div>
                      ))}
                    </div>
                  </Disclosure>
                ) : null}
                {planCompilation.plan.sources?.length ? (
                  <Disclosure
                    title="Where these came from"
                    summary={`${planCompilation.plan.sources.length} source${planCompilation.plan.sources.length === 1 ? "" : "s"} with the value each carried`}
                    open={planSourcesOpen}
                    onOpenChange={setPlanSourcesOpen}
                  >
                    <div className="grid gap-2" aria-label="Preference provenance">
                      <p className="text-(length:--type-caption) text-muted-foreground">Every typed preference above traces to one of these. The legacy value is what the rule carried before translation; it is not the value Deluno decides with.</p>
                      {planCompilation.plan.sources.map((source) => (
                        <div key={`${source.sourceKind}:${source.sourceId}:${source.layer ?? ""}`} className="rounded-[10px] border border-hairline px-3 py-2">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <span className="text-(length:--type-caption) break-all text-foreground">{source.ruleName ?? source.sourceId}</span>
                            <Chip tone="idle">{source.sourceKind}</Chip>
                          </div>
                          <dl className="mt-1 grid gap-x-3 gap-y-1 text-(length:--type-caption) sm:grid-cols-[auto_minmax(0,1fr)]">
                            {source.originalScore !== null ? (
                              <>
                                <dt className="text-muted-foreground">Legacy value</dt>
                                <dd className="font-mono text-foreground">
                                  {source.originalScore}
                                  {source.assignedScore !== null && source.assignedScore !== source.originalScore
                                    ? ` → ${source.assignedScore} in this profile`
                                    : ""}
                                </dd>
                              </>
                            ) : null}
                            {source.profileIntent ? (
                              <>
                                <dt className="text-muted-foreground">Intent</dt>
                                <dd className="text-foreground">{profileFormatIntentLabel(source.profileIntent)}</dd>
                              </>
                            ) : null}
                            {source.ruleId ? (
                              <>
                                <dt className="text-muted-foreground">Saved rule</dt>
                                <dd className="font-mono break-all text-foreground">{source.ruleId}</dd>
                              </>
                            ) : null}
                            {source.mappingId ? (
                              <>
                                <dt className="text-muted-foreground">Guide identity</dt>
                                <dd className="font-mono break-all text-foreground">{source.mappingId}</dd>
                              </>
                            ) : null}
                            {source.mappedTraitIds?.length ? (
                              <>
                                <dt className="text-muted-foreground">Became</dt>
                                <dd className="text-foreground">{source.mappedTraitIds.map(humanizeTrait).join(", ")}</dd>
                              </>
                            ) : null}
                            {source.matcherDefinition ? (
                              <>
                                <dt className="text-muted-foreground">Matcher</dt>
                                <dd className="font-mono break-all text-foreground">{source.matcherDefinition}{source.matcherAny ? " (any)" : ""}</dd>
                              </>
                            ) : null}
                            <dt className="text-muted-foreground">Mapping</dt>
                            <dd className="font-mono break-all text-foreground">{source.mappingVersion ?? "none"}</dd>
                            <dt className="text-muted-foreground">Source version</dt>
                            <dd className="font-mono break-all text-foreground">{source.sourceVersion}</dd>
                          </dl>
                        </div>
                      ))}
                    </div>
                  </Disclosure>
                ) : null}
                {planCompilation.warnings.length ? (
                  <ul className="grid gap-1 text-(length:--type-caption) text-warning" aria-label="Preference plan warnings">
                    {planCompilation.warnings.map((warning) => <li key={warning}>{warning}</li>)}
                  </ul>
                ) : null}
              </div>
            ) : (
              <p className="text-(length:--type-caption) text-muted-foreground">The typed preference plan is not available for this saved profile yet.</p>
            )}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection title="Used by" aside={usedBy.length ? `${usedBy.length} ${usedBy.length === 1 ? "library" : "libraries"}` : undefined}>
            {usedBy.length ? (
              <div className="grid gap-2">
                {usedBy.map((library) => (
                  <div key={library.id} className="flex min-h-10 items-center justify-between gap-(--grid-gap) rounded-[10px] border border-hairline px-(--field-pad-x) text-(length:--type-body-sm)">
                    <span className="truncate font-medium text-foreground">{library.name}</span>
                    <span className="truncate text-(length:--type-caption) text-muted-foreground">{library.defaultPolicySetName ? `via ${library.defaultPolicySetName}` : "direct assignment"}</span>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-(length:--type-caption) text-muted-foreground">Not assigned yet. A Library Profile normally chooses a Quality Profile for each library.</p>
            )}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection title="Make your own version">
            <div className="flex min-h-[52px] items-center justify-between gap-(--grid-gap) rounded-[10px] border border-hairline px-(--field-pad-x) py-2">
              <div className="min-w-0">
                <p className="text-(length:--type-body-sm) font-medium text-foreground">Duplicate this profile</p>
                <p className="mt-0.5 text-(length:--type-caption) text-muted-foreground">Opens a copy with the same tiers, sizes, rules and answers for you to change. This one stays as it is.</p>
              </div>
              <Button type="button" variant="outline" size="sm" onClick={() => openDuplicate(form)} disabled={busy || dirty} title={dirty ? "Save or discard your changes first." : undefined}>Duplicate</Button>
            </div>
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
          <DrawerDanger title="Delete this Quality Profile" description="Library Profiles using it need another Quality Profile." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog open={confirmRemove} onOpenChange={setConfirmRemove} title={`Delete “${editing?.name ?? form.name}”?`} description={usedBy.length ? `${usedBy.length} ${usedBy.length === 1 ? "library uses" : "libraries use"} this Quality Profile and will need another one.` : "This Quality Profile is not assigned to any Library Profile."} confirmLabel="Delete Quality Profile" busy={busy} onConfirm={() => void handleRemove()} />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this quality profile haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

/* --------------------------------------------------------------- utils */

/**
 * The answers a new profile opens with.
 *
 * <p>#386's second rule: <b>nothing ever starts empty</b>. Somebody who clicks
 * straight through the seven steps must end up with a profile that works, not
 * an inert one that accepts everything and prefers nothing — and an empty first
 * step cannot even be saved, so the old blank form's real message was "answer
 * this before I let you leave".</p>
 *
 * <p>The answer comes from the guide's own balanced profile rather than from a
 * constant here, because that is the same material the scenario picker used to
 * offer. A scenario was only ever a named set of answers to these questions, so
 * the sensible default and the scenario are the same thing — which is what lets
 * the picker go.</p>
 */
/**
 * The template a profile with no template of its own is measured against: the guide's balanced profile for the
 * media type, else its first. A custom profile gets this template's recommended formats as its baseline, for
 * movies and TV alike (#1139), instead of whichever formats happened to exist already.
 */
export function baselineStarterFor(starters: ProfileStarter[], mediaType: "movies" | "tv"): ProfileStarter | undefined {
  return starters.find((starter) => starter.mediaType === mediaType && /balanced/i.test(starter.label))
    ?? starters.find((starter) => starter.mediaType === mediaType);
}

function defaultForm(
  starters: ProfileStarter[],
  mediaType: "movies" | "tv" = "movies",
  guide?: GuidePackage,
  existingFormats: CustomFormatItem[] = []
): ProfileForm {
  const blank: ProfileForm = {
    name: "",
    mediaType,
    allowed: [],
    cutoff: "",
    customFormatIds: [],
    upgradeUntilCutoff: true,
    upgradeUnknownItems: false,
    sizeRules: [],
    requireFormatGain: true,
    formatIntents: {},
    acquisition: EMPTY_ACQUISITION,
    ...QUALITY_LIMITS_DEFAULTS
  };

  const balanced = baselineStarterFor(starters, mediaType);
  if (!balanced) return blank;

  // Steps 3 to 7 open on the guide's recommendations too, not just step 1.
  // Only the ones that already exist as formats here — the rest are created on
  // save by ensureRecommendedFormats, and a checklist cannot show an answer
  // pointing at a row that does not exist yet.
  const recommended = guide ? recommendedFormatsFor(balanced.id, guide) : [];
  const byTrashId = new Map(
    existingFormats
      .filter((format) => format.trashId && format.mediaType === mediaType)
      .map((format) => [format.trashId!, format.id])
  );
  const customFormatIds = recommended
    .map(({ trashId }) => byTrashId.get(trashId))
    .filter((id): id is string => Boolean(id));

  return { ...blank, allowed: balanced.allowed, cutoff: balanced.cutoff, customFormatIds };
}

function emptyForm(): ProfileForm {
  return { name: "", mediaType: "movies", allowed: [], cutoff: "", customFormatIds: [], upgradeUntilCutoff: true, upgradeUnknownItems: false, sizeRules: [], requireFormatGain: true, formatIntents: {}, acquisition: EMPTY_ACQUISITION, ...QUALITY_LIMITS_DEFAULTS };
}

/**
 * The numbers behind the three plain choices, for saving. A hand-edited
 * number under "Show the numbers" is sent as-is; otherwise the plain choice
 * is mapped using the generic TRaSH defaults, since nothing here is measured
 * against a specific guide profile once the profile is being edited directly.
 */
function scoreLimitsFromForm(form: ProfileForm): ProfileScoreLimits {
  const floor = form.minFormatScore ?? GENERIC_MIN_FORMAT_SCORE;
  const ceiling = form.cutoffFormatScore ?? GENERIC_CUTOFF_FORMAT_SCORE;
  return {
    minFormatScore: mapMinScore(form.neverGrabBad, floor),
    cutoffFormatScore: form.cutoffFormatScore ?? mapCutoffScore(form.cutoffChoice, floor, ceiling),
    minUpgradeFormatScore: form.minUpgradeFormatScore ?? mapUpgradeGainScore(form.upgradeGainChoice, GENERIC_MIN_UPGRADE_FORMAT_SCORE)
  };
}

/** The plain choice nearest a saved score, or the default when nothing was ever saved. */
function scoreLimitsToForm(limits: ProfileScoreLimits | null | undefined) {
  const minFormatScore = limits?.minFormatScore ?? null;
  const cutoffFormatScore = limits?.cutoffFormatScore ?? null;
  const minUpgradeFormatScore = limits?.minUpgradeFormatScore ?? null;
  const floor = minFormatScore ?? GENERIC_MIN_FORMAT_SCORE;
  return {
    neverGrabBad: minFormatScore !== null && minFormatScore > 0,
    cutoffChoice: cutoffFormatScore === null
      ? QUALITY_LIMITS_DEFAULTS.cutoffChoice
      : nearestCutoffChoice(cutoffFormatScore, floor, cutoffFormatScore),
    upgradeGainChoice: minUpgradeFormatScore === null
      ? QUALITY_LIMITS_DEFAULTS.upgradeGainChoice
      : nearestUpgradeGainChoice(minUpgradeFormatScore, GENERIC_MIN_UPGRADE_FORMAT_SCORE),
    minFormatScore,
    cutoffFormatScore,
    minUpgradeFormatScore
  };
}

function formFrom(profile: QualityProfileItem): ProfileForm {
  return {
    name: profile.name,
    mediaType: profile.mediaType === "tv" ? "tv" : "movies",
    allowed: splitCsv(profile.allowedQualities),
    cutoff: profile.cutoffQuality,
    customFormatIds: splitCsv(profile.customFormatIds),
    upgradeUntilCutoff: profile.upgradeUntilCutoff,
    upgradeUnknownItems: profile.upgradeUnknownItems,
    sizeRules: profile.sizeRules ?? [],
    requireFormatGain: profile.upgradeStop?.requireCustomFormatGainForSameQuality ?? true,
    formatIntents: { ...(profile.formatIntents ?? {}) },
    acquisition: { ...EMPTY_ACQUISITION, ...(profile.acquisition ?? {}) },
    ...scoreLimitsToForm(profile.scoreLimits),
    languageCodes: profile.languagePreference?.preferredLanguages ?? [],
    requireLanguage: profile.languagePreference?.requireLanguage ?? false
  };
}
function sameForm(a: ProfileForm, b: ProfileForm) {
  return a.name === b.name && a.mediaType === b.mediaType && a.cutoff === b.cutoff && a.upgradeUntilCutoff === b.upgradeUntilCutoff && a.upgradeUnknownItems === b.upgradeUnknownItems && a.allowed.join("|") === b.allowed.join("|") && sameIds(a.customFormatIds, b.customFormatIds) && sameSizeRules(a.sizeRules, b.sizeRules) && a.requireFormatGain === b.requireFormatGain && sameIntents(a.formatIntents, b.formatIntents)
    && JSON.stringify(a.acquisition) === JSON.stringify(b.acquisition)
    && a.neverGrabBad === b.neverGrabBad && a.cutoffChoice === b.cutoffChoice && a.upgradeGainChoice === b.upgradeGainChoice
    && a.requireLanguage === b.requireLanguage && a.languageCodes.join("|") === b.languageCodes.join("|")
    && a.minFormatScore === b.minFormatScore && a.cutoffFormatScore === b.cutoffFormatScore && a.minUpgradeFormatScore === b.minUpgradeFormatScore;
}

/** Changing how much you care has to count as a change, like any other answer. */
function sameIntents(a: Record<string, string>, b: Record<string, string>) {
  const key = (intents: Record<string, string>) =>
    Object.entries(intents)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([id, intent]) => `${id}:${intent}`)
      .join("|");
  return key(a) === key(b);
}

/** Dragging a handle has to count as a change, or the save footer stays asleep. */
function sameSizeRules(a: ProfileSizeRule[], b: ProfileSizeRule[]) {
  const key = (rules: ProfileSizeRule[]) =>
    [...rules]
      .sort((left, right) => left.quality.localeCompare(right.quality))
      .map((rule) => `${rule.quality}:${rule.minGb}:${rule.maxGb}:${rule.minMb}:${rule.maxMb}`)
      .join("|");
  return key(a) === key(b);
}
function sameIds(a: string[], b: string[]) {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((id) => set.has(id));
}
function splitCsv(value: string | null | undefined) {
  return (value ?? "").split(",").map((item) => item.trim()).filter(Boolean);
}

interface ProfileStarter {
  id: string;
  label: string;
  mediaType: "movies" | "tv";
  allowed: string[];
  cutoff: string;
  summary: string;
  /** TRaSH's own score guardrails and language answer for this guide profile (#778, #769). */
  minFormatScore: number;
  cutoffFormatScore: number;
  minUpgradeFormatScore: number;
  language: string | null;
}

/**
 * Converts the backend guide package's best-first tier order into the profile
 * editor's least-first storage order. The quality model remains authoritative
 * for the exact display name, so WEB-DL/WEBRip aliases cannot create a tier the
 * decision engine does not recognise.
 */
function guideStarters(guide: GuidePackage, tiers: { name: string; rank: number }[]): ProfileStarter[] {
  const byGuideId = new Map(guide.qualityTiers.map((tier) => [tier.id, tier.label]));
  const resolveModelName = (label: string) => {
    const normalise = (value: string) => value
      .toLowerCase()
      .replaceAll("web-dl", "web")
      .replaceAll("webrip", "web")
      .replaceAll("blu-ray", "bluray")
      .replaceAll("4k", "2160p")
      .replaceAll(/\s+/g, " ")
      .trim();
    return tiers.find((tier) => normalise(tier.name) === normalise(label))?.name ?? label;
  };

  return guide.qualityProfiles.map((profile) => {
    const qualityOrder = profile.qualityOrder
      .map((id) => byGuideId.get(id))
      .filter((label): label is string => Boolean(label))
      .map(resolveModelName);
    const cutoff = resolveModelName(byGuideId.get(profile.cutoffQualityId) ?? profile.cutoffQualityId);
    const allowedBestFirst = [...new Set([...qualityOrder, cutoff])];
    return {
      id: profile.id,
      label: profile.name,
      // The backend buckets a guide profile's "anime" media type with movies
      // (Radarr's own anime custom formats), not TV, so the starter's own
      // recommended formats — Deluno's bundled anime release-group tiers and
      // version tags among them — resolve against the right guide entries.
      mediaType: profile.mediaType === "tv" ? "tv" : "movies",
      allowed: allowedBestFirst.reverse(),
      cutoff,
      summary: profile.description,
      minFormatScore: profile.minFormatScore,
      cutoffFormatScore: profile.cutoffFormatScore,
      minUpgradeFormatScore: profile.minUpgradeFormatScore ?? GENERIC_MIN_UPGRADE_FORMAT_SCORE,
      language: profile.language ?? null
    };
  });
}

/**
 * TRaSH's own "language" answer for a guide profile is a display name
 * ("Original", "Any", "French", ...); a profile's preferred-language codes are
 * the same lowercase codes the typed preference system and this page's
 * language select already use. "Any" means no language opinion.
 */
function languageCodeFromGuideLanguage(language: string | null): string[] {
  if (!language || language.trim().toLowerCase() === "any") return [];
  const match = Object.entries(LANGUAGE_LABELS).find(
    ([, label]) => label.toLowerCase() === language.trim().toLowerCase()
  );
  if (match) return [match[0]];
  // "Original" is the guide's most common non-"Any" answer and maps to the
  // "original language" code directly rather than a specific ISO language.
  return language.trim().toLowerCase() === "original" ? ["original"] : [];
}

function recommendedFormatsFor(starterId: string, guide: GuidePackage) {
  return guide.qualityProfiles.find((preset) => preset.id === starterId)?.recommendedFormats ?? [];
}

/** Create any recommended TRaSH formats this starter needs, and return every id to attach. */
async function ensureRecommendedFormats(starterId: string, mediaType: "movies" | "tv", existing: CustomFormatItem[], guide: GuidePackage) {
  const recommended = recommendedFormatsFor(starterId, guide);
  if (!recommended.length) return [];
  // Only this media type's rules: a Deluno extra serves movies and TV under one
  // id, and a TV profile used to attach the movie rule it found first.
  const byTrashId = new Map(existing.filter((format) => format.trashId && format.mediaType === mediaType).map((format) => [format.trashId!, format]));
  const ids: { id: string; score: number }[] = [];

  for (const { trashId, score } of recommended) {
    const match = byTrashId.get(trashId);
    if (match) {
      ids.push({ id: match.id, score });
      continue;
    }
    const bundled = guide.customFormats.find((format) => format.trashId === trashId);
    if (!bundled || !guideFormatAppliesTo(bundled, mediaType)) continue;
    let created: CustomFormatItem;
    try {
      created = await createRuleFromGuide(bundled, mediaType, score);
    } catch (error) {
      throw new Error(`Could not create the “${friendlyRuleName(bundled.name)}” format: ${error instanceof Error ? error.message : "unknown error"}`);
    }
    ids.push({ id: created.id, score });
    byTrashId.set(trashId, created);
  }
  return ids;
}

function rankOf(tiers: { name: string; rank: number }[], name: string) {
  return tiers.find((tier) => tier.name === name)?.rank ?? 0;
}

function profileFormatIntentLabel(intent: string) {
  switch (intent) {
    case "blocked": return "Must not have";
    case "avoid": return "Avoid";
    case "neutral": return "Off";
    case "prefer": return "Prefer";
    case "strong-prefer": return "Strongly prefer";
    default: return intent;
  }
}

/**
 * The classification a legacy rule was given, in words.
 *
 * #351 asks that the owner can trace every new preference back to the exact
 * legacy rule and value that produced it. "unmappedAdvanced" is the API's
 * word for it, not a person's.
 */
const FAMILY_LEVELS_SHOWN = 6;

function describeFamilyLevels(family: { levels: Array<{ traitIds: string[] }> }) {
  if (!family.levels.length) return "No explicit levels";
  const named = family.levels.map((level) => level.traitIds.map(humanizeTrait).join(" or "));
  if (named.length <= FAMILY_LEVELS_SHOWN) return named.join(" → ");
  const remaining = named.length - FAMILY_LEVELS_SHOWN;
  return `${named.slice(0, FAMILY_LEVELS_SHOWN).join(" → ")} → ${remaining} more`;
}

function legacyRuleKindLabel(kind: string) {
  switch (kind) {
    case "exactTyped": return "Translated exactly";
    case "guideMapped": return "Mapped from the guide";
    case "orderedFamilyCandidate": return "Could become an ordered preference — needs your confirmation";
    case "hardGateCandidate": return "Looks like a must-have or must-not-have — needs your confirmation";
    case "tieBreakCandidate": return "Preference that should not drive upgrades — needs your confirmation";
    case "ambiguousOverlap": return "Overlaps another rule and may count twice";
    case "conflicting": return "Conflicts with another rule";
    case "unmappedAdvanced": return "Kept as-is; no safe typed translation yet";
    case "invalid": return "Cannot affect decisions — the rule or its reference is broken";
    default: return kind;
  }
}

function preferenceIntentLabel(intent: string) {
  return intent === "required" ? "Required" : intent === "forbidden" ? "Forbidden" : intent === "ranked" ? "Prefer" : intent === "tieBreak" ? "Tie-break" : "Neutral";
}

function humanizePreferenceFamily(value: string) {
  return value
    .split(/[._-]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

function humanizeTrait(value: string) {
  return humanizePreferenceFamily(value.split(".").slice(-1)[0] ?? value);
}

function humanizeLevel(family: ReleasePreferencePlanCompilation["plan"]["families"][number], levelId: string) {
  const level = family.levels.find((candidate) => candidate.id === levelId);
  return level?.traitIds.map(humanizeTrait).join(" or ") ?? levelId;
}

function PlanSummary({ label, value, detail, tone = "idle" }: { label: string; value: number; detail: string; tone?: "idle" | "warn" | "ok" }) {
  return (
    <div className="rounded-[10px] border border-hairline bg-surface-2 px-3 py-2">
      <p className="text-(length:--type-caption) text-muted-foreground">{label}</p>
      <p className={cn("mt-0.5 text-(length:--type-body-sm) font-semibold", tone === "warn" ? "text-warning" : tone === "ok" ? "text-success" : "text-foreground")}>
        {value} <span className="font-normal text-muted-foreground">{detail}</span>
      </p>
    </div>
  );
}
