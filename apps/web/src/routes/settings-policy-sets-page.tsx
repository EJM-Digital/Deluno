import { useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { LoaderCircle, Route, ShieldCheck, SlidersHorizontal, Sparkles, Wand2 } from "lucide-react";
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
  }

  function startEdit(policySet: PolicySetItem) {
    setEditingId(policySet.id);
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
        throw new Error(isEditing ? "Policy set could not be updated." : "Policy set could not be created.");
      }

      toast.success(isEditing ? "Policy set updated" : "Policy set created");
      startCreate();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Policy set action failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDelete(policySetId: string) {
    setBusyKey(`delete:${policySetId}`);
    try {
      const response = await authedFetch(`/api/policy-sets/${policySetId}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) {
        throw new Error("Policy set could not be removed.");
      }

      toast.success("Policy set removed");
      if (editingId === policySetId) {
        startCreate();
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Policy set could not be removed.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title="Policy Sets"
      description="Combine quality profiles, custom formats, and destination rules into reusable policies that keep Deluno single-install and easier to reason about."
    >
      <div className="fluid-kpi-grid">
        <KpiCard
          label="Policy sets"
          value={String(policySets.length)}
          icon={ShieldCheck}
          meta="Reusable acquisition policies available to future title and library assignment."
          sparkline={[1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5, 6, 6]}
        />
        <KpiCard
          label="Enabled"
          value={String(enabledSets)}
          icon={Sparkles}
          meta="Policies currently active for assignment."
          sparkline={[1, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 4, 4, 5, 5]}
        />
        <KpiCard
          label="With route"
          value={String(linkedDestinationRules)}
          icon={Route}
          meta="Policy sets already attached to a destination rule."
          sparkline={[0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5]}
        />
        <KpiCard
          label="With quality"
          value={String(linkedQualityProfiles)}
          icon={SlidersHorizontal}
          meta="Policy sets already attached to a quality profile."
          sparkline={[0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5, 5]}
        />
      </div>

      <div className="settings-split settings-split-balanced">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>{editingId ? "Edit policy set" : "Create policy set"}</CardTitle>
            <CardDescription>
              Policy sets are where Deluno starts to beat multiple Arr installs. They combine what to grab with where to put it.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[calc(var(--field-group-pad)*0.9)]" onSubmit={handleSubmit}>
              <div className="grid gap-[var(--grid-gap)] md:grid-cols-2">
                <Field label="Policy name" description="A descriptive name for this acquisition policy (e.g., Standard 1080p, Premium 4K, Anime).">
                  <Input value={formState.name} onChange={(event) => setFormState((current) => ({ ...current, name: event.target.value }))} />
                </Field>
                <Field label="Media type" description="Whether this policy applies to Movies or TV series. Changing this resets quality and route selections.">
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
                <Field label="Quality profile" description="What quality tiers and upgrade behaviour this policy should use when searching.">
                  <select
                    value={formState.qualityProfileId}
                    onChange={(event) => setFormState((current) => ({ ...current, qualityProfileId: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="">No profile selected</option>
                    {availableProfiles.map((profile) => (
                      <option key={profile.id} value={profile.id}>
                        {profile.name}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Destination rule" description="Where imported titles should be routed (which root folder and naming pattern).">
                  <select
                    value={formState.destinationRuleId}
                    onChange={(event) => setFormState((current) => ({ ...current, destinationRuleId: event.target.value }))}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="">No rule selected</option>
                    {availableDestinationRules.map((rule) => (
                      <option key={rule.id} value={rule.id}>
                        {rule.name}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Search override (hours)" description="How often Deluno should search for this policy instead of using the library default interval.">
                  <PresetField
                    inputType="number"
                    value={formState.searchIntervalOverrideHours}
                    onChange={(value) => setFormState((current) => ({ ...current, searchIntervalOverrideHours: value }))}
                    options={OVERRIDE_INTERVAL_OPTIONS}
                    customLabel="Custom interval"
                    customPlaceholder="Hours"
                  />
                </Field>
                <Field label="Retry override (hours)" description="How long Deluno should wait before retrying a failed search for this policy.">
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

              <Field label="Notes">
                <textarea
                  value={formState.notes}
                  onChange={(event) => setFormState((current) => ({ ...current, notes: event.target.value }))}
                  className="density-control-text min-h-24 w-full rounded-xl border border-hairline bg-surface-2 px-3 py-2 text-foreground outline-none"
                  placeholder="Explain what this policy is for: Kids 1080p, Anime Dual Audio, Premium 4K..."
                />
              </Field>

              <Card className="border-hairline bg-surface-1">
                <CardHeader>
                  <CardTitle>Custom format boosts</CardTitle>
                  <CardDescription>
                    Pick the release scoring rules this policy should carry with it.
                  </CardDescription>
                </CardHeader>
                <CardContent className="flex flex-wrap gap-2">
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
                </CardContent>
              </Card>

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
                  {editingId ? "Save policy set" : "Create policy set"}
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
              <CardTitle>What policy sets solve</CardTitle>
              <CardDescription>
                Policy sets are how Deluno should collapse multiple Arr instances into one install.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 text-sm text-muted-foreground">
              <GuidanceRow icon={ShieldCheck} title="One install, many policies">
                Keep separate behaviour for standard, 4K, anime, or kids content without cloning the whole app.
              </GuidanceRow>
              <GuidanceRow icon={Route} title="Routing plus quality">
                Pair a destination rule with a quality profile so the policy says both <strong className="text-foreground">what</strong> to acquire and <strong className="text-foreground">where</strong> it goes.
              </GuidanceRow>
              <GuidanceRow icon={Wand2} title="Reusable scoring">
                Carry custom-format scoring with the policy instead of rebuilding the same preference stack repeatedly.
              </GuidanceRow>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Current policy sets</CardTitle>
              <CardDescription>Reusable policy building blocks for the next assignment layer.</CardDescription>
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
                        {policySet.qualityProfileName ?? "No quality profile"} · {policySet.destinationRuleName ?? "No destination rule"}
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
                <div className="rounded-xl border border-dashed border-hairline bg-surface-1 p-6 text-sm text-muted-foreground">
                  No policy sets yet. Start with one for a core case like Standard 1080p, Premium 4K, or Anime Dual Audio.
                </div>
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Your libraries</CardTitle>
              <CardDescription>Libraries available for policy set assignment.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {libraries.map((library) => (
                <div key={library.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                  <p className="font-medium text-foreground">{library.name}</p>
                  <p className="mt-1 text-sm text-muted-foreground">
                    {library.qualityProfileName ?? "No quality profile"} · {library.rootPath}
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
