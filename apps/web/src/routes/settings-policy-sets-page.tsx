/**
 * Media plans — reference implementation of the list → drawer grammar.
 *
 *   PageToolbar (tabs · New plan)
 *   ListCard (rows: name · quality · releases · used by · status · on · ›)
 *   Drawer  (Basics · Quality & size · Releases [Fine-tune] · Used by · Delete)
 *
 * API contracts are unchanged: POST/PUT/DELETE /api/policy-sets and
 * PUT /api/libraries/{id}/media-plan.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Plus } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { ListGroupHeader, MediaTypeFilter, useMediaTypeSplit } from "../components/ui/media-type-split";
import { PresetField } from "../components/ui/preset-field";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { Textarea } from "../components/ui/textarea";
import { toast } from "../components/shell/toaster";
import { configurationNavAreas } from "../components/app/settings-shell";
import {
  fetchJson,
  type CustomFormatItem,
  type DestinationRuleItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type PolicySetItem,
  type QualityProfileItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { MEDIA_PLAN_STARTERS } from "../lib/media-plan-starters";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";

const OVERRIDE_INTERVAL_OPTIONS = [
  { label: "Use library default", value: "" },
  { label: "Off / manual only", value: "0" },
  { label: "Every hour", value: "1" },
  { label: "Every 3 hours", value: "3" },
  { label: "Every 6 hours", value: "6" },
  { label: "Every 12 hours", value: "12" },
  { label: "Daily", value: "24" }
];

const OVERRIDE_RETRY_OPTIONS = [
  { label: "Use library default", value: "" },
  { label: "No delay", value: "0" },
  { label: "1 hour", value: "1" },
  { label: "3 hours", value: "3" },
  { label: "6 hours", value: "6" },
  { label: "12 hours", value: "12" },
  { label: "Daily", value: "24" }
];

const PLAN_TABS = configurationNavAreas.find((area) => area.label === "Media Plans")?.items ?? [];

interface SettingsPolicySetsLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  destinationRules: DestinationRuleItem[];
  policySets: PolicySetItem[];
  settings: PlatformSettingsSnapshot;
}

interface PolicySetFormState {
  name: string;
  mediaType: "movies" | "tv";
  qualityProfileId: string;
  destinationRuleId: string;
  customFormatIds: string[];
  searchIntervalOverrideHours: string;
  retryDelayOverrideHours: string;
  upgradeUntilCutoff: boolean;
  isEnabled: boolean;
  notes: string;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsPolicySetsLoader(): Promise<SettingsPolicySetsLoaderData> {
  const [overview, customFormats, destinationRules, policySets] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchJson<DestinationRuleItem[]>("/api/destination-rules"),
    fetchJson<PolicySetItem[]>("/api/policy-sets")
  ]);

  return {
    libraries: overview.libraries,
    qualityProfiles: overview.qualityProfiles,
    customFormats,
    destinationRules,
    policySets,
    settings: overview.settings
  };
}

export function SettingsPolicySetsPage() {
  const { libraries, qualityProfiles, customFormats, destinationRules, policySets } = useLoaderData() as SettingsPolicySetsLoaderData;
  const revalidator = useRevalidator();

  /* ------------------------------------------------------------ list */
  const [filter, setFilter] = useState("");
  const [togglingId, setTogglingId] = useState<string | null>(null);

  const visiblePlans = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    const sorted = [...policySets].sort((a, b) => a.name.localeCompare(b.name));
    if (!needle) return sorted;
    return sorted.filter((plan) =>
      [plan.name, plan.qualityProfileName ?? "", plan.destinationRuleName ?? "", plan.notes ?? ""].some((value) =>
        value.toLowerCase().includes(needle)
      )
    );
  }, [policySets, filter]);

  const split = useMediaTypeSplit(visiblePlans, (plan) => plan.mediaType);

  const librariesByPlan = useMemo(() => {
    const map = new Map<string, LibraryItem[]>();
    for (const library of libraries) {
      if (!library.defaultPolicySetId) continue;
      map.set(library.defaultPolicySetId, [...(map.get(library.defaultPolicySetId) ?? []), library]);
    }
    return map;
  }, [libraries]);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<PolicySetFormState>(emptyForm);
  const [initialForm, setInitialForm] = useState<PolicySetFormState>(emptyForm);
  const [targetLibraryIds, setTargetLibraryIds] = useState<string[]>([]);
  const [initialTargetIds, setInitialTargetIds] = useState<string[]>([]);
  const [starterId, setStarterId] = useState("");
  const [fineTuneOpen, setFineTuneOpen] = useState(false);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [nameError, setNameError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);

  const isOpen = mode.kind !== "closed";
  const editingPlan = mode.kind === "edit" ? policySets.find((plan) => plan.id === mode.id) ?? null : null;

  const dirty = useMemo(
    () => isOpen && (!sameForm(form, initialForm) || !sameIds(targetLibraryIds, initialTargetIds)),
    [isOpen, form, initialForm, targetLibraryIds, initialTargetIds]
  );
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";

  const blocker = useUnsavedChanges(dirty);

  // Any edit clears a stale saved/error status.
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  const availableProfiles = useMemo(() => qualityProfiles.filter((profile) => profile.mediaType === form.mediaType), [qualityProfiles, form.mediaType]);
  const availableDestinationRules = useMemo(() => destinationRules.filter((rule) => rule.mediaType === form.mediaType), [destinationRules, form.mediaType]);
  const availableCustomFormats = useMemo(() => customFormats.filter((format) => format.mediaType === form.mediaType), [customFormats, form.mediaType]);
  const matchingLibraries = useMemo(() => libraries.filter((library) => library.mediaType === form.mediaType), [libraries, form.mediaType]);
  const selectedProfile = availableProfiles.find((profile) => profile.id === form.qualityProfileId);

  function openCreate() {
    const next = emptyForm();
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    setTargetLibraryIds([]);
    setInitialTargetIds([]);
    setStarterId("");
    setFineTuneOpen(false);
    setSaveState(undefined);
    setNameError(null);
  }

  function openEdit(plan: PolicySetItem) {
    const next = formFromPlan(plan);
    const assigned = (librariesByPlan.get(plan.id) ?? []).map((library) => library.id);
    setMode({ kind: "edit", id: plan.id });
    setForm(next);
    setInitialForm(next);
    setTargetLibraryIds(assigned);
    setInitialTargetIds(assigned);
    setStarterId("");
    setFineTuneOpen(false);
    setSaveState(undefined);
    setNameError(null);
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
    const starter = MEDIA_PLAN_STARTERS.find((item) => item.id === id);
    if (!starter) {
      setForm(emptyForm());
      setTargetLibraryIds([]);
      return;
    }
    setForm({ ...emptyForm(), ...starter.values });
    const matches = libraries.filter((library) => library.mediaType === starter.values.mediaType);
    setTargetLibraryIds(matches.length === 1 && matches[0] ? [matches[0].id] : []);
  }

  function setMediaType(mediaType: "movies" | "tv") {
    if (mediaType === form.mediaType) return;
    setTargetLibraryIds([]);
    setForm((current) => ({ ...current, mediaType, qualityProfileId: "", destinationRuleId: "", customFormatIds: [] }));
  }

  function toggleCustomFormat(id: string) {
    setForm((current) => ({
      ...current,
      customFormatIds: current.customFormatIds.includes(id) ? current.customFormatIds.filter((item) => item !== id) : [...current.customFormatIds, id]
    }));
  }

  function toggleTargetLibrary(id: string, on: boolean) {
    setTargetLibraryIds((current) => (on ? [...new Set([...current, id])] : current.filter((item) => item !== id)));
  }

  /* ---------------------------------------------------------- saving */
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    if (!form.name.trim()) {
      setNameError("Give the plan a name.");
      return;
    }
    setNameError(null);
    setBusy(true);
    setSaveState("saving");

    const isEditing = mode.kind === "edit";
    const planId = mode.kind === "edit" ? mode.id : null;

    try {
      const response = await authedFetch(isEditing ? `/api/policy-sets/${planId}` : "/api/policy-sets", {
        method: isEditing ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(toPayload(form))
      });
      if (!response.ok) {
        throw new Error(isEditing ? "Media plan could not be updated." : "Media plan could not be created.");
      }
      const saved = (await response.json()) as PolicySetItem;

      const toClear = initialTargetIds.filter((id) => !targetLibraryIds.includes(id));
      const toAssign = targetLibraryIds.filter((id) => !initialTargetIds.includes(id));
      const outcomes = await Promise.all([
        ...toClear.map((id) => assignPlan(id, null).then((ok) => ({ id, ok }))),
        ...toAssign.map((id) => assignPlan(id, saved.id).then((ok) => ({ id, ok })))
      ]);
      const failed = outcomes.filter((outcome) => !outcome.ok).map((outcome) => libraries.find((library) => library.id === outcome.id)?.name ?? outcome.id);

      const savedForm = formFromPlan(saved);
      setForm(savedForm);
      setInitialForm(savedForm);
      const settledTargets = targetLibraryIds.filter((id) => !failed.includes(libraries.find((library) => library.id === id)?.name ?? id));
      setTargetLibraryIds(settledTargets);
      setInitialTargetIds(settledTargets);
      if (mode.kind === "create") setMode({ kind: "edit", id: saved.id });

      // The footer status is the feedback here; toasts are reserved for
      // outcomes that happen away from the drawer.
      if (failed.length) {
        setSaveState("error");
        setSaveMessage(`Plan saved, but not applied to ${failed.join(", ")}`);
      } else {
        setSaveState("saved");
        setSaveMessage(isEditing ? "Saved just now" : "Plan created");
      }
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/policy-sets/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Media plan could not be removed.");
      toast.success("Media plan removed");
      setConfirmDelete(false);
      setInitialForm(form);
      setInitialTargetIds(targetLibraryIds);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Media plan could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  async function toggleEnabled(plan: PolicySetItem, isEnabled: boolean) {
    setTogglingId(plan.id);
    try {
      const response = await authedFetch(`/api/policy-sets/${plan.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(toPayload({ ...formFromPlan(plan), isEnabled }))
      });
      if (!response.ok) throw new Error(`Could not ${isEnabled ? "enable" : "pause"} ${plan.name}.`);
      if (mode.kind === "edit" && mode.id === plan.id && !dirty) {
        const next = { ...form, isEnabled };
        setForm(next);
        setInitialForm(next);
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Media plan could not be updated.");
    } finally {
      setTogglingId(null);
    }
  }

  /* ---------------------------------------------------------- render */
  const usedByCount = (planId: string) => librariesByPlan.get(planId)?.length ?? 0;
  const drawerTitle = mode.kind === "create" ? "New media plan" : editingPlan?.name ?? (form.name || "Media plan");
  const drawerDescription =
    mode.kind === "create"
      ? "Pick a starter or begin blank, then save."
      : `Media plan · ${form.mediaType === "tv" ? "TV" : "Movies"} · ${describeUsage(usedByCount(editingPlan?.id ?? ""))}`;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={PLAN_TABS}
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
            <Button type="button" onClick={openCreate}>
              <Plus className="h-4 w-4" />
              New plan
            </Button>
          </>
        }
      />

      <ListCard
        title="Media plans"
        count={`${policySets.length} ${policySets.length === 1 ? "plan" : "plans"} · ${policySets.filter((plan) => plan.isEnabled).length} enabled`}
        filter={policySets.length > 3 ? { value: filter, onChange: setFilter, placeholder: "Filter plans" } : undefined}
      >
        {policySets.length === 0 ? (
          <ListEmpty
            title="No media plans yet"
            description="A plan is the single source of truth for quality, size, releases, upgrades and search timing. Each library follows one by default."
            actions={
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus className="h-3.5 w-3.5" />
                New plan
              </Button>
            }
          />
        ) : (
          <ListTable
            columns={[
              { label: "Name" },
              { label: "Quality" },
              { label: "Releases" },
              { label: "Used by" },
              { label: "Status", width: LIST_TRACK.status, mobile: true },
              { label: "On", width: LIST_TRACK.toggle, mobile: true }
            ]}
          >
            {split.visibleCount === 0 ? (
              <ListEmpty title="No plans match" description={filter ? `Nothing matches “${filter}”.` : "No plans for this media type yet."} />
            ) : (
              split.groups.flatMap((group) => [
                split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
                ...group.items.map((plan) => {
                const used = librariesByPlan.get(plan.id) ?? [];
                const rules = splitCsv(plan.customFormatIds);
                const profile = qualityProfiles.find((item) => item.id === plan.qualityProfileId);
                const tone = !plan.isEnabled ? "muted" : used.length ? "ok" : "muted";
                const statusLabel = !plan.isEnabled ? "Off" : used.length ? "Active" : "Unused";
                return (
                  <ListRow key={plan.id} onClick={() => openEdit(plan)} selected={mode.kind === "edit" && mode.id === plan.id}>
                    <ListNameCell
                      name={plan.name}
                      sub={[plan.mediaType === "tv" ? "TV" : "Movies", `upgrades ${plan.upgradeUntilCutoff ? "on" : "off"}`, plan.notes?.trim() || null].filter(Boolean).join(" · ")}
                    />
                    <ListCell
                      primary={plan.qualityProfileName ?? <span className="text-muted-foreground">Choose later</span>}
                      secondary={profile ? `Stops at ${profile.cutoffQuality}` : plan.destinationRuleName ? `Folder: ${plan.destinationRuleName}` : "Library default folder"}
                    />
                    <ListCell
                      primary={rules.length ? `${rules.length} ${rules.length === 1 ? "rule" : "rules"}` : <span className="text-muted-foreground">None</span>}
                      secondary={
                        rules.length
                          ? rules
                              .map((id) => customFormats.find((format) => format.id === id)?.name)
                              .filter(Boolean)
                              .slice(0, 3)
                              .join(", ")
                          : "Quality profile decides"
                      }
                    />
                    <ListCell
                      numeric
                      primary={used.length ? describeUsage(used.length) : <span className="text-muted-foreground">Not assigned</span>}
                      secondary={used.length ? used.map((library) => library.name).join(", ") : undefined}
                    />
                    <ListCell mobile>
                      <Chip tone={tone}>{statusLabel}</Chip>
                    </ListCell>
                    <ListCell mobile>
                      <Switch
                        size="sm"
                        aria-label={`${plan.isEnabled ? "Pause" : "Enable"} ${plan.name}`}
                        checked={plan.isEnabled}
                        disabled={togglingId === plan.id}
                        onCheckedChange={(checked) => void toggleEnabled(plan, checked)}
                      />
                    </ListCell>
                  </ListRow>
                );
              })
              ])
            )}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={isOpen}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={drawerTitle}
        description={drawerDescription}
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={mode.kind === "create" ? "Create plan" : "Save plan"}
            onCancel={requestClose}
            disabled={busy}
          />
        }
      >
        {mode.kind === "create" ? (
          <DrawerSection title="Start from">
            <Field label="Starter" help="Defaults are editable templates, not locked presets. Everything below can be changed.">
              <Select
                value={starterId}
                onChange={(event) => applyStarter(event.target.value)}
                options={[{ value: "", label: "Blank plan" }, ...MEDIA_PLAN_STARTERS.map((starter) => ({ value: starter.id, label: starter.title.replace(/^Default:\s*/, "") }))]}
              />
            </Field>
          </DrawerSection>
        ) : null}

        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Plan name" error={nameError}>
              <Input
                value={form.name}
                onChange={(event) => {
                  setNameError(null);
                  setForm((current) => ({ ...current, name: event.target.value }));
                }}
                placeholder="Everyday movies"
                autoComplete="off"
              />
            </Field>
            <Field label="Media type">
              <SegmentedControl<"movies" | "tv">
                value={form.mediaType}
                onValueChange={setMediaType}
                options={[
                  { value: "movies", label: "Movies" },
                  { value: "tv", label: "TV shows" }
                ]}
              />
            </Field>
          </FieldRow>
          <SwitchRow
            label="Enabled"
            description="Libraries using this plan follow it for new searches and upgrades."
            checked={form.isEnabled}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))}
          />
        </DrawerSection>

        <DrawerSection title="Quality & size">
          <FieldRow>
            <Field
              label="Quality profile"
              help={
                selectedProfile
                  ? `${selectedProfile.allowedQualities.split(",").map((value) => value.trim()).filter(Boolean).join(" → ")} · stops at ${selectedProfile.cutoffQuality}`
                  : availableProfiles.length
                    ? "Which release tiers are allowed and where upgrades stop."
                    : `No ${form.mediaType === "tv" ? "TV" : "movie"} quality profiles yet — create one under Quality profiles.`
              }
            >
              <Select
                value={form.qualityProfileId}
                onChange={(event) => setForm((current) => ({ ...current, qualityProfileId: event.target.value }))}
                placeholder="Choose later"
                options={availableProfiles.map((profile) => ({ value: profile.id, label: profile.name }))}
              />
            </Field>
            <Field label="Final folder" help="Only when this plan needs a different destination.">
              <Select
                value={form.destinationRuleId}
                onChange={(event) => setForm((current) => ({ ...current, destinationRuleId: event.target.value }))}
                placeholder="Library default"
                options={availableDestinationRules.map((rule) => ({ value: rule.id, label: rule.name }))}
              />
            </Field>
          </FieldRow>
          <SwitchRow
            label="Upgrade until cutoff"
            description="Keep replacing files until the profile's target tier is reached."
            checked={form.upgradeUntilCutoff}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeUntilCutoff: checked }))}
          />
        </DrawerSection>

        <DrawerSection
          title="Releases"
          aside={form.customFormatIds.length ? `${form.customFormatIds.length} ${form.customFormatIds.length === 1 ? "rule" : "rules"} selected` : undefined}
        >
          {availableCustomFormats.length ? (
            <div role="group" aria-label="Release preferences" className="flex flex-wrap gap-1.5">
              {availableCustomFormats.map((format) => {
                const active = form.customFormatIds.includes(format.id);
                return (
                  <button
                    key={format.id}
                    type="button"
                    aria-pressed={active}
                    onClick={() => toggleCustomFormat(format.id)}
                    className={cn(
                      "inline-flex h-7 items-center gap-1.5 rounded-full border px-2.5 text-[length:var(--type-caption)] font-medium transition-colors",
                      "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                      active
                        ? "border-primary/40 bg-primary/12 text-primary"
                        : "border-hairline bg-surface-2 text-muted-foreground hover:border-primary/30 hover:text-foreground"
                    )}
                  >
                    {format.name}
                    <span className={cn("tabular-nums", active ? "text-primary/80" : "text-muted-foreground/70")}>
                      {format.score >= 0 ? `+${format.score}` : format.score}
                    </span>
                  </button>
                );
              })}
            </div>
          ) : (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">
              No release preferences for {form.mediaType === "tv" ? "TV" : "movies"} yet — the quality profile decides on its own.
            </p>
          )}
          <Disclosure title="Fine-tune" summary="Search interval, retry delay, notes" open={fineTuneOpen} onOpenChange={setFineTuneOpen}>
            <FieldRow>
              <Field label="Search schedule" help="How often to search for this plan instead of the library default.">
                <PresetField
                  inputType="number"
                  value={form.searchIntervalOverrideHours}
                  onChange={(value) => setForm((current) => ({ ...current, searchIntervalOverrideHours: value }))}
                  options={OVERRIDE_INTERVAL_OPTIONS}
                  customLabel="Custom interval"
                  customPlaceholder="Hours"
                />
              </Field>
              <Field label="Try again after" help="How long to wait before retrying a failed search.">
                <PresetField
                  inputType="number"
                  value={form.retryDelayOverrideHours}
                  onChange={(value) => setForm((current) => ({ ...current, retryDelayOverrideHours: value }))}
                  options={OVERRIDE_RETRY_OPTIONS}
                  customLabel="Custom retry delay"
                  customPlaceholder="Hours"
                />
              </Field>
            </FieldRow>
            <Field label="Notes" optional>
              <Textarea
                value={form.notes}
                onChange={(event) => setForm((current) => ({ ...current, notes: event.target.value }))}
                placeholder="Why this plan exists, or what it's tuned for."
                rows={3}
              />
            </Field>
          </Disclosure>
        </DrawerSection>

        <DrawerSection title="Used by" aside={targetLibraryIds.length ? describeUsage(targetLibraryIds.length) : undefined}>
          {matchingLibraries.length ? (
            <div className="grid gap-2">
              {matchingLibraries.map((library) => {
                const on = targetLibraryIds.includes(library.id);
                const otherPlan = !on && library.defaultPolicySetId && library.defaultPolicySetId !== editingPlan?.id
                  ? policySets.find((plan) => plan.id === library.defaultPolicySetId)?.name
                  : null;
                return (
                  <div key={library.id} className="flex min-h-10 items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-hairline px-[var(--field-pad-x)]">
                    <label htmlFor={`use-${library.id}`} className="min-w-0 cursor-pointer">
                      <span className="block truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{library.name}</span>
                      <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">
                        {library.rootPath}
                        {otherPlan ? ` · currently uses ${otherPlan}` : ""}
                      </span>
                    </label>
                    <Switch id={`use-${library.id}`} size="sm" checked={on} onCheckedChange={(checked) => toggleTargetLibrary(library.id, checked)} />
                  </div>
                );
              })}
            </div>
          ) : (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">
              No {form.mediaType === "tv" ? "TV" : "movie"} libraries yet. Create one under Library setup, then assign this plan there or here.
            </p>
          )}
        </DrawerSection>

        {mode.kind === "edit" ? (
          <DrawerSection>
            <DrawerDanger
              title="Delete this plan"
              description="Libraries using it fall back to their direct quality profile."
              action={
                <Button type="button" variant="destructive" size="sm" onClick={() => setConfirmDelete(true)} disabled={busy}>
                  Delete
                </Button>
              }
            />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete “${editingPlan?.name ?? form.name}”?`}
        description={`${describeUsage(targetLibraryIds.length, "library uses", "libraries use")} this plan. They will fall back to their direct quality profile. This can't be undone.`}
        confirmLabel="Delete plan"
        busy={busy}
        onConfirm={() => void handleDelete()}
      />

      <ConfirmDialog
        open={confirmDiscard || blocker.state === "blocked"}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
          if (blocker.state === "blocked") blocker.reset();
        }}
        title="Discard unsaved changes?"
        description="Your edits to this plan haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          if (blocker.state === "blocked") {
            setMode({ kind: "closed" });
            blocker.proceed();
          } else {
            closeDrawer();
          }
        }}
      />
    </div>
  );
}

