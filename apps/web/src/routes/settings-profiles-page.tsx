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
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
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
import { QualityBuildSteps } from "../components/app/quality-build-steps";
import { QUALITY_STEPS, type QualityStep } from "../lib/quality-steps";
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
  type ReleasePreferencePlanCompilation
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";

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
}

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

export function SettingsProfilesPage() {
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
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
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
    const next = defaultForm(profileStarters);
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
      cutoff: starter.cutoff
    }));
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
        || defaultForm(profileStarters, current.mediaType).allowed.join("|") === current.allowed.join("|");
      const next = untouched ? defaultForm(profileStarters, mediaType) : current;
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
      const starterFormatIds = mode.kind === "create" && addRecommended ? await ensureRecommendedFormats(starterId, form.mediaType, customFormats, guide) : [];
      const formatIds = [...new Set([...form.customFormatIds, ...starterFormatIds])];
      const payload = {
        name: form.name.trim(),
        mediaType: form.mediaType,
        cutoffQuality: form.cutoff || form.allowed[form.allowed.length - 1],
        allowedQualities: form.allowed.join(", "),
        customFormatIds: formatIds.join(", "),
        upgradeUntilCutoff: form.upgradeUntilCutoff,
        upgradeUnknownItems: form.upgradeUnknownItems
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
      setForm(settled);
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
  const recommendedCount = recommendedFormatsFor(starterId, guide).length;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={TABS}
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
      <PageToolbarAction onClick={openCreate}>New quality profile</PageToolbarAction>
          </>
        }
      />


      <ListCard title="Quality Profiles" count={qualityProfiles.length ? `${qualityProfiles.length} ${qualityProfiles.length === 1 ? "profile" : "profiles"} · complete quality decisions used by Library Profiles` : undefined}>
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
          <ListTable columns={[{ label: "Name" }, { label: "Allowed", width: "minmax(0,1.4fr)" }, { label: "Stops at" }, { label: "Used by" }, { label: "Formats", width: LIST_TRACK.status, mobile: true }]}>
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
            {starterId ? <p className="-mt-1 text-[length:var(--type-caption)] text-muted-foreground">{profileStarters.find((starter) => starter.id === starterId)?.summary}</p> : null}
            {recommendedCount ? (
              <SwitchRow
                label="Add the recommended formats"
                description={`${recommendedCount} TRaSH-guide preference rule${recommendedCount === 1 ? "" : "s"} — repack/proper preferences, codec and upscale handling. Created on save if they don't exist yet.`}
                checked={addRecommended}
                onCheckedChange={setAddRecommended}
              />
            ) : null}
          </DrawerSection>
        ) : null}

        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Profile name" error={errors.name}>
              <Input value={form.name} onChange={(event) => { setErrors((current) => ({ ...current, name: undefined })); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Movies / Standard" autoComplete="off" />
            </Field>
            <Field label="Media type">
              <SegmentedControl<"movies" | "tv"> value={form.mediaType} onValueChange={setMediaType} options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV shows" }]} />
            </Field>
          </FieldRow>
        </DrawerSection>

        <DrawerSection title="Build this profile">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
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
            onCustomFormatIdsChange={(customFormatIds) => setForm((current) => ({ ...current, customFormatIds }))}
            renderQualityControls={() => (
              <div className="grid gap-[var(--grid-gap)]">
          {allowedForDisplay.length ? (
            <ul className="grid gap-2">
              {allowedForDisplay.map((name, index) => {
                const tier = tiers.find((candidate) => candidate.name === name);
                return (
                  <li key={name} className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-[var(--field-pad-x)]">
                    <span className="w-5 shrink-0 text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{index + 1}</span>
                    <span className="min-w-0 flex-1 truncate text-[length:var(--type-body-sm)] font-medium text-foreground">
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
          {errors.allowed ? <p role="alert" className="text-[length:var(--type-caption)] text-destructive">{errors.allowed}</p> : null}
          <FieldRow>
            <Field label="Add a tier" help="Most preferred at the top.">
              <Select value="" onChange={(event) => addTier(event.target.value)} placeholder={unusedTiers.length ? "Choose a tier" : "All tiers allowed"} options={unusedTiers.map((tier) => ({ value: tier.name, label: `${tier.name} · rank ${tier.rank}` }))} disabled={!unusedTiers.length} />
            </Field>
            <Field label="Stop upgrading at" error={errors.cutoff} help="Deluno stops replacing files once this tier is reached.">
              <Select value={form.cutoff} onChange={(event) => { setErrors((current) => ({ ...current, cutoff: undefined })); setForm((current) => ({ ...current, cutoff: event.target.value })); }} placeholder="Choose a tier" options={allowedForDisplay.map((name) => ({ value: name, label: name }))} disabled={!form.allowed.length} />
            </Field>
          </FieldRow>
          <SwitchRow label="Upgrade until cutoff" description="Keep replacing files until the cutoff tier is reached." checked={form.upgradeUntilCutoff} onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeUntilCutoff: checked }))} />
              </div>
            )}
            renderSizeControls={() => <SizeRulesForAllowedTiers allowed={form.allowed} tiers={tiers} mediaType={form.mediaType} />}
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
              <p className="text-[length:var(--type-caption)] text-muted-foreground">Compiling the saved profile into the typed decision plan…</p>
            ) : planError ? (
              <p role="alert" className="text-[length:var(--type-caption)] text-destructive">{planError}</p>
             ) : planCompilation ? (
               <div className="grid gap-3">
                 {editing.releasePreferencePlan ? (
                   <div className="rounded-[10px] border border-info/30 bg-info/5 px-3 py-2 text-[length:var(--type-caption)]">
                     <div className="flex flex-wrap items-center justify-between gap-2">
                       <span className="font-medium text-foreground">Pinned immutable plan</span>
                       <Chip tone="info">{editing.releasePreferencePlan.version}</Chip>
                     </div>
                     <p className="mt-1 text-muted-foreground">
                       This profile uses the persisted typed plan it was migrated or compiled from. Editing the quality policy clears this reference until the profile is compiled again.
                     </p>
                     <p className="mt-1 truncate font-mono text-[length:var(--type-micro)] text-muted-foreground" title={editing.releasePreferencePlan.planHash}>
                       {editing.releasePreferencePlan.planId} · {editing.releasePreferencePlan.planHash}
                     </p>
                   </div>
                 ) : null}
                 {dirty ? (
                   <p className="rounded-[10px] border border-warning/30 bg-warning/8 px-3 py-2 text-[length:var(--type-caption)] text-warning">
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
                          <span className="text-[length:var(--type-body-sm)] font-medium text-foreground">{humanizePreferenceFamily(family.dimension)}</span>
                          <span className="text-[length:var(--type-caption)] text-muted-foreground">{preferenceIntentLabel(family.intent)}{family.upgradeDriving ? " · upgrade-driving" : " · tie-break"}</span>
                        </div>
                        <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">
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
                  <p className="text-[length:var(--type-caption)] text-muted-foreground">No typed preference families are active yet. Add formats or start from a guide template.</p>
                )}
                {planCompilation.advancedRules.length ? (
                  <Disclosure title="Advanced review" summary={`${planCompilation.advancedRules.length} compatibility rule${planCompilation.advancedRules.length === 1 ? "" : "s"} retained with provenance`} open={advancedPlanOpen} onOpenChange={setAdvancedPlanOpen}>
                    <div className="grid gap-2">
                      <p className="text-[length:var(--type-caption)] text-muted-foreground">These rules remain available for compatibility and migration review. They are not the normal typed decision value.</p>
                      {planCompilation.advancedRules.map((rule) => (
                        <div key={rule.ruleId} className="rounded-[10px] border border-hairline px-3 py-2">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <span className="text-[length:var(--type-body-sm)] font-medium text-foreground">{rule.name}</span>
                            <Chip tone={rule.requiresReview ? "warn" : "idle"}>{rule.requiresReview ? "Review" : "Mapped"}</Chip>
                          </div>
                          <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">{rule.explanation}</p>
                          {/*
                            The rule and the value it carried, not just its
                            name. "Retained with provenance" is only true if
                            you can read the provenance.
                          */}
                          <dl className="mt-2 grid gap-x-3 gap-y-1 text-[length:var(--type-caption)] sm:grid-cols-[auto_minmax(0,1fr)]">
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
                      <p className="text-[length:var(--type-caption)] text-muted-foreground">Every typed preference above traces to one of these. The legacy value is what the rule carried before translation; it is not the value Deluno decides with.</p>
                      {planCompilation.plan.sources.map((source) => (
                        <div key={`${source.sourceKind}:${source.sourceId}:${source.layer ?? ""}`} className="rounded-[10px] border border-hairline px-3 py-2">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <span className="font-mono text-[length:var(--type-caption)] break-all text-foreground">{source.sourceId}</span>
                            <Chip tone="idle">{source.sourceKind}</Chip>
                          </div>
                          <dl className="mt-1 grid gap-x-3 gap-y-1 text-[length:var(--type-caption)] sm:grid-cols-[auto_minmax(0,1fr)]">
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
                  <ul className="grid gap-1 text-[length:var(--type-caption)] text-warning" aria-label="Preference plan warnings">
                    {planCompilation.warnings.map((warning) => <li key={warning}>{warning}</li>)}
                  </ul>
                ) : null}
              </div>
            ) : (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">The typed preference plan is not available for this saved profile yet.</p>
            )}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection title="Used by" aside={usedBy.length ? `${usedBy.length} ${usedBy.length === 1 ? "library" : "libraries"}` : undefined}>
            {usedBy.length ? (
              <div className="grid gap-2">
                {usedBy.map((library) => (
                  <div key={library.id} className="flex min-h-10 items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-hairline px-[var(--field-pad-x)] text-[length:var(--type-body-sm)]">
                    <span className="truncate font-medium text-foreground">{library.name}</span>
                    <span className="truncate text-[length:var(--type-caption)] text-muted-foreground">{library.defaultPolicySetName ? `via ${library.defaultPolicySetName}` : "direct assignment"}</span>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">Not assigned yet. A Library Profile normally chooses a Quality Profile for each library.</p>
            )}
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
function defaultForm(starters: ProfileStarter[], mediaType: "movies" | "tv" = "movies"): ProfileForm {
  const blank: ProfileForm = {
    name: "",
    mediaType,
    allowed: [],
    cutoff: "",
    customFormatIds: [],
    upgradeUntilCutoff: true,
    upgradeUnknownItems: false
  };

  const balanced =
    starters.find((starter) => starter.mediaType === mediaType && /balanced/i.test(starter.label))
    ?? starters.find((starter) => starter.mediaType === mediaType);

  return balanced ? { ...blank, allowed: balanced.allowed, cutoff: balanced.cutoff } : blank;
}

function emptyForm(): ProfileForm {
  return { name: "", mediaType: "movies", allowed: [], cutoff: "", customFormatIds: [], upgradeUntilCutoff: true, upgradeUnknownItems: false };
}
function formFrom(profile: QualityProfileItem): ProfileForm {
  return {
    name: profile.name,
    mediaType: profile.mediaType === "tv" ? "tv" : "movies",
    allowed: splitCsv(profile.allowedQualities),
    cutoff: profile.cutoffQuality,
    customFormatIds: splitCsv(profile.customFormatIds),
    upgradeUntilCutoff: profile.upgradeUntilCutoff,
    upgradeUnknownItems: profile.upgradeUnknownItems
  };
}
function sameForm(a: ProfileForm, b: ProfileForm) {
  return a.name === b.name && a.mediaType === b.mediaType && a.cutoff === b.cutoff && a.upgradeUntilCutoff === b.upgradeUntilCutoff && a.upgradeUnknownItems === b.upgradeUnknownItems && a.allowed.join("|") === b.allowed.join("|") && sameIds(a.customFormatIds, b.customFormatIds);
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
      mediaType: profile.mediaType === "movies" ? "movies" : "tv",
      allowed: allowedBestFirst.reverse(),
      cutoff,
      summary: profile.description
    };
  });
}

function recommendedFormatsFor(starterId: string, guide: GuidePackage) {
  return guide.qualityProfiles.find((preset) => preset.id === starterId)?.recommendedFormats ?? [];
}

/** Create any recommended TRaSH formats this starter needs, and return every id to attach. */
async function ensureRecommendedFormats(starterId: string, mediaType: "movies" | "tv", existing: CustomFormatItem[], guide: GuidePackage) {
  const recommended = recommendedFormatsFor(starterId, guide);
  if (!recommended.length) return [];
  const byTrashId = new Map(existing.filter((format) => format.trashId).map((format) => [format.trashId!, format]));
  const ids: string[] = [];

  for (const { trashId, score } of recommended) {
    const match = byTrashId.get(trashId);
    if (match) {
      ids.push(match.id);
      continue;
    }
    const bundled = guide.customFormats.find((format) => format.trashId === trashId);
    if (!bundled) continue;
    const response = await authedFetch("/api/custom-formats", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: bundled.name,
        mediaType,
        score,
        trashId: bundled.trashId,
        conditions: bundled.patterns.map((pattern) => `regex: ${pattern}`).join("\n"),
        upgradeAllowed: true
      })
    });
    if (!response.ok) throw new Error(`Could not create the “${bundled.name}” format.`);
    const created = (await response.json()) as CustomFormatItem;
    ids.push(created.id);
    byTrashId.set(trashId, created);
  }
  return ids;
}

function rankOf(tiers: { name: string; rank: number }[], name: string) {
  return tiers.find((tier) => tier.name === name)?.rank ?? 0;
}

function preferenceIntent(score: number) {
  if (score <= -10000) return "Must not have";
  if (score < 0) return "Avoid";
  if (score === 0) return "I do not care";
  if (score >= 500) return "Strongly prefer";
  return "Prefer";
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
      <p className="text-[length:var(--type-caption)] text-muted-foreground">{label}</p>
      <p className={cn("mt-0.5 text-[length:var(--type-body-sm)] font-semibold", tone === "warn" ? "text-warning" : tone === "ok" ? "text-success" : "text-foreground")}>
        {value} <span className="font-normal text-muted-foreground">{detail}</span>
      </p>
    </div>
  );
}

/**
 * Step 2's controls: the size range Deluno treats as sensible for each tier
 * this profile allows.
 *
 * <p><b>Shared, and it says so.</b> Sizes live on the quality model rather than
 * on a profile, so a change here reaches every profile using that tier. The
 * alternative to saying that plainly was the thing #386 exists to remove —
 * sending somebody to a Size Rules tab and hoping they work out the connection.</p>
 */
function SizeRulesForAllowedTiers({
  allowed,
  tiers,
  mediaType
}: {
  allowed: string[];
  tiers: QualityTierDefinition[];
  mediaType: "movies" | "tv";
}) {
  const rows = allowed
    .map((name) => tiers.find((tier) => tier.name === name))
    .filter((tier): tier is QualityTierDefinition => Boolean(tier))
    .sort((a, b) => b.rank - a.rank);

  if (rows.length === 0) {
    return (
      <p className="text-[length:var(--type-caption)] text-muted-foreground">
        Answer the first question and the sizes for those tiers appear here.
      </p>
    );
  }

  return (
    <div className="grid gap-2">
      <ul className="grid gap-1.5">
        {rows.map((tier) => (
          <li
            key={tier.name}
            className="flex min-h-9 items-center justify-between gap-2 rounded-[10px] border border-hairline px-[var(--field-pad-x)] text-[length:var(--type-body-sm)]"
          >
            <span className="min-w-0 truncate font-medium">{tier.name}</span>
            <span className="shrink-0 tabular-nums text-muted-foreground">
              {mediaType === "tv"
                ? `${tier.episodeMinMb}–${tier.episodeMaxMb} MB per episode`
                : `${tier.movieMinGb}–${tier.movieMaxGb} GB per film`}
            </span>
          </li>
        ))}
      </ul>
      <p className="text-[length:var(--type-caption)] text-muted-foreground">
        These sizes belong to the tier, not to this profile, so every profile allowing{" "}
        {rows.length === 1 ? rows[0].name : "these tiers"} uses them. Change them under{" "}
        <Link to="/settings/quality" className="underline underline-offset-2">
          Size Rules
        </Link>
        .
      </p>
    </div>
  );
}
