/**
 * Acquisition Rules — tag-aware release timing and term rules.
 *
 * A profile is deliberately smaller than a Quality Profile: it answers the
 * question "when should this release be allowed?" and can be global or apply
 * to one reusable title tag. The planner applies these rules before a result
 * can reach a download client.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { Textarea } from "../components/ui/textarea";
import { configurationNavAreas } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { authedFetch } from "../lib/use-auth";
import { fetchJson, type ReleaseProfileItem, type ReleaseTermScore } from "../lib/api";

const TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

interface LoaderData {
  profiles: ReleaseProfileItem[];
}

interface ProfileForm {
  name: string;
  tagName: string;
  preferredProtocol: string;
  usenetDelayMinutes: string;
  torrentDelayMinutes: string;
  mustContain: string;
  mustNotContain: string;
  preferredTerms: string;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsReleaseRulesLoader(): Promise<LoaderData> {
  return { profiles: await fetchJson<ReleaseProfileItem[]>("/api/release-profiles") };
}

export function SettingsReleaseRulesPage() {
  const { profiles } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
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
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  const sorted = useMemo(
    () => [...profiles].sort((left, right) => left.tagName.localeCompare(right.tagName) || left.name.localeCompare(right.name)),
    [profiles]
  );

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
        tagName: form.tagName.trim(),
        preferredProtocol: form.preferredProtocol,
        usenetDelayMinutes: Number(form.usenetDelayMinutes || 0),
        torrentDelayMinutes: Number(form.torrentDelayMinutes || 0),
        mustContain: form.mustContain.trim(),
        mustNotContain: form.mustNotContain.trim(),
        preferredTerms: parsePreferredTerms(form.preferredTerms)
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
      setForm(settled);
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
      if (!response.ok && response.status !== 204) throw new Error("Acquisition rule could not be removed.");
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
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={TABS} actions={<PageToolbarAction onClick={() => openProfile(null)}>New acquisition rule</PageToolbarAction>} />

      <ListCard
        title="Acquisition Rules"
        count={profiles.length ? `${profiles.length} ${profiles.length === 1 ? "rule" : "rules"}` : undefined}
      >
        {profiles.length === 0 ? (
          <ListEmpty
            title="No acquisition rules yet"
            description="Add a global rule or attach one to a title tag. Deluno will apply it before automatic and interactive searches can dispatch a release."
            actions={<Button type="button" size="sm" onClick={() => openProfile(null)}>New acquisition rule</Button>}
          />
        ) : (
          <ListTable
            columns={[
              { label: "Rule", width: "minmax(0,1.45fr)" },
              { label: "Applies to", width: "minmax(0,1fr)" },
              { label: "Protocol", width: "130px" },
              { label: "Timing", width: "minmax(0,1.2fr)" },
              { label: "Terms", width: "minmax(0,1.25fr)" }
            ]}
          >
            {sorted.map((profile) => (
              <ListRow key={profile.id} onClick={() => openProfile(profile)} selected={mode.kind === "edit" && mode.id === profile.id}>
                <ListNameCell name={profile.name} sub={profile.tagName ? "Tag profile" : "Global profile"} />
                <ListCell primary={profile.tagName || "Every title"} secondary={profile.tagName ? "Matching title tag" : "No tag required"} />
                <ListCell primary={protocolLabel(profile.preferredProtocol)} />
                <ListCell primary={formatTiming(profile)} secondary={formatAvailability(profile)} />
                <ListCell primary={termSummary(profile)} secondary={profile.preferredTerms.length ? `${profile.preferredTerms.length} weighted preference${profile.preferredTerms.length === 1 ? "" : "s"}` : undefined} />
              </ListRow>
            ))}
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
            <Field label="Title tag" optional error={errors.tagName} help="Leave blank to apply globally. Otherwise this must match a tag on the title.">
              <Input value={form.tagName} onChange={(event) => setForm((current) => ({ ...current, tagName: event.target.value }))} placeholder="Kids" autoComplete="off" />
            </Field>
          </FieldRow>
          <Field label="Preferred protocol" help="A matching protocol gets a ranking lift; the rule does not exclude the other protocol unless its delay or terms hold it.">
            <Select value={form.preferredProtocol} onChange={(event) => setForm((current) => ({ ...current, preferredProtocol: event.target.value }))} options={[{ value: "any", label: "Any protocol" }, { value: "usenet", label: "Usenet first" }, { value: "torrent", label: "Torrent first" }]} />
          </Field>
        </DrawerSection>

        <DrawerSection title="Protocol timing">
          <FieldRow>
            <Field label="Usenet delay" error={errors.usenetDelayMinutes} help="Hold matching results for this many minutes after they appear.">
              <div className="flex items-center gap-2"><Input type="number" min="0" step="1" value={form.usenetDelayMinutes} onChange={(event) => setForm((current) => ({ ...current, usenetDelayMinutes: event.target.value }))} placeholder="0" /><span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">min</span></div>
            </Field>
            <Field label="Torrent delay" error={errors.torrentDelayMinutes} help="Hold matching results for this many minutes after they appear.">
              <div className="flex items-center gap-2"><Input type="number" min="0" step="1" value={form.torrentDelayMinutes} onChange={(event) => setForm((current) => ({ ...current, torrentDelayMinutes: event.target.value }))} placeholder="0" /><span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">min</span></div>
            </Field>
          </FieldRow>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">The largest applicable delay wins. A held result stays visible with the reason and becomes eligible when the timing window clears.</p>
        </DrawerSection>

        <DrawerSection title="Release terms">
          <Field label="Must contain" optional error={errors.mustContain} help="Comma- or newline-separated terms. A release without one is rejected.">
            <Textarea value={form.mustContain} onChange={(event) => setForm((current) => ({ ...current, mustContain: event.target.value }))} placeholder="Proper, Remux" rows={2} />
          </Field>
          <Field label="Must not contain" optional error={errors.mustNotContain} help="Comma- or newline-separated terms. A release with one is rejected.">
            <Textarea value={form.mustNotContain} onChange={(event) => setForm((current) => ({ ...current, mustNotContain: event.target.value }))} placeholder="CAM, screener" rows={2} />
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
  return { name: "", tagName: "", preferredProtocol: "any", usenetDelayMinutes: "", torrentDelayMinutes: "", mustContain: "", mustNotContain: "", preferredTerms: "" };
}

function formFrom(profile: ReleaseProfileItem): ProfileForm {
  return {
    name: profile.name,
    tagName: profile.tagName,
    preferredProtocol: profile.preferredProtocol || "any",
    usenetDelayMinutes: String(profile.usenetDelayMinutes || ""),
    torrentDelayMinutes: String(profile.torrentDelayMinutes || ""),
    mustContain: profile.mustContain,
    mustNotContain: profile.mustNotContain,
    preferredTerms: profile.preferredTerms.map((term) => `${term.term} = ${term.score}`).join("\n")
  };
}

function sameForm(left: ProfileForm, right: ProfileForm) {
  return left.name === right.name && left.tagName === right.tagName && left.preferredProtocol === right.preferredProtocol && left.usenetDelayMinutes === right.usenetDelayMinutes && left.torrentDelayMinutes === right.torrentDelayMinutes && left.mustContain === right.mustContain && left.mustNotContain === right.mustNotContain && left.preferredTerms === right.preferredTerms;
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

function protocolLabel(value: string) {
  return value === "usenet" ? "Usenet" : value === "torrent" ? "Torrent" : "Any";
}

function formatTiming(profile: ReleaseProfileItem) {
  const usenet = profile.usenetDelayMinutes ? `${profile.usenetDelayMinutes}m Usenet` : "Usenet now";
  const torrent = profile.torrentDelayMinutes ? `${profile.torrentDelayMinutes}m torrent` : "torrent now";
  return `${usenet} · ${torrent}`;
}

function formatAvailability(profile: ReleaseProfileItem) {
  return profile.preferredProtocol === "any" ? "Both protocols considered" : `${protocolLabel(profile.preferredProtocol)} receives the preference`;
}

function termSummary(profile: ReleaseProfileItem) {
  const must = profile.mustContain ? "Must contain" : profile.mustNotContain ? "Must not contain" : "No hard terms";
  const hardCount = [profile.mustContain, profile.mustNotContain].filter(Boolean).length;
  return hardCount ? `${must} · ${hardCount} hard rule${hardCount === 1 ? "" : "s"}` : "No hard terms";
}
