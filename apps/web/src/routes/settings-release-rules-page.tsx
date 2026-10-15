/**
 * Acquisition Rules — tag-aware release timing and term rules.
 *
 * A profile is deliberately smaller than a Quality Profile: it answers the
 * question "when should this release be allowed?" and can be global or apply
 * to one reusable title tag. The planner applies these rules before a result
 * can reach a download client.
 */
import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { ArrowDown, ArrowUp } from "lucide-react";
import { TagPicker, joinTagList, parseTagList } from "../components/app/tag-picker";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { Button } from "../components/ui/button";
import { Checkbox } from "../components/ui/checkbox";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { Textarea } from "../components/ui/textarea";
import { configurationNavAreas } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { authedFetch } from "../lib/use-auth";
import { readErrorText, fetchJson, type IndexerItem, type PlatformSettingsSnapshot, type ReleaseProfileItem, type ReleaseRuleMode, type ReleaseTermScore } from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

const TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

const PROPER_REPACK_OPTIONS = [
  { value: "prefer-and-upgrade", label: "Prefer and upgrade", description: "A Proper or Repack is preferred, and can replace an already-installed file on its own." },
  { value: "do-not-upgrade-automatically", label: "Do not upgrade automatically", description: "Still preferred when choosing between releases found in the same search, but never a reason to replace a file you already have." },
  { value: "do-not-prefer", label: "Do not prefer", description: "Turns this off entirely. Leave Proper/Repack handling to your own Custom Formats." }
];

const RULE_MODE_OPTIONS: { value: ReleaseRuleMode; label: string; description: string }[] = [
  { value: "combine", label: "Use every rule that matches", description: "A title takes the longest wait from all its matching rules, and every must-contain and must-not-contain term from all of them." },
  { value: "first-match", label: "Use only the first rule that matches", description: "Rules are listed in order. A title uses the highest rule whose tags it carries, and nothing else. A rule with no tag is the default for titles no other rule matches." }
];

interface LoaderData {
  profiles: ReleaseProfileItem[];
  indexers: IndexerItem[];
  settings: PlatformSettingsSnapshot;
  ruleMode?: ReleaseRuleMode;
}

interface ProfileForm {
  name: string;
  /** Comma-separated tag names this rule applies to; blank makes it the default rule. */
  tags: string;
  preferredProtocol: string;
  usenetDelayMinutes: string;
  torrentDelayMinutes: string;
  mustContain: string;
  mustNotContain: string;
  preferredTerms: string;
  enabled: boolean;
  indexerIds: string[];
  bypassIfHighestQuality: boolean;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsReleaseRulesLoader(): Promise<LoaderData> {
  const [profiles, indexers, settings, mode] = await Promise.all([
    fetchJson<ReleaseProfileItem[]>("/api/release-profiles"),
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<{ mode: ReleaseRuleMode }>("/api/release-profiles/mode").catch(() => ({ mode: "combine" as ReleaseRuleMode }))
  ]);
  return { profiles, indexers, settings, ruleMode: mode.mode };
}

function RuleModeCard({ mode, onChanged }: { mode: ReleaseRuleMode; onChanged: () => void }) {
  const [value, setValue] = useState<ReleaseRuleMode>(mode);
  const [saveState, setSaveState] = useState<"idle" | "saving" | "error">("idle");

  useEffect(() => setValue(mode), [mode]);

  async function change(next: ReleaseRuleMode) {
    setValue(next);
    setSaveState("saving");
    try {
      const response = await authedFetch("/api/release-profiles/mode", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ mode: next })
      });
      if (!response.ok) throw new Error(await readErrorText(response, "not saved"));
      setSaveState("idle");
      onChanged();
    } catch {
      setValue(mode);
      setSaveState("error");
    }
  }

  return (
    <ListCard title="When more than one rule matches" count="How a title's rules are used">
      <div className="grid gap-3 p-(--card-pad-x)">
        <Select
          aria-label="When more than one rule matches"
          value={value}
          onChange={(event) => void change(event.target.value as ReleaseRuleMode)}
          options={RULE_MODE_OPTIONS.map(({ value: optionValue, label }) => ({ value: optionValue, label }))}
        />
        <p className="text-(length:--type-caption) text-muted-foreground">
          {RULE_MODE_OPTIONS.find((option) => option.value === value)?.description}
        </p>
        {saveState === "error" ? <p className="text-(length:--type-caption) text-destructive">Could not save. Try again.</p> : null}
      </div>
    </ListCard>
  );
}

