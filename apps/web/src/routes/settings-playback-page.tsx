/**
 * Playback goals — a friendly front door to typed compatibility plans.
 *
 * Device profiles deliberately ask for capabilities rather than model names.
 * A goal can then require that a release works on every selected device while
 * keeping the exact compiled gates visible to an advanced user.
 */
import { useMemo, useState, type Dispatch, type FormEvent, type SetStateAction } from "react";
import { Check, Eye, Plus, Trash2 } from "lucide-react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Button } from "../components/ui/button";
import { CheckboxRow } from "../components/ui/checkbox";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFacts, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { configurationNavAreas } from "../components/app/settings-shell";
import {
  fetchJson,
  fetchPlaybackGoalCompilation,
  type PlaybackCapability,
  type PlaybackDeviceGroup,
  type PlaybackDeviceProfile,
  type PlaybackGoalCompilation,
  type PlaybackGoalItem,
  type PreferenceTraitDefinition,
  type ReleasePreferenceRegistryResponse
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { resolvePlaybackGoalPreset, type PlaybackGoalPresetId } from "../lib/playback-goal-presets";

const TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

interface LoaderData {
  profiles: PlaybackDeviceProfile[];
  groups: PlaybackDeviceGroup[];
  goals: PlaybackGoalItem[];
  registry: ReleasePreferenceRegistryResponse;
}

interface ProfileForm {
  name: string;
  capabilities: PlaybackCapability[];
  isEnabled: boolean;
}

interface GroupForm {
  name: string;
  mode: "every-device" | "primary-device" | "fallback";
  deviceProfileIds: string[];
  primaryDeviceProfileId: string;
}

interface GoalForm {
  name: string;
  mediaType: "movies" | "tv";
  deviceGroupId: string;
  mustPlay: boolean;
  requiredTraitIds: string[];
  requiredAnyTraitGroups: string[][];
  forbiddenTraitIds: string[];
  preferredTraitIds: string[];
  stopWhenTraitId: string;
}

type DrawerMode =
  | { kind: "profile"; id: string | null }
  | { kind: "group"; id: string | null }
  | { kind: "goal"; id: string | null }
  | { kind: "compile"; id: string }
  | null;

export async function settingsPlaybackLoader(): Promise<LoaderData> {
  const [profiles, groups, goals, registry] = await Promise.all([
    fetchJson<PlaybackDeviceProfile[]>("/api/playback/device-profiles"),
    fetchJson<PlaybackDeviceGroup[]>("/api/playback/device-groups"),
    fetchJson<PlaybackGoalItem[]>("/api/playback/goals"),
    fetchJson<ReleasePreferenceRegistryResponse>("/api/v1/release-preferences/registry")
  ]);
  return { profiles, groups, goals, registry };
}

export function SettingsPlaybackPage() {
  const { profiles, groups, goals, registry } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const [drawer, setDrawer] = useState<DrawerMode>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<{ kind: "profile" | "group" | "goal"; id: string; name: string } | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();

  const [profileForm, setProfileForm] = useState<ProfileForm>(emptyProfile());
  const [initialProfileForm, setInitialProfileForm] = useState<ProfileForm>(emptyProfile());
  const [groupForm, setGroupForm] = useState<GroupForm>(emptyGroup());
  const [initialGroupForm, setInitialGroupForm] = useState<GroupForm>(emptyGroup());
  const [goalForm, setGoalForm] = useState<GoalForm>(emptyGoal());
  const [initialGoalForm, setInitialGoalForm] = useState<GoalForm>(emptyGoal());
  const [goalPreset, setGoalPreset] = useState("custom");
  const [compile, setCompile] = useState<PlaybackGoalCompilation | null>(null);
  const [compileBusy, setCompileBusy] = useState(false);

  const editingProfile = drawer?.kind === "profile" && drawer.id ? profiles.find((item) => item.id === drawer.id) ?? null : null;
  const editingGroup = drawer?.kind === "group" && drawer.id ? groups.find((item) => item.id === drawer.id) ?? null : null;
  const editingGoal = drawer?.kind === "goal" && drawer.id ? goals.find((item) => item.id === drawer.id) ?? null : null;
  const profileDirty = drawer?.kind === "profile" && !sameProfile(profileForm, initialProfileForm);
  const groupDirty = drawer?.kind === "group" && !sameGroup(groupForm, initialGroupForm);
  const goalDirty = drawer?.kind === "goal" && !sameGoal(goalForm, initialGoalForm);
  const dirty = Boolean(profileDirty || groupDirty || goalDirty);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);

  const traits = useMemo(
    () => [...registry.traits].sort((left, right) => left.dimension.localeCompare(right.dimension) || left.displayName.localeCompare(right.displayName)),
    [registry.traits]
  );
  const traitMap = useMemo(() => new Map(traits.map((trait) => [trait.id, trait])), [traits]);
  const goalTraits = useMemo(
    () => traits.filter((trait) => trait.transient === false && appliesTo(trait, goalForm.mediaType)),
    [goalForm.mediaType, traits]
  );
  const profileMap = useMemo(() => new Map(profiles.map((profile) => [profile.id, profile])), [profiles]);
  const groupMap = useMemo(() => new Map(groups.map((group) => [group.id, group])), [groups]);

  function openProfile(profile: PlaybackDeviceProfile | null) {
    const next = profile ? profileFrom(profile) : emptyProfile();
    setProfileForm(next);
    setInitialProfileForm(next);
    setMessage(null);
    setSaveState(undefined);
    setDrawer({ kind: "profile", id: profile?.id ?? null });
  }

  function openGroup(group: PlaybackDeviceGroup | null) {
    const next = group ? groupFrom(group) : emptyGroup();
    setGroupForm(next);
    setInitialGroupForm(next);
    setMessage(null);
    setSaveState(undefined);
    setDrawer({ kind: "group", id: group?.id ?? null });
  }

  function openGoal(goal: PlaybackGoalItem | null) {
    const next = goal ? goalFrom(goal) : emptyGoal();
    setGoalForm(next);
    setInitialGoalForm(next);
    setGoalPreset("custom");
    setMessage(null);
    setSaveState(undefined);
    setDrawer({ kind: "goal", id: goal?.id ?? null });
  }

  async function inspectGoal(goal: PlaybackGoalItem) {
    setCompile(null);
    setCompileBusy(true);
    setDrawer({ kind: "compile", id: goal.id });
    try {
      setCompile(await fetchPlaybackGoalCompilation(goal.id));
    } catch {
      setMessage("The goal could not be compiled.");
    } finally {
      setCompileBusy(false);
    }
  }

  function closeDrawer() {
    setDrawer(null);
    setCompile(null);
    setMessage(null);
    setSaveState(undefined);
  }

  function requestClose() {
    if (dirty) {
      setMessage("You have unsaved changes. Save or discard them before closing.");
      return;
    }
    closeDrawer();
  }

  async function submitProfile(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (drawer?.kind !== "profile" || saveState === "saving") return;
    if (!profileForm.name.trim()) {
      setSaveState("error");
      setMessage("Give this device profile a name.");
      return;
    }
    await saveEntity("profile", drawer.id, {
      name: profileForm.name.trim(),
      capabilities: profileForm.capabilities,
      isEnabled: profileForm.isEnabled
    });
  }

  async function submitGroup(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (drawer?.kind !== "group" || saveState === "saving") return;
    if (!groupForm.name.trim()) {
      setSaveState("error");
      setMessage("Give this device group a name.");
      return;
    }
    if (groupForm.deviceProfileIds.length === 0) {
      setSaveState("error");
      setMessage("Choose at least one device profile.");
      return;
    }
    await saveEntity("group", drawer.id, {
      name: groupForm.name.trim(),
      mode: groupForm.mode,
      deviceProfileIds: groupForm.deviceProfileIds,
      primaryDeviceProfileId: groupForm.primaryDeviceProfileId || null
    });
  }

  async function submitGoal(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (drawer?.kind !== "goal" || saveState === "saving") return;
    if (!goalForm.name.trim()) {
      setSaveState("error");
      setMessage("Give this playback goal a name.");
      return;
    }
    if (!goalForm.deviceGroupId) {
      setSaveState("error");
      setMessage("Choose a device group.");
      return;
    }
    await saveEntity("goal", drawer.id, {
      name: goalForm.name.trim(),
      mediaType: goalForm.mediaType,
      deviceGroupId: goalForm.deviceGroupId,
      mustPlay: goalForm.mustPlay,
      requiredTraitIds: goalForm.requiredTraitIds,
      requiredAnyTraitGroups: goalForm.requiredAnyTraitGroups,
      forbiddenTraitIds: goalForm.forbiddenTraitIds,
      preferredTraitIds: goalForm.preferredTraitIds,
      stopWhenTraitId: goalForm.stopWhenTraitId || null
    });
  }

  async function saveEntity(kind: "profile" | "group" | "goal", id: string | null, body: unknown) {
    setSaveState("saving");
    setMessage(null);
    const endpoint = kind === "profile" ? "device-profiles" : kind === "group" ? "device-groups" : "goals";
    try {
      const response = await authedFetch(`/api/playback/${endpoint}${id ? `/${id}` : ""}`, {
        method: id ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      if (!response.ok) {
        const problem = await response.json().catch(() => null) as { message?: string; errors?: Record<string, string[]> } | null;
        throw new Error(problem?.message ?? (problem?.errors ? Object.values(problem.errors).flat()[0] : null) ?? "Could not save.");
      }
      const saved = await response.json() as { id: string };
      setSaveState("saved");
      setMessage("Saved just now");
      setDrawer({ kind, id: saved.id });
      if (kind === "profile") {
        setInitialProfileForm(profileForm);
      } else if (kind === "group") {
        setInitialGroupForm(groupForm);
      } else {
        setInitialGoalForm(goalForm);
      }
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save.");
    }
  }

  async function removeConfirmed() {
    if (!confirmDelete) return;
    setBusy(`delete:${confirmDelete.kind}:${confirmDelete.id}`);
    const endpoint = confirmDelete.kind === "profile" ? "device-profiles" : confirmDelete.kind === "group" ? "device-groups" : "goals";
    try {
      const response = await authedFetch(`/api/playback/${endpoint}/${confirmDelete.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Could not remove it.");
      toast.success(`${confirmDelete.name} removed`);
      setConfirmDelete(null);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not remove it.");
    } finally {
      setBusy(null);
    }
  }

  function addGoalTrait(target: "requiredTraitIds" | "forbiddenTraitIds" | "preferredTraitIds", value: string) {
    if (!value) return;
    setGoalForm((current) => current[target].includes(value) ? current : { ...current, [target]: [...current[target], value] });
  }

  function addGoalAlternative(value: string, groupIndex: number) {
    if (!value) return;
    setGoalForm((current) => ({
      ...current,
      requiredAnyTraitGroups: current.requiredAnyTraitGroups.map((group, index) =>
        index !== groupIndex || group.includes(value) ? group : [...group, value])
    }));
  }

  function removeGoalAlternative(value: string, groupIndex: number) {
    setGoalForm((current) => ({
      ...current,
      requiredAnyTraitGroups: current.requiredAnyTraitGroups
        .map((group, index) => index === groupIndex ? group.filter((trait) => trait !== value) : group)
        .filter((group) => group.length > 0)
    }));
  }

  function addGoalAlternativeGroup() {
    setGoalForm((current) => ({ ...current, requiredAnyTraitGroups: [...current.requiredAnyTraitGroups, []] }));
  }

  function applyPreset(value: string) {
    setGoalPreset(value);
    if (value === "custom") return;
    const preset = resolvePlaybackGoalPreset(
      value as PlaybackGoalPresetId,
      traits.map((trait) => trait.id)
    );
    if (preset.missingTraitIds.length > 0) {
      setGoalPreset("custom");
      setSaveState("error");
      setMessage(`This preset is unavailable because the playback registry is missing: ${preset.missingTraitIds.join(", ")}.`);
      return;
    }
    setSaveState(undefined);
    setMessage(null);
    setGoalForm((current) => ({
      ...current,
      name: preset.name,
      mustPlay: preset.mustPlay,
      preferredTraitIds: preset.preferredTraitIds,
      stopWhenTraitId: preset.stopWhenTraitId
    }));
  }

  const activeGoalForCompile = drawer?.kind === "compile" ? goals.find((goal) => goal.id === drawer.id) ?? null : null;
  const compiledGateGroups = compile?.plan.requiredAnyTraitGroups ?? [];
  const compiledCompatibilityGroups = compile?.plan.compatibilityGroups ?? [];
  const drawerTitle = drawer?.kind === "profile"
    ? editingProfile?.name ?? "New device profile"
    : drawer?.kind === "group"
      ? editingGroup?.name ?? "New device group"
      : drawer?.kind === "goal"
        ? editingGoal?.name ?? "New playback goal"
        : activeGoalForCompile?.name ?? "Playback plan";

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={TABS} />

      <ListCard
        title="Device profiles"
        count={profiles.length ? `${profiles.length} ${profiles.length === 1 ? "device" : "devices"}` : undefined}
        actions={<Button size="sm" onClick={() => openProfile(null)}><Plus className="h-3.5 w-3.5" />Add device</Button>}
      >
        {profiles.length === 0 ? (
          <ListEmpty title="No playback devices yet" description="Describe what each screen, receiver, or player can actually decode. Do not enter a model number unless you know the capability." actions={<Button size="sm" onClick={() => openProfile(null)}>Add a device</Button>} />
        ) : (
          <ListTable columns={[{ label: "Device" }, { label: "Capabilities", width: "minmax(0,1.8fr)" }, { label: "Evidence" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
            {profiles.map((profile) => (
              <ListRow key={profile.id} onClick={() => openProfile(profile)} selected={drawer?.kind === "profile" && drawer.id === profile.id}>
                <ListNameCell name={profile.name} sub={profile.isEnabled ? "Available for goals" : "Disabled"} />
                <ListCell primary={capabilitySummary(profile.capabilities, traitMap)} secondary={`${profile.capabilities.length} explicit capability${profile.capabilities.length === 1 ? "" : "ies"}`} />
                <ListCell primary={profile.capabilities.some((capability) => capability.state === "unknown" || capability.state === "conflicting") ? "Needs review" : "Owner confirmed"} />
                <ListCell mobile><Chip tone={profile.isEnabled ? "ok" : "idle"}>{profile.isEnabled ? "Enabled" : "Disabled"}</Chip></ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <ListCard
        title="Device groups"
        count={groups.length ? `${groups.length} ${groups.length === 1 ? "group" : "groups"}` : undefined}
        actions={<Button size="sm" onClick={() => openGroup(null)}><Plus className="h-3.5 w-3.5" />Add group</Button>}
      >
        {groups.length === 0 ? (
          <ListEmpty title="No device groups yet" description="A group is the set of devices a library goal must support. Every-device is the safe default." actions={<Button size="sm" onClick={() => openGroup(null)}>Add a group</Button>} />
        ) : (
          <ListTable columns={[{ label: "Group" }, { label: "Devices", width: "minmax(0,1.6fr)" }, { label: "Compatibility" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
            {groups.map((group) => (
              <ListRow key={group.id} onClick={() => openGroup(group)} selected={drawer?.kind === "group" && drawer.id === group.id}>
                <ListNameCell name={group.name} sub={modeLabel(group.mode)} />
                <ListCell primary={group.deviceProfileIds.map((id) => profileMap.get(id)?.name ?? "Missing device").join(" · ")} secondary={`${group.deviceProfileIds.length} selected`} />
                <ListCell primary={group.mode === "every-device" ? "Must work on all" : group.mode === "primary-device" ? "Primary with fallback" : "Fallback group"} />
                <ListCell mobile><Chip tone="info">Configured</Chip></ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <ListCard
        title="Playback goals"
        count={goals.length ? `${goals.length} ${goals.length === 1 ? "goal" : "goals"}` : undefined}
        actions={<Button size="sm" onClick={() => openGoal(null)}><Plus className="h-3.5 w-3.5" />Add goal</Button>}
      >
        {goals.length === 0 ? (
          <ListEmpty title="No playback goals yet" description="A goal turns friendly choices into typed compatibility gates and preference targets. Equipment setup is optional for ordinary quality plans." actions={<Button size="sm" onClick={() => openGoal(null)}>Create a goal</Button>} />
        ) : (
          <ListTable columns={[{ label: "Goal" }, { label: "Applies to" }, { label: "Device group", width: "minmax(0,1.35fr)" }, { label: "Outcome", width: LIST_TRACK.status, mobile: true }]}>
            {goals.map((goal) => (
              <ListRow key={goal.id} onClick={() => openGoal(goal)} selected={drawer?.kind === "goal" && drawer.id === goal.id}>
                <ListNameCell name={goal.name} sub={goal.mustPlay ? "Compatibility is required" : "Capabilities are informational"} />
                <ListCell primary={goal.mediaType === "tv" ? "TV" : "Movies"} />
                <ListCell primary={groupMap.get(goal.deviceGroupId)?.name ?? "Missing group"} secondary={`${goal.preferredTraitIds.length} preference${goal.preferredTraitIds.length === 1 ? "" : "s"}`} />
                <ListCell mobile><Button type="button" variant="outline" size="sm" onClick={(event) => { event.stopPropagation(); void inspectGoal(goal); }}><Eye className="h-3.5 w-3.5" />Inspect</Button></ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={drawer?.kind === "profile"}
        onOpenChange={(open) => { if (!open) requestClose(); }}
        title={drawerTitle}
        description="Explicit playback capability"
        onSubmit={submitProfile}
        footer={<DrawerFooter state={footerState} message={message} saveLabel={editingProfile ? "Save device" : "Add device"} onCancel={requestClose} saveEnabled={editingProfile ? undefined : true} disabled={busy !== null} />}
      >
        <DrawerSection title="Device details">
          <Field label="Name" help="Use a friendly name such as Living-room TV or Headphones.">
            <Input value={profileForm.name} onChange={(event) => setProfileForm((current) => ({ ...current, name: event.target.value }))} placeholder="Living-room TV" autoComplete="off" />
          </Field>
          <SwitchRow label="Available for playback goals" description="Disabled profiles are retained but excluded from compatibility compilation." checked={profileForm.isEnabled} onCheckedChange={(checked) => setProfileForm((current) => ({ ...current, isEnabled: checked }))} />
        </DrawerSection>
        <DrawerSection title="What it can play" aside="I don't know is always valid">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">Record capabilities explicitly. Deluno never guesses from a brand or model name, and missing evidence stays unknown rather than becoming “not supported.”</p>
          <div className="grid gap-2">
            {profileForm.capabilities.map((capability, index) => (
              <div key={`${capability.traitId}-${index}`} className="grid gap-2 rounded-[10px] border border-hairline bg-surface-2/40 p-3">
                <Field label="Capability">
                  <Select value={capability.traitId} onChange={(event) => updateCapability(setProfileForm, index, { traitId: event.target.value })} options={traits.map((trait) => ({ value: trait.id, label: `${trait.dimension} · ${trait.displayName}` }))} />
                </Field>
                <FieldRow>
                  <Field label="Evidence">
                    <Select value={capability.state} onChange={(event) => updateCapability(setProfileForm, index, { state: event.target.value })} options={[{ value: "present", label: "Supported" }, { value: "absent", label: "Not supported" }, { value: "unknown", label: "I don't know" }, { value: "conflicting", label: "Conflicting evidence" }]} />
                  </Field>
                  <Field label="Source">
                    <Select value={capability.source === "owner" ? "user" : capability.source} onChange={(event) => updateCapability(setProfileForm, index, { source: event.target.value })} options={[{ value: "user", label: "My assertion" }, { value: "template", label: "Deluno template" }, { value: "verified-discovery", label: "Verified discovery" }]} />
                  </Field>
                </FieldRow>
                <FieldRow>
                  <Field label="Confidence" optional help="Use 100% only when you are certain.">
                    <Input type="number" min="0" max="1" step="0.05" value={capability.confidence ?? ""} onChange={(event) => updateCapability(setProfileForm, index, { confidence: event.target.value === "" ? null : Number(event.target.value) })} />
                  </Field>
                  <Field label="Note" optional>
                    <Input value={capability.detail ?? ""} onChange={(event) => updateCapability(setProfileForm, index, { detail: event.target.value || null })} placeholder="Pass-through via AVR" />
                  </Field>
                </FieldRow>
                <Field label="Last confirmed" optional help="Keep this current when you verify the playback path. Deluno never treats an old or missing confirmation as a new capability.">
                  <Input type="date" value={dateInputValue(capability.lastConfirmedUtc)} onChange={(event) => updateCapability(setProfileForm, index, { lastConfirmedUtc: event.target.value ? `${event.target.value}T00:00:00.000Z` : null })} />
                </Field>
                <div className="flex justify-end"><Button type="button" variant="ghost" size="sm" onClick={() => setProfileForm((current) => ({ ...current, capabilities: current.capabilities.filter((_, itemIndex) => itemIndex !== index) }))}><Trash2 className="h-3.5 w-3.5" />Remove</Button></div>
              </div>
            ))}
          </div>
          <Button type="button" variant="outline" size="sm" onClick={() => setProfileForm((current) => ({ ...current, capabilities: [...current.capabilities, emptyCapability(traits[0]?.id ?? "")] }))} disabled={traits.length === 0}><Plus className="h-3.5 w-3.5" />Add capability</Button>
        </DrawerSection>
        {editingProfile ? <DrawerSection><DrawerDanger title="Remove this device profile" description="Goals will show a missing-device warning until they are updated." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmDelete({ kind: "profile", id: editingProfile.id, name: editingProfile.name })}>Remove</Button>} /></DrawerSection> : null}
      </Drawer>

      <Drawer
        open={drawer?.kind === "group"}
        onOpenChange={(open) => { if (!open) requestClose(); }}
        title={drawerTitle}
        description="Choose which devices a goal must support"
        onSubmit={submitGroup}
        footer={<DrawerFooter state={footerState} message={message} saveLabel={editingGroup ? "Save group" : "Add group"} onCancel={requestClose} saveEnabled={editingGroup ? undefined : true} disabled={busy !== null} />}
      >
        <DrawerSection title="Group details">
          <Field label="Name"><Input value={groupForm.name} onChange={(event) => setGroupForm((current) => ({ ...current, name: event.target.value }))} placeholder="Every room" autoComplete="off" /></Field>
          <Field label="How should Deluno use it?" help="Every device is safest. Primary device prefers the named device while retaining a fallback path.">
            <Select value={groupForm.mode} onChange={(event) => setGroupForm((current) => ({ ...current, mode: event.target.value as GroupForm["mode"] }))} options={[{ value: "every-device", label: "Must work on every device" }, { value: "primary-device", label: "Optimise for a primary device" }, { value: "fallback", label: "Use as a fallback group" }]} />
          </Field>
        </DrawerSection>
        <DrawerSection title="Devices" aside={`${groupForm.deviceProfileIds.length} selected`}>
          {profiles.length === 0 ? <p className="text-[length:var(--type-caption)] text-warning">Add a device profile first.</p> : <div className="grid gap-2">{profiles.map((profile) => <CheckboxRow key={profile.id} label={profile.name} description={`${profile.capabilities.length} capabilities${profile.isEnabled ? "" : " · disabled"}`} checked={groupForm.deviceProfileIds.includes(profile.id)} onCheckedChange={(checked) => setGroupForm((current) => ({ ...current, deviceProfileIds: checked ? [...current.deviceProfileIds, profile.id] : current.deviceProfileIds.filter((id) => id !== profile.id), primaryDeviceProfileId: checked ? current.primaryDeviceProfileId : current.primaryDeviceProfileId === profile.id ? "" : current.primaryDeviceProfileId }))} disabled={!profile.isEnabled} />)}</div>}
          {groupForm.mode === "primary-device" ? <Field label="Primary device" help="The remaining selected devices are the fallback set."><Select value={groupForm.primaryDeviceProfileId} onChange={(event) => setGroupForm((current) => ({ ...current, primaryDeviceProfileId: event.target.value }))} placeholder="Choose primary device" options={profiles.filter((profile) => currentGroupHasProfile(groupForm, profile.id)).map((profile) => ({ value: profile.id, label: profile.name }))} /></Field> : null}
        </DrawerSection>
        {editingGroup ? <DrawerSection><DrawerDanger title="Remove this device group" description="Playback goals using it will need a different group." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmDelete({ kind: "group", id: editingGroup.id, name: editingGroup.name })}>Remove</Button>} /></DrawerSection> : null}
      </Drawer>

      <Drawer
        open={drawer?.kind === "goal"}
        onOpenChange={(open) => { if (!open) requestClose(); }}
        title={drawerTitle}
        description="Guided choices compiled into the typed release plan"
        onSubmit={submitGoal}
        footer={<DrawerFooter state={footerState} message={message} saveLabel={editingGoal ? "Save goal" : "Create goal"} onCancel={requestClose} saveEnabled={editingGoal ? undefined : true} disabled={busy !== null} />}
      >
        <DrawerSection title="Start with an outcome">
          <Field label="Goal preset" help="Presets only fill the typed choices below; you can inspect every consequence before using the goal.">
            <Select value={goalPreset} onChange={(event) => applyPreset(event.target.value)} options={[{ value: "custom", label: "Custom" }, { value: "everywhere", label: "Works everywhere" }, { value: "main", label: "Best for my main setup" }, { value: "lossless", label: "Best lossless audio" }, { value: "atmos", label: "Atmos preferred" }, { value: "storage", label: "Storage balanced" }]} />
          </Field>
          <Field label="Name"><Input value={goalForm.name} onChange={(event) => setGoalForm((current) => ({ ...current, name: event.target.value }))} placeholder="Works everywhere" autoComplete="off" /></Field>
          <FieldRow>
            <Field label="Applies to"><SegmentedControl<"movies" | "tv"> aria-label="Media type" value={goalForm.mediaType} onValueChange={(value) => setGoalForm((current) => ({ ...current, mediaType: value, requiredTraitIds: [], requiredAnyTraitGroups: [], forbiddenTraitIds: [], preferredTraitIds: [], stopWhenTraitId: "" }))} options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV" }]} /></Field>
            <Field label="Device group"><Select value={goalForm.deviceGroupId} onChange={(event) => setGoalForm((current) => ({ ...current, deviceGroupId: event.target.value }))} placeholder="Choose a group" options={groups.map((group) => ({ value: group.id, label: `${group.name} · ${modeLabel(group.mode)}` }))} /></Field>
          </FieldRow>
          <SwitchRow label="Must play on these devices" description="When enabled, unsupported or unproven compatibility is rejected or held for review. When disabled, capabilities explain the choice but do not gate it." checked={goalForm.mustPlay} onCheckedChange={(checked) => setGoalForm((current) => ({ ...current, mustPlay: checked }))} />
        </DrawerSection>
        <DrawerSection title="Must have" aside="Hard gates">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">These traits must be proven on the release. Unknown evidence never becomes approval.</p>
          <TraitPicker traits={goalTraits} selected={goalForm.requiredTraitIds} onAdd={(value) => addGoalTrait("requiredTraitIds", value)} onRemove={(value) => setGoalForm((current) => ({ ...current, requiredTraitIds: current.requiredTraitIds.filter((trait) => trait !== value) }))} label="Add a required capability" traitMap={traitMap} />
        </DrawerSection>
        <DrawerSection title="Any compatible option" aside="OR hard gates">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">Add alternatives when one of several proven traits is enough. Each row is a separate required group; rows are combined with AND.</p>
          <div className="grid gap-2">
            {goalForm.requiredAnyTraitGroups.map((group, groupIndex) => (
              <div key={`required-any-${groupIndex}`} className="rounded-[10px] border border-hairline bg-surface-2/40 p-3">
                <p className="mb-2 text-[length:var(--type-caption)] font-medium text-foreground">Required group {groupIndex + 1} · any one</p>
                <TraitPicker
                  traits={goalTraits}
                  selected={group}
                  onAdd={(value) => addGoalAlternative(value, groupIndex)}
                  onRemove={(value) => removeGoalAlternative(value, groupIndex)}
                  label="Add an alternative"
                  traitMap={traitMap}
                />
              </div>
            ))}
            <Button type="button" variant="outline" size="sm" onClick={addGoalAlternativeGroup}>Add an alternative group</Button>
          </div>
        </DrawerSection>
        <DrawerSection title="Must not have" aside="Forbidden gates">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">These traits reject a release when they are proven. Unknown evidence stays review-only.</p>
          <TraitPicker traits={goalTraits} selected={goalForm.forbiddenTraitIds} onAdd={(value) => addGoalTrait("forbiddenTraitIds", value)} onRemove={(value) => setGoalForm((current) => ({ ...current, forbiddenTraitIds: current.forbiddenTraitIds.filter((trait) => trait !== value) }))} label="Add a forbidden capability" traitMap={traitMap} />
        </DrawerSection>
        <DrawerSection title="Prefer" aside="Stop when prevents endless upgrades">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">Preferences are ordered best-first. They can only drive upgrades when Stop when is explicitly set; otherwise they remain a same-search tie-break.</p>
          <TraitPicker traits={goalTraits} selected={goalForm.preferredTraitIds} onAdd={(value) => addGoalTrait("preferredTraitIds", value)} onRemove={(value) => setGoalForm((current) => ({ ...current, preferredTraitIds: current.preferredTraitIds.filter((trait) => trait !== value), stopWhenTraitId: current.stopWhenTraitId === value ? "" : current.stopWhenTraitId }))} label="Add a preferred trait" traitMap={traitMap} />
          <Field label="Stop when" optional help="Choose the preferred trait that ends automatic upgrade work."><Select value={goalForm.stopWhenTraitId} onChange={(event) => setGoalForm((current) => ({ ...current, stopWhenTraitId: event.target.value }))} placeholder="Tie-break only — no automatic upgrades" options={goalForm.preferredTraitIds.map((id) => ({ value: id, label: traitMap.get(id)?.displayName ?? id }))} /></Field>
        </DrawerSection>
        {editingGoal ? <DrawerSection title="Inspect compiled plan"><Button type="button" variant="outline" onClick={() => void inspectGoal(editingGoal)}><Eye className="h-4 w-4" />View gates and outcome</Button><DrawerDanger title="Remove this playback goal" description="The goal will no longer be available when building a Media Plan." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmDelete({ kind: "goal", id: editingGoal.id, name: editingGoal.name })}>Remove</Button>} /></DrawerSection> : null}
      </Drawer>

      <Drawer open={drawer?.kind === "compile"} onOpenChange={(open) => { if (!open) closeDrawer(); }} title={drawerTitle} description="Exact typed compatibility and preference result" footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={closeDrawer} />}>
        {compileBusy ? <DrawerSection><p className="text-[length:var(--type-caption)] text-muted-foreground">Compiling the goal…</p></DrawerSection> : compile ? <>
          <DrawerSection title="Outcome">
            <DrawerFacts items={[{ label: "Devices", value: compile.selectedDevices.map((device) => device.name).join(" · ") || "None selected" }, { label: "Hard compatibility", value: compile.goal.mustPlay ? "Required" : "Informational" }, { label: "Plan hash", value: compile.planHash, mono: true }]} />
            {compile.requiresReview ? <Chip tone="warn">Needs review before automatic use</Chip> : <Chip tone="ok">Ready for automatic evaluation</Chip>}
          </DrawerSection>
          <DrawerSection title="Device compatibility" aside={`${compiledCompatibilityGroups.length} device path${compiledCompatibilityGroups.length === 1 ? "" : "s"}`}>
            {compiledCompatibilityGroups.length ? <div className="grid gap-2">{compiledCompatibilityGroups.map((group) => <div key={group.id} className="grid gap-1 text-[length:var(--type-body-sm)]"><span className="font-medium text-foreground">{compatibilityGroupLabel(group.id, compile.selectedDevices)}</span>{group.alternatives.map((alternative, index) => <div key={`${group.id}-${index}`} className="flex items-start gap-2 pl-2 text-muted-foreground"><Check className="mt-0.5 h-3.5 w-3.5 shrink-0 text-success" /><span>{alternative.map((id) => traitMap.get(id)?.displayName ?? id).join(" + ")}</span></div>)}</div>)}</div> : <p className="text-[length:var(--type-caption)] text-muted-foreground">No proven device capabilities became hard gates.</p>}
          </DrawerSection>
          <DrawerSection title="Additional hard gates" aside={`${compiledGateGroups.length} groups`}>
            {compiledGateGroups.length ? <div className="grid gap-1.5">{compiledGateGroups.map((group, index) => <div key={index} className="flex items-center gap-2 text-[length:var(--type-body-sm)]"><Check className="h-3.5 w-3.5 shrink-0 text-success" /><span>Any of: {group.map((id) => traitMap.get(id)?.displayName ?? id).join(", ")}</span></div>)}</div> : <p className="text-[length:var(--type-caption)] text-muted-foreground">No additional capability gates configured.</p>}
          </DrawerSection>
          <DrawerSection title="Preference ladder">
            {compile.plan.families.length ? compile.plan.families.flatMap((family) => family.levels.map((level) => <div key={`${family.id}-${level.id}`} className="flex items-center justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0"><span className="text-[length:var(--type-body-sm)] text-foreground">{level.traitIds.map((id) => traitMap.get(id)?.displayName ?? id).join(" or ")}</span><span className="text-[length:var(--type-caption)] text-muted-foreground">{family.upgradeDriving && level.id === family.targetLevelId ? "Stop when" : family.intent === "tieBreak" ? "Nice to have" : "Preferred"}</span></div>)) : <p className="text-[length:var(--type-caption)] text-muted-foreground">No preference ladder configured.</p>}
          </DrawerSection>
          {compile.unknownCapabilities.length ? <DrawerSection title="Unknown capability"><div className="grid gap-1">{compile.unknownCapabilities.map((item) => <p key={item} className="text-[length:var(--type-caption)] text-warning">{item}</p>)}</div></DrawerSection> : null}
          {compile.warnings.length ? <DrawerSection title="Review notes"><div className="grid gap-1">{compile.warnings.map((warning) => <p key={warning} className="text-[length:var(--type-caption)] text-warning">{warning}</p>)}</div></DrawerSection> : null}
        </> : <DrawerSection><p className="text-[length:var(--type-caption)] text-warning">{message ?? "Nothing to show."}</p></DrawerSection>}
      </Drawer>

      <ConfirmDialog open={confirmDelete !== null} onOpenChange={(open) => { if (!open) setConfirmDelete(null); }} title={`Remove “${confirmDelete?.name ?? "this item"}”?`} description="This removes the saved playback configuration. Existing media is not changed." confirmLabel="Remove" busy={busy !== null} onConfirm={() => void removeConfirmed()} />
    </div>
  );
}

function TraitPicker({ traits, selected, onAdd, onRemove, label, traitMap }: { traits: PreferenceTraitDefinition[]; selected: string[]; onAdd: (value: string) => void; onRemove: (value: string) => void; label: string; traitMap: Map<string, PreferenceTraitDefinition> }) {
  const available = traits.filter((trait) => !selected.includes(trait.id));
  return <div className="grid gap-2"><div className="flex flex-wrap gap-2">{selected.map((id) => <span key={id} className="inline-flex items-center gap-1 rounded-full border border-primary/25 bg-primary/10 px-2.5 py-1 text-[length:var(--type-caption)] text-primary">{traitMap.get(id)?.displayName ?? id}<button type="button" className="text-primary/70 hover:text-primary" aria-label={`Remove ${traitMap.get(id)?.displayName ?? id}`} onClick={() => onRemove(id)}>×</button></span>)}</div><Field label={label} hideLabel><Select value="" onChange={(event) => onAdd(event.target.value)} placeholder={available.length ? "Choose a trait" : "All applicable traits selected"} options={available.map((trait) => ({ value: trait.id, label: `${trait.dimension} · ${trait.displayName}` }))} disabled={!available.length} /></Field></div>;
}

function emptyProfile(): ProfileForm { return { name: "", capabilities: [], isEnabled: true }; }
function emptyGroup(): GroupForm { return { name: "", mode: "every-device", deviceProfileIds: [], primaryDeviceProfileId: "" }; }
function emptyGoal(): GoalForm { return { name: "", mediaType: "movies", deviceGroupId: "", mustPlay: true, requiredTraitIds: [], requiredAnyTraitGroups: [], forbiddenTraitIds: [], preferredTraitIds: [], stopWhenTraitId: "" }; }
function emptyCapability(traitId: string): PlaybackCapability { return { traitId, state: "present", source: "user", confidence: 1, detail: null, lastConfirmedUtc: new Date().toISOString() }; }
function profileFrom(profile: PlaybackDeviceProfile): ProfileForm { return { name: profile.name, capabilities: profile.capabilities.map((capability) => ({ ...capability, source: capability.source === "owner" ? "user" : capability.source, lastConfirmedUtc: capability.lastConfirmedUtc ?? null })), isEnabled: profile.isEnabled }; }
function groupFrom(group: PlaybackDeviceGroup): GroupForm { return { name: group.name, mode: group.mode === "primary-device" || group.mode === "fallback" ? group.mode : "every-device", deviceProfileIds: [...group.deviceProfileIds], primaryDeviceProfileId: group.primaryDeviceProfileId ?? "" }; }
function goalFrom(goal: PlaybackGoalItem): GoalForm { return { name: goal.name, mediaType: goal.mediaType === "tv" ? "tv" : "movies", deviceGroupId: goal.deviceGroupId, mustPlay: goal.mustPlay, requiredTraitIds: [...goal.requiredTraitIds], requiredAnyTraitGroups: goal.requiredAnyTraitGroups.map((group) => [...group]), forbiddenTraitIds: [...(goal.forbiddenTraitIds ?? [])], preferredTraitIds: [...goal.preferredTraitIds], stopWhenTraitId: goal.stopWhenTraitId ?? "" }; }
function sameProfile(left: ProfileForm, right: ProfileForm) { return JSON.stringify(left) === JSON.stringify(right); }
function sameGroup(left: GroupForm, right: GroupForm) { return JSON.stringify(left) === JSON.stringify(right); }
function sameGoal(left: GoalForm, right: GoalForm) { return JSON.stringify(left) === JSON.stringify(right); }
function updateCapability(setter: Dispatch<SetStateAction<ProfileForm>>, index: number, patch: Partial<PlaybackCapability>) { setter((current) => ({ ...current, capabilities: current.capabilities.map((capability, itemIndex) => itemIndex === index ? { ...capability, ...patch } : capability) })); }
function capabilitySummary(capabilities: PlaybackCapability[], traitMap: Map<string, PreferenceTraitDefinition>) { const names = capabilities.filter((capability) => capability.state === "present").map((capability) => traitMap.get(capability.traitId)?.displayName ?? capability.traitId); return names.slice(0, 3).join(" · ") || "No proven capabilities"; }
function modeLabel(mode: string) { return mode === "primary-device" ? "Primary with fallback" : mode === "fallback" ? "Fallback" : "Every device"; }
function compatibilityGroupLabel(id: string, devices: PlaybackDeviceProfile[]) {
  if (id.startsWith("device/")) {
    const profileId = id.slice("device/".length);
    return `Must work on ${devices.find((device) => device.id === profileId)?.name ?? profileId}`;
  }
  if (id.startsWith("primary-with-fallback/")) return "Primary device with fallback paths";
  if (id.startsWith("fallback/")) return "Any selected fallback device";
  return id;
}
function currentGroupHasProfile(form: GroupForm, profileId: string) { return form.deviceProfileIds.includes(profileId); }
function dateInputValue(value: string | null) { return value ? value.slice(0, 10) : ""; }
function appliesTo(trait: PreferenceTraitDefinition, mediaType: "movies" | "tv") { return !trait.mediaTypes?.length || trait.mediaTypes.includes("both") || trait.mediaTypes.includes(mediaType); }
