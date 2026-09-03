/**
 * Library Profiles — reusable profiles attached to one or more libraries.
 *
 *   PageToolbar (tabs · New library profile)
 *   ListCard (rows: name · quality · releases · used by · status · on · ›)
 *   Drawer  (profile identity · quality target · release choices · exclusions · libraries)
 *
 * API contracts are unchanged: POST/PUT/DELETE /api/policy-sets and
 * PUT /api/libraries/{id}/media-plan.
 */
import React, { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useLocation, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { Plus } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { ListGroupHeader, MediaTypeFilter, useMediaTypeSplit } from "../components/ui/media-type-split";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { Textarea } from "../components/ui/textarea";
import { toast } from "../components/shell/toaster";
import { configurationNavAreas } from "../components/app/settings-shell";
import { friendlyRuleName } from "../lib/guide-names";
import {
  fetchJson,
  type CustomFormatItem,
  type DestinationRuleItem,
  type LibraryItem,
  type MediaPlanAutomationIntent,
  type MediaPlanPreview,
  type MediaPlanScenario,
  type MediaPlanScenarioCompilation,
  type MediaPlanVersionItem,
  type PlatformSettingsSnapshot,
  type PolicySetItem,
  type QualityProfileItem
} from "../lib/api";
import { fetchMediaPlanScenarioCompilation } from "../lib/api/scenarios";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";

const PLAN_TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

interface SettingsPolicySetsLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  destinationRules: DestinationRuleItem[];
  policySets: PolicySetItem[];
  mediaPlanScenarios: MediaPlanScenario[];
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
  automationIntent: MediaPlanAutomationIntent | null;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsPolicySetsLoader(): Promise<SettingsPolicySetsLoaderData> {
  const [overview, customFormats, destinationRules, policySets, mediaPlanScenarios] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchJson<DestinationRuleItem[]>("/api/destination-rules"),
    fetchJson<PolicySetItem[]>("/api/policy-sets"),
    fetchJson<MediaPlanScenario[]>("/api/media-plan-scenarios")
  ]);

  return {
    libraries: overview.libraries,
    qualityProfiles: overview.qualityProfiles,
    customFormats,
    destinationRules,
    policySets,
    mediaPlanScenarios,
    settings: overview.settings
  };
}