function ProperRepackCard({ settings }: { settings: PlatformSettingsSnapshot }) {
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [mode, setMode] = useState(settings.properRepackAcquisitionMode);
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");

  async function onChange(nextMode: string) {
    setMode(nextMode);
    setSaveState("saving");
    try {
      const next = await settingsMutation.mutate({ properRepackAcquisitionMode: nextMode });
      setMode(next.properRepackAcquisitionMode);
      setSaveState("saved");
    } catch {
      setMode(settings.properRepackAcquisitionMode);
      setSaveState("error");
    }
  }

  return (
    <ListCard title="Proper &amp; Repack" count="How a corrected re-release affects acquisition">
      <div className="grid gap-3 p-(--card-pad-x)">
        <Select
          value={mode}
          onChange={(event) => onChange(event.target.value)}
          options={PROPER_REPACK_OPTIONS.map(({ value, label }) => ({ value, label }))}
        />
        <p className="text-(length:--type-caption) text-muted-foreground">
          {PROPER_REPACK_OPTIONS.find((option) => option.value === mode)?.description}
        </p>
        {saveState === "error" ? <p className="text-(length:--type-caption) text-destructive">Could not save. Try again.</p> : null}
      </div>
    </ListCard>
  );
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["IndexerChanged", "SettingsChanged"], areas: [ConfigAreas.releaseRules] };

