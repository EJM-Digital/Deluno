import { useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { ChevronDown, LoaderCircle, Route, ShieldCheck, SlidersHorizontal, Sparkles, Wand2 } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { KpiCard } from "../components/app/kpi-card";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
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

const MEDIA_PLAN_STARTERS: Array<{
  id: string;
  title: string;
  description: string;
  values: Pick<PolicySetFormState, "name" | "mediaType" | "searchIntervalOverrideHours" | "retryDelayOverrideHours" | "upgradeUntilCutoff" | "notes">;
}> = [
  {
    id: "family-movies",
    title: "Family movies",
    description: "Balanced quality, sensible upgrades, and a gentle search schedule.",
    values: {
      name: "Family Movies 1080p",
      mediaType: "movies",
      searchIntervalOverrideHours: "12",
      retryDelayOverrideHours: "6",
      upgradeUntilCutoff: true,
      notes: "A dependable 1080p movie experience for the whole household."
    }
  },
  {
    id: "premium-4k",
    title: "Premium 4K",
    description: "A quality-first plan for a home-theatre movie collection.",
    values: {
      name: "Premium 4K Movies",
      mediaType: "movies",
      searchIntervalOverrideHours: "12",
      retryDelayOverrideHours: "6",
      upgradeUntilCutoff: true,
      notes: "A 4K and HDR-focused movie plan. Choose the matching quality goal and release preferences below."
    }
  },
  {
    id: "everyday-tv",
    title: "Everyday TV",
    description: "Keep monitored shows current without overwhelming your sources.",
    values: {
      name: "Everyday TV 1080p",
      mediaType: "tv",
      searchIntervalOverrideHours: "6",
      retryDelayOverrideHours: "3",
      upgradeUntilCutoff: true,
      notes: "An everyday TV plan with steady missing-episode and upgrade searches."
    }
  },
  {
    id: "anime",
    title: "Anime",
    description: "A starting point for anime-specific language, group, and format preferences.",
    values: {
      name: "Anime",
      mediaType: "tv",
      searchIntervalOverrideHours: "6",
      retryDelayOverrideHours: "3",
      upgradeUntilCutoff: true,
      notes: "Choose anime release preferences below, then fine-tune language and quality for this library."
    }
  }
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

  const enabledSets = policySets.filter((set) => set.isEnabled).length;
  const linkedDestinationRules = policySets.filter((set) => set.destinationRuleId).length;
  const linkedQualityProfiles = policySets.filter((set) => set.qualityProfileId).length;

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

  function startCreate() {
    setEditingId(null);
    setFormState(createPolicySetForm());
    setSelectedStarterId(null);
    setShowDetailedRules(false);
  }

  function applyStarter(starter: (typeof MEDIA_PLAN_STARTERS)[number]) {
    setEditingId(null);
    setFormState({
      ...createPolicySetForm(),
      ...starter.values
    });
    setSelectedStarterId(starter.id);
    setShowDetailedRules(false);
  }

  function startEdit(policySet: PolicySetItem) {
    setEditingId(policySet.id);
    setSelectedStarterId(null);
    setShowDetailedRules(false);
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

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const isEditing = editingId !== null;
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
      description="Describe the experience you want for a part of your library. Deluno combines quality, release preferences, storage routing, and automation behind that plan."
    >
      <div className="fluid-kpi-grid">
        <KpiCard
          label="Media plans"
          value={String(policySets.length)}
          icon={ShieldCheck}
          meta="Reusable experiences available to your libraries and titles."
          sparkline={[1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5, 6, 6]}
        />
        <KpiCard
          label="Active"
          value={String(enabledSets)}
          icon={Sparkles}
          meta="Plans currently ready to use."
          sparkline={[1, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 4, 4, 5, 5]}
        />
        <KpiCard
          label="With library route"
          value={String(linkedDestinationRules)}
          icon={Route}
          meta="Plans that know where imported media belongs."
          sparkline={[0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5]}
        />
        <KpiCard
          label="With quality goal"
          value={String(linkedQualityProfiles)}
          icon={SlidersHorizontal}
          meta="Plans with a defined quality target."
          sparkline={[0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5, 5]}
        />
      </div>

      <Card className="border-primary/20 bg-gradient-to-r from-primary/[0.08] via-primary/[0.03] to-transparent">
        <CardHeader>
          <CardTitle>Start with the library you want</CardTitle>
          <CardDescription>
            Choose a starting point, then tailor the details below. Nothing is saved until you create the media plan.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
          {MEDIA_PLAN_STARTERS.map((starter) => (
            <button
              key={starter.id}
              type="button"
              onClick={() => applyStarter(starter)}
              className={`rounded-xl border p-4 text-left transition hover:-translate-y-0.5 hover:border-primary/40 hover:bg-primary/5 ${
                selectedStarterId === starter.id ? "border-primary/50 bg-primary/[0.07]" : "border-hairline bg-card/85"
              }`}
            >
              <p className="font-display text-base font-semibold tracking-tight text-foreground">{starter.title}</p>
              <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{starter.description}</p>
              <span className="mt-3 inline-flex items-center gap-1 text-xs font-semibold text-primary">
                Use this starting point <Wand2 className="h-3.5 w-3.5" />
              </span>
              <span className="mt-2 block text-xs leading-relaxed text-muted-foreground">
                Includes: {describeStarter(starter)}
              </span>
            </button>
          ))}
        </CardContent>
      </Card>

      <div className="settings-split settings-split-balanced">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>{editingId ? "Edit media plan" : "Create media plan"}</CardTitle>
            <CardDescription>
              Start with the outcome you want. Detailed rules are available when you need them and never replace choices you have already made.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handleSubmit}>
              <div className="grid gap-[var(--grid-gap)] md:grid-cols-2">
                <Field label="Plan name" description="Give this experience an understandable name, such as Family Movies 1080p, Premium 4K, or Anime.">
                  <Input value={formState.name} onChange={(event) => setFormState((current) => ({ ...current, name: event.target.value }))} />
                </Field>
                <Field label="Media type" description="Choose Movies or TV. Changing this resets choices that only apply to the other media type.">
                  <select
                    value={formState.mediaType}
                    onChange={(event) => setFormState((current) => ({
                      ...current,
                      mediaType: event.target.value,
                      qualityProfileId: "",
                      destinationRuleId: "",
                      customFormatIds: []
                    }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="movies">Movies</option>
                    <option value="tv">TV</option>
                  </select>
                </Field>
                <Field label="Quality goal" description="The quality tiers and upgrade behaviour Deluno should aim for when searching.">
                  <select
                    value={formState.qualityProfileId}
                    onChange={(event) => setFormState((current) => ({ ...current, qualityProfileId: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="">Choose later</option>
                    {availableProfiles.map((profile) => (
                      <option key={profile.id} value={profile.id}>
                        {profile.name}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Library route" description="Where imported titles should go: the root folder and naming pattern.">
                  <select
                    value={formState.destinationRuleId}
                    onChange={(event) => setFormState((current) => ({ ...current, destinationRuleId: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="">Choose later</option>
                    {availableDestinationRules.map((rule) => (
                      <option key={rule.id} value={rule.id}>
                        {rule.name}
                      </option>
                    ))}
                  </select>
                </Field>
              </div>

              <Field label="Notes">
                <textarea
                  value={formState.notes}
                  onChange={(event) => setFormState((current) => ({ ...current, notes: event.target.value }))}
                  className="density-control-text min-h-24 w-full rounded-xl border border-hairline bg-surface-2 px-3 py-2 text-foreground outline-none"
                  placeholder="Describe this plan: Kids 1080p, Anime Dual Audio, Premium 4K..."
                />
              </Field>

              <div className="rounded-xl border border-hairline bg-surface-1">
                <button
                  type="button"
                  onClick={() => setShowDetailedRules((current) => !current)}
                  className="flex w-full items-center justify-between gap-[var(--grid-gap)] p-4 text-left"
                  aria-expanded={showDetailedRules}
                >
                  <span>
                    <span className="block font-display text-base font-semibold text-foreground">Fine-tune detailed rules</span>
                    <span className="mt-1 block text-sm leading-relaxed text-muted-foreground">
                      Optional search timing and release-preference rules for granular setups. Your basic plan works without changing these.
                    </span>
                  </span>
                  <ChevronDown className={`h-5 w-5 shrink-0 text-muted-foreground transition-transform ${showDetailedRules ? "rotate-180" : ""}`} />
                </button>

                {showDetailedRules ? (
                  <div className="space-y-[var(--page-gap)] border-t border-hairline p-4">
                    <div className="grid gap-[var(--grid-gap)] md:grid-cols-2">
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
                      <p className="font-medium text-foreground">Release preferences</p>
                      <p className="mt-1 text-sm text-muted-foreground">Pick the custom-format rules this plan should apply when comparing candidates.</p>
                      <div className="mt-3 flex flex-wrap gap-2">
                        {availableCustomFormats.map((format) => {
                          const active = formState.customFormatIds.includes(format.id);
                          return (
                            <button
                              key={format.id}
                              type="button"
                              onClick={() => toggleCustomFormat(format.id)}
                              className={`rounded-full border px-3 py-1.5 text-xs transition-colors ${
                                active
                                  ? "border-primary/40 bg-primary/10 text-primary"
                                  : "border-hairline bg-card text-muted-foreground hover:border-primary/30 hover:text-foreground"
                              }`}
                            >
                              {format.name} · {format.score >= 0 ? `+${format.score}` : format.score}
                            </button>
                          );
                        })}
                        {availableCustomFormats.length === 0 ? (
                          <p className="text-sm text-muted-foreground">No custom formats available for this media type yet.</p>
                        ) : null}
                      </div>
                    </div>
                  </div>
                ) : null}
              </div>

              <div className="grid gap-3 md:grid-cols-2">
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
              </div>

              <div className="flex flex-wrap gap-2">
                <Button type="submit" disabled={busyKey === "create" || (editingId !== null && busyKey === `save:${editingId}`)}>
                  {busyKey === "create" || (editingId !== null && busyKey === `save:${editingId}`) ? (
                    <LoaderCircle className="h-4 w-4 animate-spin" />
                  ) : null}
                  {editingId ? "Save media plan" : "Create media plan"}
                </Button>
                {editingId ? (
                  <Button type="button" variant="outline" onClick={startCreate}>
                    Cancel editing
                  </Button>
                ) : null}
              </div>
            </form>
          </CardContent>
        </Card>

        <div className="settings-side-stack">
          <Card>
            <CardHeader>
              <CardTitle>What media plans solve</CardTitle>
              <CardDescription>
                Media plans let one Deluno library handle different scenarios without duplicating the app.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 text-sm text-muted-foreground">
              <GuidanceRow icon={ShieldCheck} title="One library, many experiences">
                Keep separate behaviour for standard, 4K, anime, or kids content without cloning the whole app.
              </GuidanceRow>
              <GuidanceRow icon={Route} title="Storage plus quality">
                Pair a library route with a quality goal so the plan says both <strong className="text-foreground">what</strong> to acquire and <strong className="text-foreground">where</strong> it goes.
              </GuidanceRow>
              <GuidanceRow icon={Wand2} title="Reusable release preferences">
                Carry the same release preferences with the plan instead of rebuilding them title by title.
              </GuidanceRow>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Current media plans</CardTitle>
              <CardDescription>The library experiences you have defined so far.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {policySets.map((policySet) => (
                <div key={policySet.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="font-display text-base font-semibold text-foreground">{policySet.name}</p>
                        <Badge variant={policySet.isEnabled ? "success" : "default"}>
                          {policySet.isEnabled ? "Enabled" : "Paused"}
                        </Badge>
                        <Badge variant="info">{policySet.mediaType === "tv" ? "TV" : "Movies"}</Badge>
                      </div>
                      <p className="text-sm text-muted-foreground">
                        {policySet.qualityProfileName ?? "Quality goal not chosen"} · {policySet.destinationRuleName ?? "Library route not chosen"}
                      </p>
                      <p className="text-xs text-muted-foreground">
                        {splitCsv(policySet.customFormatIds).length} custom formats · upgrade until cutoff {policySet.upgradeUntilCutoff ? "on" : "off"}
                      </p>
                    </div>
                    <div className="flex gap-2">
                      <Button size="sm" variant="outline" onClick={() => startEdit(policySet)}>
                        Edit
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => void handleDelete(policySet.id)} disabled={busyKey === `delete:${policySet.id}`}>
                        {busyKey === `delete:${policySet.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                        Remove
                      </Button>
                    </div>
                  </div>
                  {policySet.notes ? (
                    <p className="mt-3 rounded-xl border border-hairline bg-card px-3 py-2 text-sm text-muted-foreground">
                      {policySet.notes}
                    </p>
                  ) : null}
                </div>
              ))}
              {policySets.length === 0 ? (
                <div className="rounded-xl border border-dashed border-hairline bg-surface-1 p-[var(--tile-pad)] text-sm text-muted-foreground">
                  No media plans yet. Start with Family Movies, Premium 4K, Everyday TV, or Anime.
                </div>
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Your libraries</CardTitle>
              <CardDescription>Libraries available for media-plan assignment.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {libraries.map((library) => (
                <div key={library.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                  <p className="font-medium text-foreground">{library.name}</p>
                  <p className="mt-1 text-sm text-muted-foreground">
                    {library.qualityProfileName ?? "Quality goal not chosen"} · {library.rootPath}
                  </p>
                </div>
              ))}
            </CardContent>
          </Card>
        </div>
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

function describeStarter(starter: (typeof MEDIA_PLAN_STARTERS)[number]) {
  const mediaType = starter.values.mediaType === "tv" ? "TV" : "movies";
  const searchSchedule = starter.values.searchIntervalOverrideHours
    ? `search every ${starter.values.searchIntervalOverrideHours} hours`
    : "use the library search schedule";
  const retryDelay = starter.values.retryDelayOverrideHours
    ? `retry after ${starter.values.retryDelayOverrideHours} hours`
    : "use the library retry delay";
  const upgrades = starter.values.upgradeUntilCutoff ? "upgrade until the quality goal is met" : "keep the first accepted release";

  return `${mediaType}; ${searchSchedule}; ${retryDelay}; ${upgrades}.`;
}

function Field({ children, description, label }: { children: ReactNode; description?: string; label: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
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
    <label className="density-field density-control-text flex items-center gap-3 rounded-xl border border-hairline bg-surface-1 text-foreground">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      {label}
    </label>
  );
}

function GuidanceRow({
  icon: Icon,
  title,
  children
}: {
  icon: typeof ShieldCheck;
  title: string;
  children: ReactNode;
}) {
  return (
    <div className="flex gap-3 rounded-xl border border-hairline bg-surface-1 p-4">
      <div className="mt-0.5 rounded-lg bg-primary/10 p-2 text-primary">
        <Icon className="h-4 w-4" />
      </div>
      <div>
        <p className="font-medium text-foreground">{title}</p>
        <p className="mt-1">{children}</p>
      </div>
    </div>
  );
}