export function SettingsPolicySetsPage() {
  const { libraries, qualityProfiles, customFormats, destinationRules, policySets, mediaPlanScenarios } = useLoaderData() as SettingsPolicySetsLoaderData;
  const revalidator = useRevalidator();
  const location = useLocation();
  const navigate = useNavigate();
  const planHandoff = useMemo(() => {
    const params = new URLSearchParams(location.search);
    return {
      libraryId: params.get("libraryId"),
      returnTo: params.get("returnTo")
    };
  }, [location.search]);
  const planHandoffOpened = useRef<string | null>(null);

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
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [nameError, setNameError] = useState<string | null>(null);
  const [qualityProfileError, setQualityProfileError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);
  const [planVersions, setPlanVersions] = useState<MediaPlanVersionItem[]>([]);
  const [planVersionsBusy, setPlanVersionsBusy] = useState(false);
  const [historyReload, setHistoryReload] = useState(0);
  const [preview, setPreview] = useState<MediaPlanPreview | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [rollbackTarget, setRollbackTarget] = useState<MediaPlanVersionItem | null>(null);
  const [rollbackBusy, setRollbackBusy] = useState(false);
  const [scenarioId, setScenarioId] = useState<string | null>(null);
  const [scenarioCompilation, setScenarioCompilation] = useState<MediaPlanScenarioCompilation | null>(null);
  const [scenarioCompilationBusy, setScenarioCompilationBusy] = useState(false);

  const isOpen = mode.kind !== "closed";
  const editingPlan = mode.kind === "edit" ? policySets.find((plan) => plan.id === mode.id) ?? null : null;
  const editingPlanId = mode.kind === "edit" ? mode.id : null;

  const dirty = useMemo(
    () => isOpen && (!sameForm(form, initialForm) || !sameIds(targetLibraryIds, initialTargetIds)),
    [isOpen, form, initialForm, targetLibraryIds, initialTargetIds]
  );
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";

  useUnsavedChanges(dirty);

  useEffect(() => {
    if (!editingPlanId) {
      setPlanVersions([]);
      setPreview(null);
      return;
    }

    let cancelled = false;
    setPlanVersionsBusy(true);
    fetchJson<MediaPlanVersionItem[]>(`/api/policy-sets/${editingPlanId}/versions`)
      .then((items) => {
        if (!cancelled) setPlanVersions(items);
      })
      .catch(() => {
        if (!cancelled) setPlanVersions([]);
      })
      .finally(() => {
        if (!cancelled) setPlanVersionsBusy(false);
      });
    return () => {
      cancelled = true;
    };
  }, [editingPlanId, historyReload]);

  useEffect(() => {
    if (mode.kind !== "create" || !scenarioId) {
      setScenarioCompilation(null);
      setScenarioCompilationBusy(false);
      return;
    }

    let cancelled = false;
    setScenarioCompilationBusy(true);
    fetchMediaPlanScenarioCompilation(scenarioId, form.mediaType)
      .then((compilation) => {
        if (!cancelled) setScenarioCompilation(compilation);
      })
      .catch(() => {
        if (!cancelled) setScenarioCompilation(null);
      })
      .finally(() => {
        if (!cancelled) setScenarioCompilationBusy(false);
      });

    return () => {
      cancelled = true;
    };
  }, [form.mediaType, mode.kind, scenarioId]);

  // Any edit clears a stale saved/error status.
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  const availableDestinationRules = useMemo(() => destinationRules.filter((rule) => rule.mediaType === form.mediaType), [destinationRules, form.mediaType]);
  const availableCustomFormats = useMemo(() => customFormats.filter((format) => format.mediaType === form.mediaType), [customFormats, form.mediaType]);
  const matchingLibraries = useMemo(() => libraries.filter((library) => library.mediaType === form.mediaType), [libraries, form.mediaType]);

  function openCreate() {
    const next = emptyForm();
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    setTargetLibraryIds([]);
    setInitialTargetIds([]);
    setSaveState(undefined);
    setNameError(null);
      setQualityProfileError(null);
      setPreview(null);
    setScenarioId(null);
  }

  const openEdit = useCallback((plan: PolicySetItem) => {
    const next = formFromPlan(plan);
    const assigned = (librariesByPlan.get(plan.id) ?? []).map((library) => library.id);
    setMode({ kind: "edit", id: plan.id });
    setForm(next);
    setInitialForm(next);
    setTargetLibraryIds(assigned);
    setInitialTargetIds(assigned);
    setSaveState(undefined);
    setNameError(null);
    setQualityProfileError(null);
    setPreview(null);
    setScenarioId(null);
  }, [librariesByPlan]);

  useEffect(() => {
    if (mode.kind !== "closed" || !planHandoff.libraryId || planHandoffOpened.current === planHandoff.libraryId) return;
    const library = libraries.find((item) => item.id === planHandoff.libraryId);
    if (!library) return;
    planHandoffOpened.current = library.id;
    const assignedPlan = library.defaultPolicySetId ? policySets.find((plan) => plan.id === library.defaultPolicySetId) : null;
    if (assignedPlan) {
      openEdit(assignedPlan);
      return;
    }
    const next = emptyForm(library.mediaType === "tv" ? "tv" : "movies");
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    setTargetLibraryIds([library.id]);
    setInitialTargetIds([library.id]);
    setSaveState(undefined);
    setNameError(null);
    setQualityProfileError(null);
  }, [libraries, mode.kind, openEdit, planHandoff.libraryId, policySets]);

  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }

  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  function setMediaType(mediaType: "movies" | "tv") {
    if (mediaType === form.mediaType) return;
    setTargetLibraryIds([]);
    setQualityProfileError(null);
    setScenarioId(null);
    setForm((current) => ({ ...current, mediaType, qualityProfileId: "", customFormatIds: [], destinationRuleId: "", automationIntent: null }));
  }

  function applyScenario(id: string) {
    const scenario = mediaPlanScenarios.find((item) => item.id === id);
    const variant = scenario?.variants.find((item) => item.mediaType === form.mediaType);
    if (!scenario || !variant) {
      setScenarioId(null);
      return;
    }

    const qualityProfile = qualityProfiles.find((profile) =>
      profile.mediaType === form.mediaType && profile.presetId?.toLowerCase() === variant.qualityPresetId.toLowerCase());
    setScenarioId(id);
    setQualityProfileError(null);
    setForm((current) => ({
      ...current,
      name: current.name.trim() ? current.name : `${scenario.name} · ${form.mediaType === "tv" ? "TV" : "Movies"}`,
      qualityProfileId: qualityProfile?.id ?? "",
      customFormatIds: [],
      searchIntervalOverrideHours: variant.searchIntervalHours.toString(),
      retryDelayOverrideHours: variant.retryDelayHours.toString(),
      upgradeUntilCutoff: variant.upgradeUntilCutoff,
      automationIntent: {
        scenarioId: scenario.id,
        scenarioVersion: scenario.version,
        sizeTierId: variant.sizeTierId,
        sizeTierName: variant.sizeTierName,
        sizeDescription: variant.sizeDescription,
        subtitleIntent: variant.subtitleIntent,
        routingIntent: variant.routingIntent,
        sharingIntent: variant.sharingIntent,
        cleanupIntent: variant.cleanupIntent,
        notificationIntent: variant.notificationIntent,
        namingIntent: variant.namingIntent
      },
      notes: [
        `Scenario: ${scenario.id} v${scenario.version}`,
        `${scenario.name} · ${variant.summary}`,
        `Size tier: ${variant.sizeTierName} — ${variant.sizeDescription}`,
        `Routing: ${variant.routingIntent}`,
        `Subtitles: ${variant.subtitleIntent}`
      ].join("\n")
    }));
  }

  function selectReleasePreference(id: string) {
    if (!id) return;
    setForm((current) => ({ ...current, customFormatIds: current.customFormatIds.includes(id) ? current.customFormatIds : [...current.customFormatIds, id] }));
  }

  function removeReleasePreference(id: string) {
    setForm((current) => ({ ...current, customFormatIds: current.customFormatIds.filter((item) => item !== id) }));
  }

  function toggleTargetLibrary(id: string, on: boolean) {
    setTargetLibraryIds((current) => (on ? [...new Set([...current, id])] : current.filter((item) => item !== id)));
  }

  /* ---------------------------------------------------------- saving */
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    if (!form.name.trim()) {
      setNameError("Give this library profile a name.");
      return;
    }
    if (!form.qualityProfileId) {
      setQualityProfileError("Choose a Quality Profile first.");
      return;
    }
    setNameError(null);
    setQualityProfileError(null);
    setBusy(true);
    setSaveState("saving");

    const isEditing = mode.kind === "edit";
    const planId = mode.kind === "edit" ? mode.id : null;

    try {
      const allFormatIds = [...new Set(form.customFormatIds)];
      const response = await authedFetch(isEditing ? `/api/policy-sets/${planId}` : "/api/policy-sets", {
        method: isEditing ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(toPayload(form, form.qualityProfileId, allFormatIds))
      });
      if (!response.ok) {
        throw new Error(isEditing ? "Library profile could not be updated." : "Library profile could not be created.");
      }
      const saved = (await response.json()) as PolicySetItem;

      const toClear = initialTargetIds.filter((id) => !targetLibraryIds.includes(id));
      const toAssign = targetLibraryIds.filter((id) => !initialTargetIds.includes(id));
      const outcomes = await Promise.all([
        ...toClear.map((id) => assignPlan(id, null).then((ok) => ({ id, ok }))),
        ...toAssign.map((id) => assignPlan(id, saved.id).then((ok) => ({ id, ok })))
      ]);
      const failed = outcomes.filter((outcome) => !outcome.ok).map((outcome) => libraries.find((library) => library.id === outcome.id)?.name ?? outcome.id);

      const savedForm: PolicySetFormState = {
        ...form,
        qualityProfileId: saved.qualityProfileId ?? form.qualityProfileId,
        customFormatIds: allFormatIds
      };
      setForm(savedForm);
      setInitialForm(savedForm);
      setPreview(null);
      setHistoryReload((current) => current + 1);
      const settledTargets = targetLibraryIds.filter((id) => !failed.includes(libraries.find((library) => library.id === id)?.name ?? id));
      setTargetLibraryIds(settledTargets);
      setInitialTargetIds(settledTargets);
      if (mode.kind === "create") setMode({ kind: "edit", id: saved.id });

      // The footer status is the feedback here; toasts are reserved for
      // outcomes that happen away from the drawer.
      if (failed.length) {
        setSaveState("error");
        setSaveMessage(`Profile saved, but not attached to ${failed.join(", ")}`);
      } else {
        setSaveState("saved");
        setSaveMessage(isEditing ? "Saved just now" : "Library profile created");
      }
      if (!failed.length && !isEditing && planHandoff.returnTo === "library" && planHandoff.libraryId) {
        navigate(`/settings/libraries?libraryId=${encodeURIComponent(planHandoff.libraryId)}`, { replace: true });
        return;
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
      if (!response.ok && response.status !== 204) throw new Error("Library profile could not be removed.");
      toast.success("Library profile removed");
      setConfirmDelete(false);
      setInitialForm(form);
      setInitialTargetIds(targetLibraryIds);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Library profile could not be removed.");
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
        body: JSON.stringify(toPayload({ ...formFromPlan(plan), isEnabled }, plan.qualityProfileId, splitCsv(plan.customFormatIds)))
      });
      if (!response.ok) throw new Error(`Could not ${isEnabled ? "enable" : "pause"} ${plan.name}.`);
      if (mode.kind === "edit" && mode.id === plan.id && !dirty) {
        const next = { ...form, isEnabled };
        setForm(next);
        setInitialForm(next);
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Library profile could not be updated.");
    } finally {
      setTogglingId(null);
    }
  }

  async function handlePreview() {
    if (!editingPlanId || previewBusy) return;
    setPreviewBusy(true);
    try {
      const response = await authedFetch(`/api/policy-sets/${editingPlanId}/preview`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(toPayload(form, form.qualityProfileId, form.customFormatIds))
      });
      if (!response.ok) throw new Error("Could not preview these changes.");
      setPreview((await response.json()) as MediaPlanPreview);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not preview these changes.");
    } finally {
      setPreviewBusy(false);
    }
  }

  async function handleRollback() {
    if (!editingPlanId || !rollbackTarget || rollbackBusy) return;
    setRollbackBusy(true);
    try {
      const response = await authedFetch(`/api/policy-sets/${editingPlanId}/rollback`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ version: rollbackTarget.version })
      });
      if (!response.ok) {
        const problem = await response.json().catch(() => null) as { message?: string; title?: string; errors?: Record<string, string[]> } | null;
        const detail = problem?.message
          ?? problem?.title
          ?? Object.values(problem?.errors ?? {}).flat()[0]
          ?? "Could not restore that media-plan version.";
        throw new Error(response.status === 409 ? `Rollback needs review: ${detail}` : detail);
      }
      const restored = (await response.json()) as PolicySetItem;
      const next = formFromPlan(restored);
      setForm(next);
      setInitialForm(next);
      setPreview(null);
      setRollbackTarget(null);
      setHistoryReload((current) => current + 1);
      toast.success(`Restored media-plan version ${rollbackTarget.version}`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not restore that media-plan version.");
    } finally {
      setRollbackBusy(false);
    }
  }

  /* ---------------------------------------------------------- render */
  const usedByCount = (planId: string) => librariesByPlan.get(planId)?.length ?? 0;
  const drawerTitle = mode.kind === "create" ? "New library profile" : editingPlan?.name ?? (form.name || "Library Profiles");
  const drawerDescription =
    mode.kind === "create"
      ? "Create a reusable profile, then attach it to one or more libraries."
      : `Library Profile · ${form.mediaType === "tv" ? "TV" : "Movies"} · ${describeUsage(usedByCount(editingPlan?.id ?? ""))}`;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={PLAN_TABS}
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
            <PageToolbarAction onClick={openCreate}>New library profile</PageToolbarAction>
          </>
        }
      />

      <ListCard
        title="Library Profiles"
        count={`${policySets.length} ${policySets.length === 1 ? "profile" : "profiles"} · ${policySets.filter((plan) => plan.isEnabled).length} enabled · combines existing settings for each library`}
        filter={policySets.length > 3 ? { value: filter, onChange: setFilter, placeholder: "Filter library profiles" } : undefined}
      >
        {policySets.length === 0 ? (
          <ListEmpty
            title="No library profiles yet"
            description="A Library Profile combines an existing Quality Profile, Release Preferences and destination rule, then attaches them to one or more libraries."
            actions={
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus className="h-3.5 w-3.5" />
                New library profile
              </Button>
            }
          />
        ) : (
          <ListTable columns={LIBRARY_COLUMNS}>
            {split.visibleCount === 0 ? (
              <ListEmpty title="No profiles match" description={filter ? `Nothing matches “${filter}”.` : "No library profile for this media type yet."} />
            ) : (
              split.groups.flatMap((group) => [
                split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
                ...group.items.map((plan) => {
                const used = librariesByPlan.get(plan.id) ?? [];
                const rules = splitCsv(plan.customFormatIds);
                const profile = qualityProfiles.find((item) => item.id === plan.qualityProfileId);
                const wants = describeWhatItWants(profile, rules, customFormats);
                const tone = !plan.isEnabled ? "idle" : used.length ? "ok" : "idle";
                const statusLabel = !plan.isEnabled ? "Off" : used.length ? "Active" : "Unused";
                return (
                  <ListRow key={plan.id} onClick={() => openEdit(plan)} selected={mode.kind === "edit" && mode.id === plan.id}>
                    <ListNameCell
                      name={plan.name}
                      sub={[plan.mediaType === "tv" ? "TV" : "Movies", plan.notes?.trim() || null].filter(Boolean).join(" · ")}
                    />
                    <ListCell
                      primary={wants.headline}
                      secondary={wants.detail}
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
        className="sm:w-[min(48rem,100vw)]"
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={mode.kind === "create" ? "Create library profile" : "Save library profile"}
            onCancel={requestClose}
            saveEnabled={mode.kind === "create" ? true : undefined}
            disabled={busy}
          />
        }
      >
        <div className="grid gap-3 py-3">
        <DrawerSection title="Profile details" className="min-w-0 rounded-[12px] border border-primary/20 bg-primary/5 px-4 !py-4 border-b-0">
          <FieldRow>
            <Field label="Name" error={nameError}>
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
          {mode.kind === "create" ? (
            <Field label="Start from a scenario" optional help="A scenario fills the plan with readable defaults for quality, size, upgrades, search, subtitles, routing, and retention. Review every field before saving.">
              <Select
                value={scenarioId ?? ""}
                onChange={(event) => applyScenario(event.target.value)}
                placeholder="Build a custom plan"
                options={mediaPlanScenarios
                  .filter((scenario) => scenario.mediaTypes.includes(form.mediaType))
                  .map((scenario) => ({ value: scenario.id, label: scenario.name }))}
              />
            </Field>
          ) : null}
          {mode.kind === "create" && scenarioId ? (() => {
            const scenario = mediaPlanScenarios.find((item) => item.id === scenarioId);
            const variant = scenario?.variants.find((item) => item.mediaType === form.mediaType);
            return scenario && variant ? (
              <div className="rounded-[10px] border border-primary/20 bg-surface-2/60 px-3 py-2 text-[length:var(--type-caption)] text-muted-foreground">
                <p className="font-medium text-foreground">{scenario.name} · {variant.sizeTierName} size tier</p>
                <p className="mt-0.5">{variant.summary}</p>
                <p className="mt-0.5">{scenario.requirements.join(" ")}</p>
                {scenarioCompilationBusy ? <p className="mt-2">Checking which scenario behaviours are active…</p> : null}
                {scenarioCompilation?.behaviors?.length ? (
                  <div className="mt-3 grid gap-2 border-t border-primary/15 pt-3">
                    <p className="font-medium text-foreground">What this scenario will do</p>
                    {scenarioCompilation.behaviors.map((behavior) => (
                      <div key={behavior.id} className="grid gap-1 rounded-[8px] border border-hairline bg-surface px-2.5 py-2">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <span className="font-medium text-foreground">{behavior.area}</span>
                          <Chip tone={scenarioBehaviorTone(behavior.applicationStatus)}>{scenarioBehaviorLabel(behavior.applicationStatus)}</Chip>
                        </div>
                        <p>{behavior.intent}</p>
                        <p className="text-muted-foreground">{behavior.explanation}{behavior.configurationSurface ? ` Configure in ${behavior.configurationSurface}.` : ""}</p>
                      </div>
                    ))}
                  </div>
                ) : null}
              </div>
            ) : null;
          })() : null}
          {form.automationIntent ? (
            <div className="grid gap-2 rounded-[10px] border border-info/30 bg-info/5 px-3 py-2 text-[length:var(--type-caption)]">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-medium text-foreground">Captured automation intent</span>
                <Chip tone="info">
                  {form.automationIntent.scenarioId
                    ? `${form.automationIntent.scenarioId} · v${form.automationIntent.scenarioVersion ?? "?"}`
                    : "Typed plan detail"}
                </Chip>
              </div>
              <p className="text-muted-foreground">These scenario details are stored with the Media Plan so each recommendation remains visible and auditable. They only become executable where the owning Deluno setting is configured.</p>
              <div className="grid gap-1 sm:grid-cols-2">
                {[
                  ["Size", [form.automationIntent.sizeTierName, form.automationIntent.sizeDescription].filter(Boolean).join(" — ")],
                  ["Subtitles", form.automationIntent.subtitleIntent],
                  ["Routing", form.automationIntent.routingIntent],
                  ["Sharing", form.automationIntent.sharingIntent],
                  ["Cleanup", form.automationIntent.cleanupIntent],
                  ["Notifications", form.automationIntent.notificationIntent],
                  ["Naming", form.automationIntent.namingIntent]
                ].filter(([, value]) => Boolean(value)).map(([label, value]) => (
                  <div key={label} className="rounded-[8px] border border-info/15 bg-surface px-2.5 py-2">
                    <span className="font-medium text-foreground">{label}</span>
                    <span className="ml-1 text-muted-foreground">{value}</span>
                  </div>
                ))}
              </div>
            </div>
          ) : null}
          <SwitchRow
            label="Enabled"
            description="Libraries attached to this profile use these quality and release choices."
            checked={form.isEnabled}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))}
          />
          <Field label="Profile note" optional help="A private reminder about what this profile is for. It does not change searching or quality decisions.">
            <Textarea
              value={form.notes}
              onChange={(event) => setForm((current) => ({ ...current, notes: event.target.value }))}
              placeholder="For example: 4K movies for the lounge TV"
              rows={2}
            />
          </Field>
        </DrawerSection>

        <DrawerSection title="Settings to use" className="min-w-0 rounded-[12px] border border-success/20 bg-success/5 px-4 !py-4 border-b-0">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            Select the settings you created in the tabs above. This page only combines them into one profile for your libraries.
          </p>
          <Field label="Quality Profile" help="Choose the quality, cutoff and file-size behaviour this profile should use.">
            <Select
              value={form.qualityProfileId}
              onChange={(event) => {
                setQualityProfileError(null);
                setForm((current) => ({ ...current, qualityProfileId: event.target.value }));
              }}
              placeholder="Choose a Quality Profile"
              options={qualityProfiles.filter((profile) => profile.mediaType === form.mediaType).map((profile) => ({ value: profile.id, label: profile.name }))}
              aria-invalid={qualityProfileError ? "true" : undefined}
            />
          </Field>
          {qualityProfileError ? <p role="alert" className="text-[length:var(--type-caption)] text-destructive">{qualityProfileError}</p> : null}
          {form.qualityProfileId ? (
            <p className="-mt-1 text-[length:var(--type-caption)] text-muted-foreground">
              {qualityProfiles.find((profile) => profile.id === form.qualityProfileId)?.cutoffQuality
                ? `Stops at ${qualityProfiles.find((profile) => profile.id === form.qualityProfileId)?.cutoffQuality}. Size Rules are applied automatically.`
                : "This Quality Profile controls the accepted quality and cutoff."}
            </p>
          ) : null}
          <div className="rounded-[10px] border border-hairline bg-surface-2/60 px-3 py-2 text-[length:var(--type-caption)] text-muted-foreground">
            Size Rules are shared by Deluno and are applied through the selected Quality Profile. Edit them from the Size Rules tab.
          </div>
          <Field label="Release Preferences" optional help="Choose the release rules created under Release Preferences. You can select more than one.">
            {form.customFormatIds.length ? (
              <div role="list" aria-label="Selected release preferences" className="mb-2 grid gap-1.5">
                {form.customFormatIds.map((id) => {
                  const format = availableCustomFormats.find((item) => item.id === id);
                  if (!format) return null;
                  return (
                    <div key={id} role="listitem" className="flex items-center justify-between gap-2 rounded-[9px] border border-hairline bg-surface-2 px-2.5 py-2">
                      <span className="min-w-0 truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{format.name}</span>
                      <Button type="button" size="sm" variant="ghost" onClick={() => removeReleasePreference(id)}>Remove</Button>
                    </div>
                  );
                })}
              </div>
            ) : null}
            <Select
              value=""
              onChange={(event) => selectReleasePreference(event.target.value)}
              placeholder={availableCustomFormats.length ? "Choose a Release Preference" : "Create Release Preferences first"}
              disabled={!availableCustomFormats.length}
              options={availableCustomFormats.filter((format) => !form.customFormatIds.includes(format.id)).map((format) => ({ value: format.id, label: format.name }))}
            />
          </Field>
          <Field label="Final Destination" optional help="Leave this on the library folder unless you created a separate destination rule.">
            <Select
              value={form.destinationRuleId}
              onChange={(event) => setForm((current) => ({ ...current, destinationRuleId: event.target.value }))}
              placeholder="Use the library folder"
              options={availableDestinationRules.map((rule) => ({ value: rule.id, label: rule.name }))}
            />
          </Field>
        </DrawerSection>

        <DrawerSection title="Select libraries to use this profile" aside={targetLibraryIds.length ? describeUsage(targetLibraryIds.length) : "None selected"} className="min-w-0 rounded-[12px] border border-info/25 bg-info/5 px-4 !py-4 border-b-0">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            Choose the libraries that should use this profile. Their library rows will show the profile in Media Management.
          </p>
          {matchingLibraries.length ? (
            <div className="mt-3 grid gap-3">
              {matchingLibraries.map((library) => {
                const on = targetLibraryIds.includes(library.id);
                const otherPlan = !on && library.defaultPolicySetId && library.defaultPolicySetId !== editingPlan?.id
                  ? policySets.find((plan) => plan.id === library.defaultPolicySetId)?.name
                  : null;
                return (
                  <div key={library.id} className={cn("flex min-h-10 items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border px-[var(--field-pad-x)] py-2 transition-colors", on ? "border-info/35 bg-info/10" : "border-hairline bg-surface-2/30 hover:border-info/25")}>
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
            <p className="mt-3 text-[length:var(--type-caption)] text-muted-foreground">
              No {form.mediaType === "tv" ? "TV" : "movie"} libraries yet. Create one under Media Management first.
            </p>
          )}
        </DrawerSection>

        {mode.kind === "edit" ? (
          <DrawerSection title="Plan history" aside={planVersionsBusy ? "Loading…" : planVersions.length ? `${planVersions.length} immutable ${planVersions.length === 1 ? "version" : "versions"}` : "No history yet"} className="min-w-0 !py-4">
            <p className="text-[length:var(--type-caption)] text-muted-foreground">
              Every saved change is retained as a version. Rollback creates a new version, so the audit trail is never rewritten.
            </p>
            {planVersions.length ? (
              <div className="grid gap-1.5">
                {planVersions.slice(0, 8).map((version, index) => (
                  <div key={`${version.planId}-${version.version}`} className="flex items-center justify-between gap-3 rounded-[10px] border border-hairline bg-surface-2/30 px-3 py-2">
                    <div className="min-w-0">
                      <p className="truncate text-[length:var(--type-body-sm)] font-medium text-foreground">
                        Version {version.version} · {formatChangeKind(version.changeKind)}
                        {index === 0 ? <span className="ml-1.5 text-success">· current</span> : null}
                      </p>
                      <p className="truncate font-mono text-[length:var(--type-micro)] text-muted-foreground" title={version.planHash}>
                        {version.planHash.slice(0, 12)}
                      </p>
                    </div>
                    {index > 0 ? (
                      <Button type="button" size="sm" variant="outline" onClick={() => setRollbackTarget(version)} disabled={busy || rollbackBusy}>
                        Restore
                      </Button>
                    ) : null}
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">History starts when this profile is next saved.</p>
            )}
            {dirty ? (
              <div className="grid gap-2">
                <Button type="button" size="sm" variant="outline" onClick={() => void handlePreview()} disabled={previewBusy}>
                  {previewBusy ? "Previewing…" : "Preview unsaved changes"}
                </Button>
                {preview ? (
                  <div className="rounded-[10px] border border-info/30 bg-info/5 px-3 py-2">
                    <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">
                      {preview.hasChanges ? `${preview.changes.length} change${preview.changes.length === 1 ? "" : "s"} will create the next version.` : "No effective changes."}
                    </p>
                    {preview.changes.length ? (
                      <ul className="mt-1 grid gap-1 text-[length:var(--type-caption)] text-muted-foreground">
                        {preview.changes.map((change) => <li key={change.field}>{formatField(change.field)}: {change.currentValue ?? "none"} → {change.proposedValue ?? "none"}</li>)}
                      </ul>
                    ) : null}
                  </div>
                ) : null}
              </div>
            ) : null}
          </DrawerSection>
        ) : null}

        {mode.kind === "edit" ? (
          <DrawerSection className="min-w-0 !py-4 border-b-0">
            <DrawerDanger
              title="Delete this Library Profile"
              description="Libraries using it will need another Library Profile."
              action={
                <Button type="button" variant="destructive" size="sm" onClick={() => setConfirmDelete(true)} disabled={busy}>
                  Delete
                </Button>
              }
            />
          </DrawerSection>
        ) : null}
        </div>
      </Drawer>

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete “${editingPlan?.name ?? form.name}”?`}
        description={`${describeUsage(targetLibraryIds.length, "library uses", "libraries use")} this Library Profile. They will need another profile. This can't be undone.`}
        confirmLabel="Delete Library Profile"
        busy={busy}
        onConfirm={() => void handleDelete()}
      />

      <ConfirmDialog
        open={rollbackTarget !== null}
        onOpenChange={(open) => {
          if (!open && !rollbackBusy) setRollbackTarget(null);
        }}
        title={`Restore version ${rollbackTarget?.version ?? ""}?`}
        description="The selected snapshot will become the active profile. The restore itself is saved as a new version and the existing history is preserved."
        confirmLabel="Restore version"
        busy={rollbackBusy}
        onConfirm={() => void handleRollback()}
      />

      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this Library Profile haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

/* ---------------------------------------------------------------- utils */

function emptyForm(mediaType: "movies" | "tv" = "movies"): PolicySetFormState {
  return {
    name: "",
    mediaType,
    qualityProfileId: "",
    destinationRuleId: "",
    customFormatIds: [],
    searchIntervalOverrideHours: "",
    retryDelayOverrideHours: "",
    upgradeUntilCutoff: true,
    isEnabled: true,
    notes: "",
    automationIntent: null
  };
}

function formFromPlan(plan: PolicySetItem): PolicySetFormState {
  const mediaType = plan.mediaType === "tv" ? "tv" : "movies";
  return {
    ...emptyForm(mediaType),
    name: plan.name,
    mediaType,
    qualityProfileId: plan.qualityProfileId ?? "",
    destinationRuleId: plan.destinationRuleId ?? "",
    customFormatIds: splitCsv(plan.customFormatIds),
    searchIntervalOverrideHours: plan.searchIntervalOverrideHours?.toString() ?? "",
    retryDelayOverrideHours: plan.retryDelayOverrideHours?.toString() ?? "",
    upgradeUntilCutoff: plan.upgradeUntilCutoff,
    isEnabled: plan.isEnabled,
    notes: plan.notes ?? "",
    automationIntent: plan.automationIntent ?? null
  };
}

function toPayload(form: PolicySetFormState, qualityProfileId: string | null, customFormatIds: string[]) {
  return {
    name: form.name.trim(),
    mediaType: form.mediaType,
    qualityProfileId,
    destinationRuleId: form.destinationRuleId || null,
    customFormatIds: customFormatIds.join(", "),
    searchIntervalOverrideHours: form.searchIntervalOverrideHours ? Number(form.searchIntervalOverrideHours) : null,
    retryDelayOverrideHours: form.retryDelayOverrideHours ? Number(form.retryDelayOverrideHours) : null,
    upgradeUntilCutoff: form.upgradeUntilCutoff,
    isEnabled: form.isEnabled,
    notes: form.notes.trim() || null,
    automationIntent: form.automationIntent
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
    a.notes === b.notes &&
    JSON.stringify(a.automationIntent) === JSON.stringify(b.automationIntent)
  );
}

function sameIds(a: string[], b: string[]) {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((id) => set.has(id));
}

function splitCsv(value: string | null | undefined) {
  return (value ?? "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

/**
 * One sentence, not four columns of fragments.
 *
 * "E2E 4K Streaming Movies · Stops at WEB 2160p · 1 rule · E2E WEB-DL bonus ·
 * 1 library · Movies" was six facts you had to assemble yourself into the one
 * thing you opened this screen to find out.
 */
const LIBRARY_COLUMNS = [
  { label: "Library profile", width: "minmax(0,0.9fr)" },
  { label: "What it wants", width: "minmax(0,2fr)" },
  { label: "Used by" },
  { label: "Status", width: LIST_TRACK.status, mobile: true },
  { label: "On", width: LIST_TRACK.toggle, mobile: true }
];

/**
 * What this library wants, as a sentence a person can read.
 *
 * The row used to carry the raw parts — a profile name, "Stops at WEB 2160p",
 * "1 rule", the rule's own name — and leave you to assemble them. The parts
 * are all still true; they are just not the answer to the question somebody
 * opened this screen to ask.
 */
function describeWhatItWants(
  profile: QualityProfileItem | undefined,
  ruleIds: string[],
  customFormats: CustomFormatItem[]
): { headline: React.ReactNode; detail: string } {
  if (!profile) {
    return {
      headline: <span className="text-muted-foreground">Nothing yet</span>,
      detail: "Choose what this library should want."
    };
  }

  const tiers = splitCsv(profile.allowedQualities);
  const best = tiers[0] ?? profile.cutoffQuality;
  const headline = profile.upgradeUntilCutoff
    ? `Up to ${best}, and keeps looking until ${profile.cutoffQuality}`
    : `Up to ${best}, and keeps whatever it already has`;

  const selected = ruleIds
    .map((id) => customFormats.find((format) => format.id === id))
    .filter((format): format is CustomFormatItem => Boolean(format));
  // A rule at the floor is a refusal; anything above zero is a preference.
  // Zero is neither, so it says nothing here.
  const refuses = selected.filter((format) => format.score <= -10000).map((format) => friendlyRuleName(format.name));
  const prefers = selected.filter((format) => format.score > 0).map((format) => friendlyRuleName(format.name));

  const clauses: string[] = [];
  if (prefers.length) {
    clauses.push(prefers.length <= 2
      ? `Prefers ${prefers.join(" and ")}`
      : `Prefers ${prefers.slice(0, 2).join(", ")} and ${prefers.length - 2} more`);
  }
  if (refuses.length) {
    clauses.push(refuses.length === 1 ? `refuses ${refuses[0]}` : `refuses ${refuses.length} things`);
  }

  return {
    headline,
    detail: clauses.length ? `${clauses.join(", ")}.` : "No release preferences beyond the quality it accepts."
  };
}

function describeUsage(count: number, singular = "library", plural = "libraries") {
  return `${count} ${count === 1 ? singular : plural}`;
}

function formatChangeKind(value: string) {
  return value.replace(/[-_]/g, " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function formatField(value: string) {
  return value
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/^./, (letter) => letter.toUpperCase());
}

function scenarioBehaviorLabel(value: string) {
  return value === "applied"
    ? "Applied"
    : value === "requires-configuration"
      ? "Configure before relying on it"
      : "Informational";
}

function scenarioBehaviorTone(value: string): "ok" | "warn" | "idle" {
  return value === "applied" ? "ok" : value === "requires-configuration" ? "warn" : "idle";
}
