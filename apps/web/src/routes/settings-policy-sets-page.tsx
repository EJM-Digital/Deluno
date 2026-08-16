import { useMemo, useState, type FormEvent, type ReactNode } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { ArrowRight, CheckCircle2, ChevronDown, LoaderCircle } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { InputDescription } from "../components/ui/input-description";
import { PresetField } from "../components/ui/preset-field";
import { Badge } from "../components/ui/badge";
import { toast } from "../components/shell/toaster";
import {
  emptyPlatformSettingsSnapshot,
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
import { RouteSkeleton } from "../components/shell/skeleton";
import { MEDIA_PLAN_STARTERS, type MediaPlanStarter } from "../lib/media-plan-starters";
import { cn } from "../lib/utils";
import { DelunoNavGlyph, type DelunoNavGlyphKind } from "../components/shell/deluno-nav-glyph";

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
  mediaType: string;
  qualityProfileId: string;
  destinationRuleId: string;
  customFormatIds: string[];
  searchIntervalOverrideHours: string;
  retryDelayOverrideHours: string;
  upgradeUntilCutoff: boolean;
  isEnabled: boolean;
  notes: string;
}

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
  const loaderData = useLoaderData() as SettingsPolicySetsLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { libraries, qualityProfiles, customFormats, destinationRules, policySets } = loaderData;
  const revalidator = useRevalidator();
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formState, setFormState] = useState<PolicySetFormState>(createPolicySetForm);
  const [selectedStarterId, setSelectedStarterId] = useState<string | null>(null);
  const [showDetailedRules, setShowDetailedRules] = useState(false);
  const [targetLibraryIds, setTargetLibraryIds] = useState<string[]>([]);

  const availableProfiles = useMemo(
    () => qualityProfiles.filter((profile) => profile.mediaType === formState.mediaType),
    [qualityProfiles, formState.mediaType]
  );
  const availableDestinationRules = useMemo(
    () => destinationRules.filter((rule) => rule.mediaType === formState.mediaType),
    [destinationRules, formState.mediaType]
  );
  const availableCustomFormats = useMemo(
    () => customFormats.filter((format) => format.mediaType === formState.mediaType),
    [customFormats, formState.mediaType]
  );
  const matchingLibraries = useMemo(
    () => libraries.filter((library) => library.mediaType === formState.mediaType),
    [libraries, formState.mediaType]
  );
  const selectedLibraries = useMemo(
    () => libraries.filter((library) => targetLibraryIds.includes(library.id)),
    [libraries, targetLibraryIds]
  );
  const selectedStarter = MEDIA_PLAN_STARTERS.find((starter) => starter.id === selectedStarterId);
  const selectedQualityProfile = availableProfiles.find((profile) => profile.id === formState.qualityProfileId);
  const selectedDestinationRule = availableDestinationRules.find((rule) => rule.id === formState.destinationRuleId);
  const selectedCustomFormats = availableCustomFormats.filter((format) => formState.customFormatIds.includes(format.id));

  function startCreate() {
    setEditingId(null);
    setFormState(createPolicySetForm());
    setSelectedStarterId(null);
    setShowDetailedRules(false);
    setTargetLibraryIds([]);
  }

  function applyStarter(starter: MediaPlanStarter) {
    const matchingLibraries = libraries.filter((library) => library.mediaType === starter.values.mediaType);
    setEditingId(null);
    setFormState({
      ...createPolicySetForm(),
      ...starter.values
    });
    setSelectedStarterId(starter.id);
    setShowDetailedRules(false);
    setTargetLibraryIds(matchingLibraries.length === 1 && matchingLibraries[0] ? [matchingLibraries[0].id] : []);
  }

  function startEdit(policySet: PolicySetItem) {
    setEditingId(policySet.id);
    setSelectedStarterId(null);
    setShowDetailedRules(false);
    setTargetLibraryIds(libraries.filter((library) => library.defaultPolicySetId === policySet.id).map((library) => library.id));
    setFormState({
      name: policySet.name,
      mediaType: policySet.mediaType,
      qualityProfileId: policySet.qualityProfileId ?? "",
      destinationRuleId: policySet.destinationRuleId ?? "",
      customFormatIds: splitCsv(policySet.customFormatIds),
      searchIntervalOverrideHours: policySet.searchIntervalOverrideHours?.toString() ?? "",
      retryDelayOverrideHours: policySet.retryDelayOverrideHours?.toString() ?? "",
      upgradeUntilCutoff: policySet.upgradeUntilCutoff,
      isEnabled: policySet.isEnabled,
      notes: policySet.notes ?? ""
    });
  }

  function toggleCustomFormat(id: string) {
    setFormState((current) => ({
      ...current,
      customFormatIds: current.customFormatIds.includes(id)
        ? current.customFormatIds.filter((item) => item !== id)
        : [...current.customFormatIds, id]
    }));
  }

  function toggleTargetLibrary(id: string) {
    setTargetLibraryIds((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const isEditing = editingId !== null;
    const previouslyAssignedLibraryIds = isEditing
      ? libraries.filter((library) => library.defaultPolicySetId === editingId).map((library) => library.id)
      : [];
    setBusyKey(isEditing ? `save:${editingId}` : "create");

    try {
      const response = await authedFetch(isEditing ? `/api/policy-sets/${editingId}` : "/api/policy-sets", {
        method: isEditing ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          ...formState,
          qualityProfileId: formState.qualityProfileId || null,
          destinationRuleId: formState.destinationRuleId || null,
          customFormatIds: formState.customFormatIds.join(", "),
          searchIntervalOverrideHours: formState.searchIntervalOverrideHours ? Number(formState.searchIntervalOverrideHours) : null,
          retryDelayOverrideHours: formState.retryDelayOverrideHours ? Number(formState.retryDelayOverrideHours) : null
        })
      });

      if (!response.ok) {
        throw new Error(isEditing ? "Media plan could not be updated." : "Media plan could not be created.");
      }

      const savedPlan = await response.json() as PolicySetItem;
      const assignments = await Promise.all([
        ...previouslyAssignedLibraryIds
          .filter((libraryId) => !targetLibraryIds.includes(libraryId))
          .map(async (libraryId) => {
            const assignment = await authedFetch(`/api/libraries/${libraryId}/media-plan`, {
              method: "PUT",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ policySetId: null })
            });
            return assignment.ok;
          }),
        ...targetLibraryIds.map(async (libraryId) => {
          const assignment = await authedFetch(`/api/libraries/${libraryId}/media-plan`, {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ policySetId: savedPlan.id })
          });
          return assignment.ok;
        })
      ]);
      if (assignments.some((assigned) => !assigned)) {
        throw new Error("Media plan was saved, but could not be applied to every selected library.");
      }

      toast.success(isEditing ? "Media plan updated" : "Media plan created");
      startCreate();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Media plan action failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDelete(policySetId: string) {
    setBusyKey(`delete:${policySetId}`);
    try {
      const response = await authedFetch(`/api/policy-sets/${policySetId}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) {
        throw new Error("Media plan could not be removed.");
      }

      toast.success("Media plan removed");
      if (editingId === policySetId) {
        startCreate();
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Media plan could not be removed.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title="Media Plans"
      description="Create the plan Deluno follows for quality, size, releases, and upgrades."
    >
      <div className="space-y-3">
        <section className="overflow-hidden rounded-xl border border-hairline bg-card shadow-card dark:border-white/[0.07]">
          <header className="flex min-h-[3.25rem] flex-wrap items-center justify-between gap-3 border-b border-hairline bg-surface-2/45 px-4 py-2.5">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <h2 className="font-display text-[length:var(--type-card-title)] font-semibold text-foreground">
                  {editingId ? "Edit media plan" : "Create media plan"}
                </h2>
                <Badge variant={formState.isEnabled ? "success" : "default"}>{formState.isEnabled ? "Enabled" : "Paused"}</Badge>
                <Badge variant="info">{formState.mediaType === "tv" ? "TV" : "Movies"}</Badge>
              </div>
              <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                Pick a starter, set the normal path, then tune only the rules this plan needs.
              </p>
            </div>
            {editingId ? (
              <Button type="button" variant="outline" size="sm" onClick={startCreate}>
                New plan
              </Button>
            ) : null}
          </header>

          <div className="grid xl:grid-cols-[280px_minmax(0,1fr)_320px]">
            <aside className="border-b border-hairline bg-background/20 p-3 xl:border-b-0 xl:border-r">
              <div className="mb-3">
                <p className="text-[length:var(--type-body-sm)] font-semibold text-foreground">Plan starters</p>
                <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">Defaults are editable templates, not locked presets.</p>
              </div>
              <div className="space-y-2">
                <PlanStarterChoice
                  active={!editingId && selectedStarterId === null}
                  title="Blank custom plan"
                  description="Start empty when none of the defaults match."
                  onClick={startCreate}
                />
                {MEDIA_PLAN_STARTERS.map((starter) => (
                  <PlanStarterChoice
                    key={starter.id}
                    active={!editingId && selectedStarterId === starter.id}
                    title={starter.title}
                    description={starter.description}
                    onClick={() => applyStarter(starter)}
                  />
                ))}
              </div>
            </aside>

            <form onSubmit={handleSubmit} className="min-w-0">
              <div className="space-y-[var(--page-gap)] p-4">
                <FormSection title="Basics">
                  <div className="grid gap-3 md:grid-cols-2">
                    <Field label="Plan name">
                      <Input className="bg-surface-2" value={formState.name} onChange={(event) => setFormState((current) => ({ ...current, name: event.target.value }))} />
                    </Field>
                    <Field label="Media type">
                      <select
                        value={formState.mediaType}
                        onChange={(event) => {
                          setTargetLibraryIds([]);
                          setFormState((current) => ({
                            ...current,
                            mediaType: event.target.value,
                            qualityProfileId: "",
                            destinationRuleId: "",
                            customFormatIds: []
                          }));
                        }}
                        className="density-control-text h-[var(--control-height)] w-full rounded-lg border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      >
                        <option value="movies">Movies</option>
                        <option value="tv">TV</option>
                      </select>
                    </Field>
                    <Field label="Quality goal">
                      <select
                        value={formState.qualityProfileId}
                        onChange={(event) => setFormState((current) => ({ ...current, qualityProfileId: event.target.value }))}
                        className="density-control-text h-[var(--control-height)] w-full rounded-lg border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      >
                        <option value="">Choose later</option>
                        {availableProfiles.map((profile) => (
                          <option key={profile.id} value={profile.id}>
                            {profile.name}
                          </option>
                        ))}
                      </select>
                    </Field>
                    <Field label="Destination exception" description="Leave on library default unless this plan needs another final folder.">
                      <select
                        value={formState.destinationRuleId}
                        onChange={(event) => setFormState((current) => ({ ...current, destinationRuleId: event.target.value }))}
                        className="density-control-text h-[var(--control-height)] w-full rounded-lg border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      >
                        <option value="">Use library default folder</option>
                        {availableDestinationRules.map((rule) => (
                          <option key={rule.id} value={rule.id}>
                            {rule.name}
                          </option>
                        ))}
                      </select>
                    </Field>
                  </div>
                </FormSection>

                <FormSection title="Apply to libraries" description="For simple setups, choose your one library. Reuse a plan only where the same rules really fit.">
                  <div className="grid gap-2 sm:grid-cols-2">
                    {matchingLibraries.map((library) => {
                      const active = targetLibraryIds.includes(library.id);
                      return (
                        <label
                          key={library.id}
                          className={cn(
                            "group grid min-h-[4rem] cursor-pointer grid-cols-[auto_minmax(0,1fr)] items-start gap-3 rounded-lg border px-3 py-2.5 text-sm transition-colors",
                            active
                              ? "border-primary/45 bg-primary/10 text-foreground"
                              : "border-hairline bg-surface-2/60 text-foreground hover:border-primary/30 hover:bg-primary/[0.035]"
                          )}
                        >
                          <input className="mt-1" type="checkbox" checked={active} onChange={() => toggleTargetLibrary(library.id)} />
                          <span className="min-w-0">
                            <span className="block font-semibold">{library.name}</span>
                            <span className="mt-0.5 block truncate text-xs text-muted-foreground">{library.rootPath}</span>
                          </span>
                        </label>
                      );
                    })}
                    {matchingLibraries.length === 0 ? (
                      <EmptyPanel>Create a {formState.mediaType === "tv" ? "TV" : "Movies"} library first, or assign this plan later from Library folders.</EmptyPanel>
                    ) : null}
                  </div>
                </FormSection>

                <div className="rounded-lg border border-hairline bg-surface-1/70">
                  <button
                    type="button"
                    onClick={() => setShowDetailedRules((current) => !current)}
                    className="flex w-full items-center justify-between gap-3 px-4 py-3 text-left transition-colors hover:bg-muted/25"
                    aria-expanded={showDetailedRules}
                  >
                    <span>
                      <span className="block text-sm font-semibold text-foreground">Fine-tune rules</span>
                      <span className="mt-0.5 block text-sm text-muted-foreground">Search timing, release preferences, and notes.</span>
                    </span>
                    <ChevronDown className={`h-5 w-5 shrink-0 text-muted-foreground transition-transform ${showDetailedRules ? "rotate-180" : ""}`} />
                  </button>

                  {showDetailedRules ? (
                    <div className="space-y-[var(--page-gap)] border-t border-hairline p-4">
                      <div className="grid gap-3 md:grid-cols-2">
                        <Field label="Search schedule" description="How often Deluno should search for this plan instead of using the library default.">
                          <PresetField
                            inputType="number"
                            value={formState.searchIntervalOverrideHours}
                            onChange={(value) => setFormState((current) => ({ ...current, searchIntervalOverrideHours: value }))}
                            options={OVERRIDE_INTERVAL_OPTIONS}
                            customLabel="Custom interval"
                            customPlaceholder="Hours"
                          />
                        </Field>
                        <Field label="Try again after" description="How long Deluno should wait before retrying a failed search for this plan.">
                          <PresetField
                            inputType="number"
                            value={formState.retryDelayOverrideHours}
                            onChange={(value) => setFormState((current) => ({ ...current, retryDelayOverrideHours: value }))}
                            options={OVERRIDE_RETRY_OPTIONS}
                            customLabel="Custom retry delay"
                            customPlaceholder="Hours"
                          />
                        </Field>
                      </div>

                      <div>
                        <p className="text-sm font-semibold text-foreground">Release preferences</p>
                        <div className="mt-2 flex flex-wrap gap-2">
                          {availableCustomFormats.map((format) => {
                            const active = formState.customFormatIds.includes(format.id);
                            return (
                              <button
                                key={format.id}
                                type="button"
                                onClick={() => toggleCustomFormat(format.id)}
                                className={cn(
                                  "rounded-md border px-3 py-1.5 text-xs transition-colors",
                                  active
                                    ? "border-primary/40 bg-primary/10 text-primary"
                                    : "border-hairline bg-surface-2 text-muted-foreground hover:border-primary/30 hover:text-foreground"
                                )}
                              >
                                {format.name} - {format.score >= 0 ? `+${format.score}` : format.score}
                              </button>
                            );
                          })}
                          {availableCustomFormats.length === 0 ? (
                            <p className="text-sm text-muted-foreground">No release preferences available for this media type yet.</p>
                          ) : null}
                        </div>
                      </div>

                      <Field label="Notes">
                        <textarea
                          value={formState.notes}
                          onChange={(event) => setFormState((current) => ({ ...current, notes: event.target.value }))}
                          className="density-control-text min-h-24 w-full rounded-lg border border-hairline bg-surface-2 px-3 py-2 text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
                          placeholder="Kids 1080p, Anime Dual Audio, Premium 4K..."
                        />
                      </Field>
                    </div>
                  ) : null}
                </div>

                <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] md:items-center">
                  <ToggleField
                    label="Upgrade until cutoff"
                    checked={formState.upgradeUntilCutoff}
                    onChange={(checked) => setFormState((current) => ({ ...current, upgradeUntilCutoff: checked }))}
                  />
                  <ToggleField
                    label="Enabled"
                    checked={formState.isEnabled}
                    onChange={(checked) => setFormState((current) => ({ ...current, isEnabled: checked }))}
                  />
                  <div className="flex flex-wrap gap-2 md:justify-end">
                    <Button type="submit" disabled={busyKey === "create" || (editingId !== null && busyKey === `save:${editingId}`)}>
                      {busyKey === "create" || (editingId !== null && busyKey === `save:${editingId}`) ? (
                        <LoaderCircle className="h-4 w-4 animate-spin" />
                      ) : null}
                      {editingId ? "Save media plan" : "Create media plan"}
                    </Button>
                    {editingId ? (
                      <Button type="button" variant="outline" onClick={startCreate}>
                        Cancel
                      </Button>
                    ) : null}
                  </div>
                </div>
              </div>
            </form>

            <aside className="border-t border-hairline bg-sidebar/30 p-3 xl:border-l xl:border-t-0">
              <div className="rounded-lg border border-hairline bg-card/75">
                <div className="border-b border-hairline px-3 py-2.5">
                  <p className="text-sm font-semibold text-foreground">Plan summary</p>
                  <p className="mt-0.5 text-xs text-muted-foreground">What this plan will do when saved.</p>
                </div>
                <div className="divide-y divide-hairline">
                  <PlanSummaryRow label="Starter" value={selectedStarter?.title ?? (editingId ? "Saved plan" : "Blank custom plan")} />
                  <PlanSummaryRow label="Quality" value={selectedQualityProfile?.name ?? "Choose later"} />
                  <PlanSummaryRow label="Destination" value={selectedDestinationRule?.name ?? "Library default folder"} />
                  <PlanSummaryRow label="Libraries" value={selectedLibraries.length ? selectedLibraries.map((library) => library.name).join(", ") : "Not assigned yet"} />
                  <PlanSummaryRow label="Releases" value={selectedCustomFormats.length ? `${selectedCustomFormats.length} preference${selectedCustomFormats.length === 1 ? "" : "s"}` : "None selected"} />
                </div>
              </div>

              <div className="mt-3 rounded-lg border border-hairline bg-card/75">
                <div className="border-b border-hairline px-3 py-2.5">
                  <p className="text-sm font-semibold text-foreground">Supporting rules</p>
                  <p className="mt-0.5 text-xs text-muted-foreground">Open only when this plan needs more control.</p>
                </div>
                <div className="divide-y divide-hairline">
                  <PlanConfigLink icon="quality" title="Quality profile" to="/settings/profiles" />
                  <PlanConfigLink icon="size" title="Size rules" to="/settings/quality" />
                  <PlanConfigLink icon="scoring" title="Release preferences" to="/settings/custom-formats" />
                  <PlanConfigLink icon="destinations" title="Destination exceptions" to="/settings/destination-rules" />
                </div>
              </div>
            </aside>
          </div>
        </section>

        <section className="overflow-hidden rounded-xl border border-hairline bg-card shadow-card dark:border-white/[0.07]">
          <header className="flex min-h-[3.05rem] flex-wrap items-center justify-between gap-3 border-b border-hairline bg-surface-2/45 px-4 py-2.5">
            <div>
              <h2 className="text-[length:var(--type-body-sm)] font-semibold text-foreground">Saved media plans</h2>
              <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">Edit plans and see where each one is used.</p>
            </div>
            <Badge variant={policySets.length ? "info" : "default"}>{policySets.length} saved</Badge>
          </header>
          {policySets.length ? (
            <div className="divide-y divide-hairline">
              {policySets.map((policySet) => {
                const assignedLibraries = libraries.filter((library) => library.defaultPolicySetId === policySet.id);
                return (
                  <div
                    key={policySet.id}
                    className="grid gap-3 px-4 py-3 lg:grid-cols-[minmax(0,1.05fr)_minmax(0,0.9fr)_auto] lg:items-center"
                  >
                    <div className="min-w-0 space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="font-semibold text-foreground">{policySet.name}</p>
                        <Badge variant={policySet.isEnabled ? "success" : "default"}>
                          {policySet.isEnabled ? "Enabled" : "Paused"}
                        </Badge>
                        <Badge variant="info">{policySet.mediaType === "tv" ? "TV" : "Movies"}</Badge>
                      </div>
                      <p className="text-sm text-muted-foreground">
                        {policySet.qualityProfileName ?? "No quality goal"} - {policySet.destinationRuleName ?? "Library default"}
                      </p>
                    </div>

                    <div className="min-w-0">
                      <p className="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">Used by</p>
                      {assignedLibraries.length ? (
                        <div className="mt-2 flex flex-wrap gap-2">
                          {assignedLibraries.map((library) => (
                            <Badge key={library.id} variant="default">
                              {library.name}
                            </Badge>
                          ))}
                        </div>
                      ) : (
                        <p className="mt-1 text-sm text-muted-foreground">No libraries assigned.</p>
                      )}
                    </div>

                    <div className="flex gap-2 lg:justify-end">
                      <Button size="sm" variant="outline" onClick={() => startEdit(policySet)}>
                        Edit
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => void handleDelete(policySet.id)} disabled={busyKey === `delete:${policySet.id}`}>
                        {busyKey === `delete:${policySet.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                        Remove
                      </Button>
                    </div>
                  </div>
                );
              })}
            </div>
          ) : (
            <div className="p-4">
              <EmptyPanel>No media plans yet. Create one above; selected libraries will appear in the saved plan row.</EmptyPanel>
            </div>
          )}
        </section>
      </div>
    </SettingsShell>
  );
}

