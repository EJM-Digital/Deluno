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
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
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
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";

const PLAN_TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

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

  const isOpen = mode.kind !== "closed";
  const editingPlan = mode.kind === "edit" ? policySets.find((plan) => plan.id === mode.id) ?? null : null;

  const dirty = useMemo(
    () => isOpen && (!sameForm(form, initialForm) || !sameIds(targetLibraryIds, initialTargetIds)),
    [isOpen, form, initialForm, targetLibraryIds, initialTargetIds]
  );
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";

  useUnsavedChanges(dirty);

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
    setForm((current) => ({ ...current, mediaType, qualityProfileId: "", customFormatIds: [], destinationRuleId: "" }));
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
              <ListEmpty title="No profiles match" description={filter ? `Nothing matches “${filter}”.` : "No library profile for this media type yet."} />
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
                      sub={[plan.mediaType === "tv" ? "TV" : "Movies", plan.notes?.trim() || null].filter(Boolean).join(" · ")}
                    />
                    <ListCell
                      primary={plan.qualityProfileName ?? <span className="text-muted-foreground">Not chosen yet</span>}
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
    notes: ""
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
    notes: plan.notes ?? ""
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
    notes: form.notes.trim() || null
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

function splitCsv(value: string | null | undefined) {
  return (value ?? "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function describeUsage(count: number, singular = "library", plural = "libraries") {
  return `${count} ${count === 1 ? singular : plural}`;
}