/* ---------------------------------------------------------------- utils */

function emptyForm(): PolicySetFormState {
  return {
    name: "",
    mediaType: "movies",
    qualityProfileId: "",
    destinationRuleId: "",
    customFormatIds: [],
    searchIntervalOverrideHours: "",
    retryDelayOverrideHours: "",
    upgradeUntilCutoff: true,
    isEnabled: true,
    notes: ""
  };
}

function formFromPlan(plan: PolicySetItem): PolicySetFormState {
  return {
    name: plan.name,
    mediaType: plan.mediaType === "tv" ? "tv" : "movies",
    qualityProfileId: plan.qualityProfileId ?? "",
    destinationRuleId: plan.destinationRuleId ?? "",
    customFormatIds: splitCsv(plan.customFormatIds),
    searchIntervalOverrideHours: plan.searchIntervalOverrideHours?.toString() ?? "",
    retryDelayOverrideHours: plan.retryDelayOverrideHours?.toString() ?? "",
    upgradeUntilCutoff: plan.upgradeUntilCutoff,
    isEnabled: plan.isEnabled,
    notes: plan.notes ?? ""
  };
}

function toPayload(form: PolicySetFormState) {
  return {
    ...form,
    name: form.name.trim(),
    qualityProfileId: form.qualityProfileId || null,
    destinationRuleId: form.destinationRuleId || null,
    customFormatIds: form.customFormatIds.join(", "),
    searchIntervalOverrideHours: form.searchIntervalOverrideHours ? Number(form.searchIntervalOverrideHours) : null,
    retryDelayOverrideHours: form.retryDelayOverrideHours ? Number(form.retryDelayOverrideHours) : null
  };
}

async function assignPlan(libraryId: string, policySetId: string | null) {
  const response = await authedFetch(`/api/libraries/${libraryId}/media-plan`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ policySetId })
  });
  return response.ok;
}

function sameForm(a: PolicySetFormState, b: PolicySetFormState) {
  return (
    a.name === b.name &&
    a.mediaType === b.mediaType &&
    a.qualityProfileId === b.qualityProfileId &&
    a.destinationRuleId === b.destinationRuleId &&
    sameIds(a.customFormatIds, b.customFormatIds) &&
    a.searchIntervalOverrideHours === b.searchIntervalOverrideHours &&
    a.retryDelayOverrideHours === b.retryDelayOverrideHours &&
    a.upgradeUntilCutoff === b.upgradeUntilCutoff &&
    a.isEnabled === b.isEnabled &&
    a.notes === b.notes
  );
}

function sameIds(a: string[], b: string[]) {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((id) => set.has(id));
}

function splitCsv(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function describeUsage(count: number, singular = "library", plural = "libraries") {
  return `${count} ${count === 1 ? singular : plural}`;
}