function createPolicySetForm(): PolicySetFormState {
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

function splitCsv(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function PlanStarterChoice({
  active,
  description,
  onClick,
  title
}: {
  active: boolean;
  description: string;
  onClick: () => void;
  title: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "group grid w-full grid-cols-[auto_minmax(0,1fr)] gap-3 rounded-lg border px-3 py-2.5 text-left transition-colors",
        active
          ? "border-primary/45 bg-primary/10 text-foreground"
          : "border-hairline bg-surface-1/70 text-foreground hover:border-primary/30 hover:bg-primary/[0.035]"
      )}
    >
      <span
        className={cn(
          "mt-0.5 flex h-5 w-5 items-center justify-center rounded-full border text-[10px] font-bold",
          active ? "border-primary/40 bg-primary text-primary-foreground" : "border-hairline bg-surface-2 text-muted-foreground"
        )}
      >
        {active ? <CheckCircle2 className="h-3.5 w-3.5" /> : null}
      </span>
      <span className="min-w-0">
        <span className="block truncate text-sm font-semibold">{title}</span>
        <span className="mt-0.5 line-clamp-2 block text-xs leading-relaxed text-muted-foreground">{description}</span>
      </span>
    </button>
  );
}

function PlanSummaryRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid gap-1 px-3 py-2.5">
      <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.16em] text-muted-foreground">{label}</p>
      <p className="text-sm font-semibold text-foreground">{value}</p>
    </div>
  );
}