export function SettingsReleaseRulesPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { profiles, indexers, settings, ruleMode = "combine" } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const [dragId, setDragId] = useState<string | null>(null);
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<ProfileForm>(emptyForm());
  const [initialForm, setInitialForm] = useState<ProfileForm>(emptyForm());
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);
  const [legacyWeightsOpen, setLegacyWeightsOpen] = useState(false);

  const editing = mode.kind === "edit" ? profiles.find((profile) => profile.id === mode.id) ?? null : null;
  const open = mode.kind !== "closed";
  const dirty = open && !sameForm(form, initialForm);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const firstMatch = ruleMode === "first-match";
  // The list is in the order the rules are used. The rule with no tag is the default:
  // it is only used when no other rule matches, so it always sits last.
  const sorted = useMemo(
    () => [...profiles].sort((left, right) =>
      Number(tagsOf(left).length === 0) - Number(tagsOf(right).length === 0)
      || left.position - right.position
      || left.name.localeCompare(right.name)),
    [profiles]
  );
  const orderable = sorted.filter((profile) => tagsOf(profile).length > 0);

  async function saveOrder(ids: string[]) {
    try {
      const response = await authedFetch("/api/release-profiles/order", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ids })
      });
      if (!response.ok) throw new Error(await readErrorText(response, "The order could not be saved."));
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The order could not be saved.");
    }
  }

  function move(id: string, toIndex: number) {
    const ids = orderable.map((profile) => profile.id);
    const from = ids.indexOf(id);
    if (from < 0 || toIndex < 0 || toIndex >= ids.length || from === toIndex) return;
    ids.splice(toIndex, 0, ids.splice(from, 1)[0]!);
    void saveOrder(ids);
  }

  function openProfile(profile: ReleaseProfileItem | null) {
    const next = profile ? formFrom(profile) : emptyForm();
    setMode(profile ? { kind: "edit", id: profile.id } : { kind: "create" });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
    setSaveMessage(null);
    setErrors({});
    setLegacyWeightsOpen(false);
  }

  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }

  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saveState === "saving") return;
    const nextErrors = validate(form);
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length > 0) return;

    setSaveState("saving");
    setSaveMessage(null);
    try {
      const body = JSON.stringify({
        name: form.name.trim(),
        tagNames: parseTagList(form.tags),
        preferredProtocol: form.preferredProtocol,
        usenetDelayMinutes: Number(form.usenetDelayMinutes || 0),
        torrentDelayMinutes: Number(form.torrentDelayMinutes || 0),
        mustContain: form.mustContain.trim(),
        mustNotContain: form.mustNotContain.trim(),
        preferredTerms: parsePreferredTerms(form.preferredTerms),
        enabled: form.enabled,
        indexerIds: form.indexerIds,
        bypassIfHighestQuality: form.bypassIfHighestQuality
      });
      const response = await authedFetch(
        mode.kind === "edit" ? `/api/release-profiles/${mode.id}` : "/api/release-profiles",
        { method: mode.kind === "edit" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body }
      );
      if (!response.ok) {
        const problem = await response.json().catch(() => null) as { message?: string; errors?: Record<string, string[]> } | null;
        const detail = problem?.message ?? (problem?.errors ? Object.values(problem.errors).flat()[0] : null);
        throw new Error(detail ?? "Acquisition rule could not be saved.");
      }
      const saved = await response.json() as ReleaseProfileItem;
      const settled = formFrom(saved);
      setForm((current) => settleFormAfterSave(current, form, settled));
      setInitialForm(settled);
      setMode({ kind: "edit", id: saved.id });
      setSaveState("saved");
      setSaveMessage("Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    }
  }

  async function remove() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/release-profiles/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error(await readErrorText(response, "Acquisition rule could not be removed."));
      toast.success(`${editing?.name ?? "Acquisition rule"} removed`);
      setConfirmRemove(false);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Acquisition rule could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar tabs={TABS} actions={<PageToolbarAction onClick={() => openProfile(null)}>New acquisition rule</PageToolbarAction>} />

      <ProperRepackCard settings={settings} />

      <RuleModeCard mode={ruleMode} onChanged={() => revalidator.revalidate()} />

      <ListCard
        title={profiles.length ? `${profiles.length} ${profiles.length === 1 ? "rule" : "rules"}` : "0 rules"}
        titleCase={false}
      >
        {profiles.length === 0 ? (
          <ListEmpty
            title="No acquisition rules yet"
            description="Add a rule for every title, or attach one to one or more title tags. Deluno applies it before automatic and interactive searches can dispatch a release."
            actions={<Button type="button" size="sm" onClick={() => openProfile(null)}>New acquisition rule</Button>}
          />
        ) : (
          <ListTable
            listId="release-rules"
            items={sorted}
            columns={[
              // First-match order is the priority order, so the rows are never re-sorted there; the columns still move.
              { id: "rule", label: "Rule", width: "minmax(0,1.45fr)", sort: firstMatch ? undefined : (profile) => profile.name.toLowerCase() },
              { id: "appliesTo", label: "Applies to", width: "minmax(0,1fr)", sort: firstMatch ? undefined : (profile) => joinTagList(tagsOf(profile)).toLowerCase() },
              { id: "protocol", label: "Protocol", width: "130px", sort: firstMatch ? undefined : (profile) => protocolLabel(profile.preferredProtocol) },
              { id: "timing", label: "Timing", width: "minmax(0,1.2fr)", sort: firstMatch ? undefined : (profile) => formatTiming(profile).toLowerCase() },
              { id: "terms", label: "Terms", width: "minmax(0,1.25fr)", sort: firstMatch ? undefined : (profile) => profile.preferredTerms.length },
              ...(firstMatch ? [{ id: "order", label: "Order", width: "96px", movable: false, sortable: false }] : [])
            ]}
          >
            {(rows) => rows.map((profile) => {
              const tags = tagsOf(profile);
              const orderIndex = orderable.findIndex((item) => item.id === profile.id);
              return (
                <ListRow
                  key={profile.id}
                  onClick={() => openProfile(profile)}
                  selected={mode.kind === "edit" && mode.id === profile.id}
                  draggable={firstMatch && tags.length > 0}
                  onDragStart={() => setDragId(profile.id)}
                  onDragOver={(event) => { if (dragId && tags.length > 0) event.preventDefault(); }}
                  onDrop={(event) => {
                    event.preventDefault();
                    if (dragId && orderIndex >= 0) move(dragId, orderIndex);
                    setDragId(null);
                  }}
                  onDragEnd={() => setDragId(null)}
                >
                  <ListNameCell name={profile.name} sub={profile.enabled ? (tags.length ? (firstMatch ? `Rule ${orderIndex + 1}` : "Tag rule") : (firstMatch ? "Default rule" : "Rule for every title")) : "Disabled"} />
                  <ListCell primary={tags.length ? joinTagList(tags) : firstMatch ? "Titles no other rule matches" : "Every title"} secondary={indexerScopeLabel(profile, indexers)} />
                  <ListCell primary={protocolLabel(profile.preferredProtocol)} />
                  <ListCell primary={formatTiming(profile)} secondary={formatAvailability(profile)} />
                  <ListCell primary={termSummary(profile)} secondary={profile.preferredTerms.length ? `${profile.preferredTerms.length} weighted preference${profile.preferredTerms.length === 1 ? "" : "s"}` : undefined} />
                  {firstMatch ? (
                    <div role="cell" className="flex items-center gap-1" onClick={(event) => event.stopPropagation()}>
                      {tags.length > 0 ? (
                        <>
                          <Button type="button" variant="ghost" size="icon" aria-label={`Move ${profile.name} up`} disabled={orderIndex <= 0} onClick={() => move(profile.id, orderIndex - 1)}>
                            <ArrowUp className="h-4 w-4" />
                          </Button>
                          <Button type="button" variant="ghost" size="icon" aria-label={`Move ${profile.name} down`} disabled={orderIndex >= orderable.length - 1} onClick={() => move(profile.id, orderIndex + 1)}>
                            <ArrowDown className="h-4 w-4" />
                          </Button>
                        </>
                      ) : null}
                    </div>
                  ) : null}
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={open}
        onOpenChange={(nextOpen) => {
          if (!nextOpen) requestClose();
        }}
        title={mode.kind === "create" ? "New acquisition rule" : editing?.name ?? form.name}
        description={mode.kind === "create" ? "Set the conditions a release must meet before dispatch." : "Release timing and term policy"}
        onSubmit={submit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create rule" : "Save rule"} onCancel={requestClose} saveEnabled={mode.kind === "create" ? true : undefined} disabled={busy} />}
      >
        <DrawerSection title="Rule details">
          <FieldRow>
            <Field label="Name" error={errors.name}>
              <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} placeholder="Global release timing" autoComplete="off" />
            </Field>
            <Field label="Title tags" optional error={errors.tags} help="Pick one or more. A title carrying any one of them uses this rule. Leave blank for the rule that covers every title. A tag can be in one rule only.">
              <TagPicker label="Title tags" value={form.tags} onChange={(tags) => setForm((current) => ({ ...current, tags }))} />
            </Field>
          </FieldRow>
          <Field label="Preferred protocol" help="A matching protocol gets a ranking lift; the rule does not exclude the other protocol unless its delay or terms hold it.">
            <Select value={form.preferredProtocol} onChange={(event) => setForm((current) => ({ ...current, preferredProtocol: event.target.value }))} options={[{ value: "any", label: "Any protocol" }, { value: "usenet", label: "Usenet first" }, { value: "torrent", label: "Torrent first" }]} />
          </Field>
          <SwitchRow
            label="Rule enabled"
            description="Off: this rule is kept but skipped entirely, as if it did not exist. On: it applies to every matching search."
            checked={form.enabled}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, enabled: checked }))}
          />
          <Field label="Indexers" optional help="Leave every indexer unchecked to apply this rule everywhere. Check one or more to scope it to just those indexers.">
            {indexers.length === 0 ? (
              <p className="text-(length:--type-body-sm) text-muted-foreground">No indexers configured yet.</p>
            ) : (
              <div className="grid gap-1.5">
                {indexers.map((indexer) => {
                  const id = `release-rule-indexer-${indexer.id}`;
                  const checked = form.indexerIds.includes(indexer.id);
                  return (
                    <label key={indexer.id} htmlFor={id} className="flex cursor-pointer items-center gap-2 text-(length:--type-body-sm) text-foreground">
                      <Checkbox
                        id={id}
                        checked={checked}
                        onCheckedChange={(nextChecked) => setForm((current) => ({
                          ...current,
                          indexerIds: nextChecked
                            ? [...current.indexerIds, indexer.id]
                            : current.indexerIds.filter((existingId) => existingId !== indexer.id)
                        }))}
                      />
                      {indexer.name}
                    </label>
                  );
                })}
              </div>
            )}
          </Field>
        </DrawerSection>

        <DrawerSection title="Protocol timing">
          <FieldRow>
            <Field label="Usenet delay" error={errors.usenetDelayMinutes} help="Hold matching results for this many minutes after they appear.">
              <div className="flex items-center gap-2"><Input type="number" min="0" step="1" value={form.usenetDelayMinutes} onChange={(event) => setForm((current) => ({ ...current, usenetDelayMinutes: event.target.value }))} placeholder="0" /><span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">min</span></div>
            </Field>
            <Field label="Torrent delay" error={errors.torrentDelayMinutes} help="Hold matching results for this many minutes after they appear.">
              <div className="flex items-center gap-2"><Input type="number" min="0" step="1" value={form.torrentDelayMinutes} onChange={(event) => setForm((current) => ({ ...current, torrentDelayMinutes: event.target.value }))} placeholder="0" /><span className="shrink-0 text-(length:--type-body-sm) text-muted-foreground">min</span></div>
            </Field>
          </FieldRow>
          <p className="text-(length:--type-caption) text-muted-foreground">The largest applicable delay wins. A held result stays visible with the reason and becomes eligible when the timing window clears.</p>
          <SwitchRow
            label="Skip the delay for the best quality"
            description="Off by default. On: a release that is already the best quality its title's quality profile allows is grabbed right away over the preferred protocol, instead of waiting out the delay above."
            checked={form.bypassIfHighestQuality}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, bypassIfHighestQuality: checked }))}
          />
        </DrawerSection>

        <DrawerSection title="Release terms">
          <Field label="Must contain" optional error={errors.mustContain} help="Comma- or newline-separated terms. A release without one is rejected. To match a pattern, put it between slashes, like /2160p|4k/i; the i ignores capital letters.">
            <Textarea value={form.mustContain} onChange={(event) => setForm((current) => ({ ...current, mustContain: event.target.value }))} placeholder="Proper, Remux, /2160p|4k/i" rows={2} />
          </Field>
          <Field label="Must not contain" optional error={errors.mustNotContain} help="Comma- or newline-separated terms. A release with one is rejected. Patterns between slashes work here too, like /\b(HC|KORSUB)\b/i.">
            <Textarea value={form.mustNotContain} onChange={(event) => setForm((current) => ({ ...current, mustNotContain: event.target.value }))} placeholder="CAM, screener, /\bHC\b/i" rows={2} />
          </Field>
          <Disclosure
            title="Advanced legacy term weights"
            summary="Optional compatibility input for release-term preferences"
            open={legacyWeightsOpen}
            onOpenChange={setLegacyWeightsOpen}
          >
            <Field label="Preferred terms" optional error={errors.preferredTerms} help="One per line as term = legacy preference weight. Positive values prefer a term; negative values avoid it. These weights are retained for compatibility and are not the typed quality decision value.">
              <Textarea value={form.preferredTerms} onChange={(event) => setForm((current) => ({ ...current, preferredTerms: event.target.value }))} placeholder={"Remux = 100\nscene-group = -25"} rows={3} />
            </Field>
          </Disclosure>
        </DrawerSection>

        {editing ? (
          <DrawerSection>
            <DrawerDanger title="Delete this acquisition rule" description="Titles will immediately stop using this rule. Existing downloads are not changed." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Delete “${editing?.name ?? form.name}”?`}
        description="This removes the rule from future searches. Existing downloads are not changed."
        confirmLabel="Delete rule"
        busy={busy}
        onConfirm={() => void remove()}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(nextOpen) => {
          if (!nextOpen) setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this acquisition rule have not been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

function emptyForm(): ProfileForm {
  return { name: "", tags: "", preferredProtocol: "any", usenetDelayMinutes: "", torrentDelayMinutes: "", mustContain: "", mustNotContain: "", preferredTerms: "", enabled: true, indexerIds: [], bypassIfHighestQuality: false };
}

function formFrom(profile: ReleaseProfileItem): ProfileForm {
  return {
    name: profile.name,
    tags: joinTagList(tagsOf(profile)),
    preferredProtocol: profile.preferredProtocol || "any",
    usenetDelayMinutes: String(profile.usenetDelayMinutes || ""),
    torrentDelayMinutes: String(profile.torrentDelayMinutes || ""),
    mustContain: profile.mustContain,
    mustNotContain: profile.mustNotContain,
    preferredTerms: profile.preferredTerms.map((term) => `${term.term} = ${term.score}`).join("\n"),
    enabled: profile.enabled,
    indexerIds: profile.indexerIds,
    bypassIfHighestQuality: profile.bypassIfHighestQuality
  };
}

function sameForm(left: ProfileForm, right: ProfileForm) {
  return left.name === right.name && left.tags === right.tags && left.preferredProtocol === right.preferredProtocol && left.usenetDelayMinutes === right.usenetDelayMinutes && left.torrentDelayMinutes === right.torrentDelayMinutes && left.mustContain === right.mustContain && left.mustNotContain === right.mustNotContain && left.preferredTerms === right.preferredTerms && left.enabled === right.enabled && left.bypassIfHighestQuality === right.bypassIfHighestQuality && sameIds(left.indexerIds, right.indexerIds);
}

/** The tags a rule applies to; older rows carry only the single tag name. */
function tagsOf(profile: ReleaseProfileItem): string[] {
  if (profile.tagNames?.length) return profile.tagNames;
  return profile.tagName?.trim() ? [profile.tagName.trim()] : [];
}

function sameIds(left: string[], right: string[]) {
  return left.length === right.length && left.every((id) => right.includes(id));
}

function validate(form: ProfileForm): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!form.name.trim()) errors.name = "Give this rule a name.";
  for (const [key, label] of [["usenetDelayMinutes", "Usenet delay"], ["torrentDelayMinutes", "Torrent delay"]] as const) {
    const value = form[key].trim();
    if (value && (!/^\d+$/.test(value) || Number(value) > 525600)) errors[key] = `${label} must be a whole number from 0 to 525600.`;
  }
  if (form.mustContain.length > 2000) errors.mustContain = "Must-contain terms must be 2,000 characters or fewer.";
  if (form.mustNotContain.length > 2000) errors.mustNotContain = "Must-not-contain terms must be 2,000 characters or fewer.";
  try {
    parsePreferredTerms(form.preferredTerms);
  } catch (error) {
    errors.preferredTerms = error instanceof Error ? error.message : "Check the preferred term format.";
  }
  return errors;
}

function parsePreferredTerms(value: string): ReleaseTermScore[] {
  const terms: ReleaseTermScore[] = [];
  for (const [index, raw] of value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean).entries()) {
    const match = raw.match(/^(.+?)\s*(?:=|:)\s*(-?\d+)$/);
    if (!match || !match[1]?.trim()) throw new Error(`Preferred term ${index + 1} must look like “term = score”.`);
    const score = Number(match[2]);
    if (score < -10000 || score > 10000) throw new Error(`Preferred term ${index + 1} score must be between -10000 and 10000.`);
    terms.push({ term: match[1].trim(), score });
  }
  return terms;
}

function indexerScopeLabel(profile: ReleaseProfileItem, indexers: IndexerItem[]) {
  if (profile.indexerIds.length === 0) return "Every indexer";
  const names = profile.indexerIds
    .map((id) => indexers.find((indexer) => indexer.id === id)?.name ?? null)
    .filter((name): name is string => Boolean(name));
  return names.length ? names.join(", ") : `${profile.indexerIds.length} indexer${profile.indexerIds.length === 1 ? "" : "s"}`;
}

function protocolLabel(value: string) {
  return value === "usenet" ? "Usenet" : value === "torrent" ? "Torrent" : "Any";
}

function formatTiming(profile: ReleaseProfileItem) {
  const usenet = profile.usenetDelayMinutes ? `${profile.usenetDelayMinutes}m Usenet` : "Usenet now";
  const torrent = profile.torrentDelayMinutes ? `${profile.torrentDelayMinutes}m torrent` : "torrent now";
  return `${usenet} · ${torrent}`;
}

function formatAvailability(profile: ReleaseProfileItem) {
  const protocol = profile.preferredProtocol === "any" ? "Both protocols considered" : `${protocolLabel(profile.preferredProtocol)} receives the preference`;
  return profile.bypassIfHighestQuality ? `${protocol} · best quality skips the delay` : protocol;
}

function termSummary(profile: ReleaseProfileItem) {
  const must = profile.mustContain ? "Must contain" : profile.mustNotContain ? "Must not contain" : "No hard terms";
  const hardCount = [profile.mustContain, profile.mustNotContain].filter(Boolean).length;
  return hardCount ? `${must} · ${hardCount} hard rule${hardCount === 1 ? "" : "s"}` : "No hard terms";
}