function PlanConfigLink({ icon, title, to }: { icon: DelunoNavGlyphKind; title: string; to: string }) {
  return (
    <Link
      to={to}
      className="group flex min-h-10 items-center justify-between gap-3 px-3 py-2 text-sm font-medium text-foreground transition hover:bg-primary/5"
    >
      <span className="flex min-w-0 items-center gap-2">
        <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
          <DelunoNavGlyph kind={icon} className="h-4 w-4" />
        </span>
        <span className="truncate">{title}</span>
      </span>
      <ArrowRight className="h-4 w-4 text-muted-foreground transition group-hover:translate-x-0.5 group-hover:text-primary" />
    </Link>
  );
}

function FormSection({
  children,
  description,
  title
}: {
  children: ReactNode;
  description?: string;
  title: string;
}) {
  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <div>
          <p className="text-sm font-semibold text-foreground">{title}</p>
          {description ? <p className="mt-0.5 text-sm text-muted-foreground">{description}</p> : null}
        </div>
      </div>
      {children}
    </section>
  );
}

function EmptyPanel({ children }: { children: ReactNode }) {
  return (
    <div className="rounded-xl border border-dashed border-hairline bg-surface-1/55 p-3 text-sm leading-relaxed text-muted-foreground">
      {children}
    </div>
  );
}

function Field({ children, description, label }: { children: ReactNode; description?: string; label: string }) {
  return (
    <div className="grid min-w-0 gap-2">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      {children}
      {description && <InputDescription>{description}</InputDescription>}
    </div>
  );
}

function ToggleField({
  checked,
  label,
  onChange
}: {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="density-control-text flex min-h-10 items-center justify-between gap-3 rounded-xl border border-hairline bg-surface-1/70 px-3 py-2 text-foreground">
      <span>{label}</span>
      <input className="sr-only" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span className={cn("relative h-5 w-9 rounded-full transition-colors", checked ? "bg-primary" : "bg-muted")}>
        <span className={cn("absolute left-0.5 top-0.5 h-4 w-4 rounded-full bg-background shadow-sm transition-transform", checked ? "translate-x-4" : "translate-x-0")} />
      </span>
    </label>
  );
}
