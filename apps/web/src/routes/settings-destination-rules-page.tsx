/**
 * Final destinations — list → drawer.
 *
 *   PageToolbar (Media Management tabs · Test a title · New rule)
 *   ListCard  (name · when · goes to · order · status · on · ›)
 *   Rule drawer (Rule details · Destination · Status · Remove) · Test drawer (routing preview)
 *
 * Contracts: GET/POST /api/destination-rules, PUT/DELETE /api/destination-rules/{id},
 * POST /api/filesystem/import/preview.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { FlaskConical, Plus } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { LibraryImpactLinks } from "../components/ui/library-impact";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { PresetField } from "../components/ui/preset-field";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { fetchJson, type DestinationRuleItem, type ImportPreviewResponse, type LibraryItem, type PlatformSettingsSnapshot, type PolicySetItem, type TagItem } from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";

interface LoaderData {
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  destinationRules: DestinationRuleItem[];
  policySets: PolicySetItem[];
  tags: TagItem[];
}

interface RuleForm {
  name: string;
  mediaType: "movies" | "tv";
  matchKind: string;
  matchValue: string;
  rootPath: string;
  folderTemplate: string;
  priority: string;
  isEnabled: boolean;
}

interface TestForm {
  mediaType: "movies" | "tv";
  title: string;
  year: string;
  sourcePath: string;
  fileName: string;
  genres: string;
  tags: string;
  studio: string;
  originalLanguage: string;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string } | { kind: "test" };

const MATCH_KIND_LABELS: Record<string, string> = {
  genre: "Genre",
  tag: "Tag",
  language: "Original language",
  quality: "Quality",
  anime: "Anime",
  certification: "Certification",
  library: "Library"
};

const MATCH_VALUE_OPTIONS: Record<string, { label: string; value: string }[]> = {
  genre: ["Action", "Animation", "Anime", "Comedy", "Documentary", "Drama", "Family", "Horror", "Sci-Fi"].map((value) => ({ label: value, value })),
  language: [
    { label: "English", value: "en" },
    { label: "Japanese", value: "ja" },
    { label: "Korean", value: "ko" },
    { label: "French", value: "fr" },
    { label: "German", value: "de" },
    { label: "Spanish", value: "es" }
  ],
  quality: [
    { label: "4K / UHD", value: "4K" },
    { label: "1080p", value: "1080p" },
    { label: "720p", value: "720p" },
    { label: "WEB-DL", value: "WEB-DL" },
    { label: "Bluray", value: "Bluray" }
  ],
  certification: ["G", "PG", "PG-13", "R", "TV-MA"].map((value) => ({ label: value, value })),
  anime: [
    { label: "Anime", value: "true" },
    { label: "Not anime", value: "false" }
  ],
  library: [
    { label: "Movies", value: "movies" },
    { label: "TV", value: "tv" }
  ]
};

const PRIORITY_OPTIONS = [
  { label: "First (10)", value: "10" },
  { label: "Early (50)", value: "50" },
  { label: "Normal (100)", value: "100" },
  { label: "Late (500)", value: "500" }
];

export async function settingsDestinationRulesLoader(): Promise<LoaderData> {
  const [overview, destinationRules, tags] = await Promise.all([settingsOverviewLoader(), fetchJson<DestinationRuleItem[]>("/api/destination-rules"), fetchJson<TagItem[]>("/api/tags")]);
  return { libraries: overview.libraries, settings: overview.settings, destinationRules, policySets: overview.policySets, tags };
}

export function SettingsDestinationRulesPage() {
  const { destinationRules, tags, settings, libraries, policySets } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const sorted = useMemo(() => [...destinationRules].sort((a, b) => a.priority - b.priority || a.name.localeCompare(b.name)), [destinationRules]);
  const tagOptions = useMemo(() => tags.map((tag) => ({ label: tag.name, value: tag.name })), [tags]);
  const librariesByRule = useMemo(() => {
    const map = new Map<string, LibraryItem[]>();
    for (const library of libraries) {
      const plan = library.defaultPolicySetId ? policySets.find((candidate) => candidate.id === library.defaultPolicySetId) : null;
      if (!plan?.destinationRuleId) continue;
      map.set(plan.destinationRuleId, [...(map.get(plan.destinationRuleId) ?? []), library]);
    }
    return map;
  }, [libraries, policySets]);
  const [togglingId, setTogglingId] = useState<string | null>(null);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<RuleForm>(() => emptyForm(settings));
  const [initialForm, setInitialForm] = useState<RuleForm>(() => emptyForm(settings));
  const [testForm, setTestForm] = useState<TestForm>(() => emptyTestForm(settings));
  const [testResult, setTestResult] = useState<ImportPreviewResponse | null>(null);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);

  const isRuleDrawer = mode.kind === "create" || mode.kind === "edit";
  const editing = mode.kind === "edit" ? destinationRules.find((rule) => rule.id === mode.id) ?? null : null;
  const dirty = isRuleDrawer && !sameForm(form, initialForm);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  function openRule(rule: DestinationRuleItem | null) {
    const next = rule ? formFrom(rule) : emptyForm(settings);
    setMode(rule ? { kind: "edit", id: rule.id } : { kind: "create" });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
    setErrors({});
  }
  function openTest() {
    setMode({ kind: "test" });
    setTestResult(null);
    setSaveState(undefined);
  }
  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    if (mode.kind === "test") {
      await runTest();
      return;
    }
    if (!isRuleDrawer) return;
    const nextErrors: Record<string, string> = {};
    if (!form.name.trim()) nextErrors.name = "Give this rule a name.";
    if (!form.matchValue.trim()) nextErrors.matchValue = "Choose what the rule matches.";
    if (!form.rootPath.trim()) nextErrors.rootPath = "Choose the destination folder.";
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) return;

    setBusy(true);
    setSaveState("saving");
    try {
      const payload = { name: form.name.trim(), mediaType: form.mediaType, matchKind: form.matchKind, matchValue: form.matchValue.trim(), rootPath: form.rootPath.trim(), folderTemplate: form.folderTemplate.trim() || null, priority: Number(form.priority || 100), isEnabled: form.isEnabled };
      const response = await authedFetch(mode.kind === "edit" ? `/api/destination-rules/${mode.id}` : "/api/destination-rules", { method: mode.kind === "edit" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      if (!response.ok) throw new Error(mode.kind === "edit" ? "Destination rule could not be saved." : "Destination rule could not be created.");
      if (mode.kind === "create") {
        const created = (await response.json()) as DestinationRuleItem;
        setMode({ kind: "edit", id: created.id });
        setSaveMessage("Rule created");
      } else {
        setSaveMessage("Saved just now");
      }
      const settled = { ...form, name: payload.name, matchValue: payload.matchValue, rootPath: payload.rootPath, priority: String(payload.priority) };
      setForm(settled);
      setInitialForm(settled);
      setSaveState("saved");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(false);
    }
  }

  async function runTest() {
    setBusy(true);
    setSaveState("saving");
    try {
      const result = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          mediaType: testForm.mediaType,
          title: testForm.title,
          year: testForm.year ? Number(testForm.year) : null,
          sourcePath: testForm.sourcePath,
          fileName: testForm.fileName,
          genres: splitValues(testForm.genres),
          tags: splitValues(testForm.tags),
          studio: testForm.studio,
          originalLanguage: testForm.originalLanguage
        })
      });
      setTestResult(result);
      setSaveState("saved");
      setSaveMessage(result.matchedRuleName ? `Matched “${result.matchedRuleName}”` : "No rule matched — library default");
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Preview failed");
    } finally {
      setBusy(false);
    }
  }

  async function handleRemove() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/destination-rules/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Destination rule could not be removed.");
      toast.success(`${editing?.name ?? "Rule"} removed`);
      setConfirmRemove(false);
      setInitialForm(form);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Destination rule could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  async function toggleEnabled(rule: DestinationRuleItem, isEnabled: boolean) {
    setTogglingId(rule.id);
    try {
      const response = await authedFetch(`/api/destination-rules/${rule.id}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...rule, isEnabled }) });
      if (!response.ok) throw new Error(`Could not ${isEnabled ? "enable" : "pause"} ${rule.name}.`);
      if (mode.kind === "edit" && mode.id === rule.id && !dirty) {
        const next = { ...form, isEnabled };
        setForm(next);
        setInitialForm(next);
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Destination rule could not be updated.");
    } finally {
      setTogglingId(null);
    }
  }

  /* ---------------------------------------------------------- render */
  const matchOptions = form.matchKind === "tag" ? tagOptions : MATCH_VALUE_OPTIONS[form.matchKind] ?? [];

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={librarySetupNavItems}
        actions={
          <>
            <Button type="button" variant="outline" onClick={openTest}>
              <FlaskConical className="h-4 w-4" />
              Test a title
            </Button>
            <PageToolbarAction onClick={() => openRule(null)}>New rule</PageToolbarAction>
          </>
        }
      />

      <ListCard title="Destination rules" count={destinationRules.length ? `${destinationRules.length} ${destinationRules.length === 1 ? "rule" : "rules"} · ${destinationRules.filter((rule) => rule.isEnabled).length} active · exceptions to the library folder` : undefined}>
        {destinationRules.length === 0 ? (
          <ListEmpty
            title="No destination rules"
            description="Your library folder is the default. Add a rule only for exceptions — a genre, tag, quality, or language that should land in a separate folder."
            actions={
              <Button type="button" size="sm" onClick={() => openRule(null)}>
                <Plus className="h-3.5 w-3.5" />
                New rule
              </Button>
            }
          />
        ) : (
          <ListTable columns={[{ label: "Name" }, { label: "Match" }, { label: "Goes to", width: "minmax(0,1.4fr)" }, { label: "Used by" }, { label: "Rule order", width: "90px", align: "end" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]}>
            {sorted.map((rule) => (
              <ListRow key={rule.id} onClick={() => openRule(rule)} selected={mode.kind === "edit" && mode.id === rule.id}>
                <ListNameCell name={rule.name} sub={rule.mediaType === "tv" ? "TV shows" : "Movies"} />
                <ListCell primary={`${MATCH_KIND_LABELS[rule.matchKind] ?? rule.matchKind} is ${rule.matchValue}`} />
                <ListCell mono primary={rule.rootPath} secondary={rule.folderTemplate ? <span className="font-mono">{rule.folderTemplate}</span> : "Standard folder naming"} />
                <ListCell primary={<LibraryImpactLinks libraries={librariesByRule.get(rule.id) ?? []} />} secondary={librariesByRule.has(rule.id) ? "Applied through a Library Profile" : "Library folder remains the default"} />
                <ListCell numeric align="end" primary={String(rule.priority)} />
                <ListCell mobile>
                  <Chip tone={rule.isEnabled ? "ok" : "muted"}>{rule.isEnabled ? "Active" : "Off"}</Chip>
                </ListCell>
                <ListCell mobile>
                  <Switch size="sm" aria-label={`${rule.isEnabled ? "Pause" : "Enable"} ${rule.name}`} checked={rule.isEnabled} disabled={togglingId === rule.id} onCheckedChange={(checked) => void toggleEnabled(rule, checked)} />
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={mode.kind !== "closed"}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={mode.kind === "test" ? "Test a title" : mode.kind === "create" ? "New destination rule" : editing?.name ?? form.name}
        description={mode.kind === "test" ? "See which rule a title would match and where it would land." : mode.kind === "create" ? "An exception to the library folder." : `${MATCH_KIND_LABELS[form.matchKind] ?? form.matchKind} is ${form.matchValue} → ${form.rootPath}`}
        onSubmit={handleSubmit}
        footer={
          mode.kind === "test" ? (
            <DrawerFooter state={saveState === "saving" ? "saving" : saveState === "error" ? "error" : "clean"} message={saveMessage} saveLabel="Run preview" onCancel={closeDrawer} disabled={busy} saveEnabled />
          ) : (
            <DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create rule" : "Save rule"} onCancel={requestClose} disabled={busy} />
          )
        }
      >
        {isRuleDrawer ? (
          <>
            <DrawerSection title="Rule details">
              <FieldRow>
                <Field label="Name" error={errors.name}>
                  <Input value={form.name} onChange={(event) => { setErrors((current) => ({ ...current, name: "" })); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Example: family films to another folder" autoComplete="off" />
                </Field>
                <Field label="Media type">
                  <SegmentedControl<"movies" | "tv"> value={form.mediaType} onValueChange={(mediaType) => setForm((current) => ({ ...current, mediaType, rootPath: current.rootPath === defaultRoot(current.mediaType, settings) ? defaultRoot(mediaType, settings) : current.rootPath }))} options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV shows" }]} />
                </Field>
              </FieldRow>
              <FieldRow>
                <Field label="Match">
                  <Select value={form.matchKind} onChange={(event) => setForm((current) => ({ ...current, matchKind: event.target.value, matchValue: "" }))} options={Object.entries(MATCH_KIND_LABELS).map(([value, label]) => ({ value, label }))} />
                </Field>
                <Field label="Value" error={errors.matchValue}>
                  <PresetField value={form.matchValue} onChange={(value) => { setErrors((current) => ({ ...current, matchValue: "" })); setForm((current) => ({ ...current, matchValue: value })); }} options={matchOptions} allowCustom={form.matchKind !== "tag" || tagOptions.length === 0} customLabel="Custom value" customPlaceholder={form.matchKind === "genre" ? "Genre name" : "Match value"} />
                </Field>
              </FieldRow>
            </DrawerSection>
            <DrawerSection title="Where matching titles go">
              <Field label="Destination folder" help="Titles matching this rule are imported here instead of the library folder." error={errors.rootPath}>
                <PathInput value={form.rootPath} onChange={(rootPath) => { setErrors((current) => ({ ...current, rootPath: "" })); setForm((current) => ({ ...current, rootPath })); }} browseTitle="Choose destination folder" />
              </Field>
              <FieldRow>
                <Field label="Folder name format" optional help="Optional. Leave blank to use the library's naming. Tokens: {Title}, {Year}, {Genre}, {Tag}.">
                  <Input value={form.folderTemplate} onChange={(event) => setForm((current) => ({ ...current, folderTemplate: event.target.value }))} placeholder="{Title} ({Year})" className="font-mono text-[length:var(--type-caption)]" />
                </Field>
                <Field label="Rule order" help="If more than one rule matches, the lower number runs first.">
                  <PresetField inputType="number" value={form.priority} onChange={(value) => setForm((current) => ({ ...current, priority: value }))} options={PRIORITY_OPTIONS} customLabel="Custom order" customPlaceholder="1–1000" />
                </Field>
              </FieldRow>
            </DrawerSection>
            <DrawerSection title="Rule status">
              <SwitchRow label="Use this rule" description="When enabled, matching titles use this destination before the library folder." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
            </DrawerSection>
            {editing ? (
              <DrawerSection title="Library impact" aside={librariesByRule.get(editing.id)?.length ? `${librariesByRule.get(editing.id)!.length} libraries` : "Not assigned"}>
                <p className="text-[length:var(--type-caption)] text-muted-foreground">This exception only applies to libraries whose Library Profile uses it.</p>
                <LibraryImpactLinks libraries={librariesByRule.get(editing.id) ?? []} emptyLabel="No Library Profile uses this rule yet." />
              </DrawerSection>
            ) : null}
            {editing ? (
              <DrawerSection>
                <DrawerDanger title="Delete this rule" description="Titles already imported stay where they are." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
              </DrawerSection>
            ) : null}
          </>
        ) : null}

        {mode.kind === "test" ? (
          <>
            <DrawerSection title="Title">
              <FieldRow>
                <Field label="Title">
                  <Input value={testForm.title} onChange={(event) => setTestForm((current) => ({ ...current, title: event.target.value }))} />
                </Field>
                <Field label="Year" optional>
                  <Input inputMode="numeric" value={testForm.year} onChange={(event) => setTestForm((current) => ({ ...current, year: event.target.value }))} />
                </Field>
              </FieldRow>
              <FieldRow>
                <Field label="Media type">
                  <SegmentedControl<"movies" | "tv"> value={testForm.mediaType} onValueChange={(mediaType) => setTestForm((current) => ({ ...current, mediaType }))} options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV shows" }]} />
                </Field>
                <Field label="Original language" optional>
                  <Input value={testForm.originalLanguage} onChange={(event) => setTestForm((current) => ({ ...current, originalLanguage: event.target.value }))} placeholder="en" />
                </Field>
              </FieldRow>
              <FieldRow>
                <Field label="Genres" optional help="Comma-separated.">
                  <Input value={testForm.genres} onChange={(event) => setTestForm((current) => ({ ...current, genres: event.target.value }))} />
                </Field>
                <Field label="Tags" optional help="Comma-separated.">
                  <Input value={testForm.tags} onChange={(event) => setTestForm((current) => ({ ...current, tags: event.target.value }))} />
                </Field>
              </FieldRow>
            </DrawerSection>
            <DrawerSection title="Source file">
              <Field label="Source path" help="A real path lets Deluno check whether the source and destination can share one set of file data through a single-copy link (hardlink).">
                <Input value={testForm.sourcePath} onChange={(event) => setTestForm((current) => ({ ...current, sourcePath: event.target.value }))} className="font-mono text-[length:var(--type-caption)]" />
              </Field>
              <Field label="File name" optional>
                <Input value={testForm.fileName} onChange={(event) => setTestForm((current) => ({ ...current, fileName: event.target.value }))} className="font-mono text-[length:var(--type-caption)]" />
              </Field>
            </DrawerSection>
            {testResult ? (
              <DrawerSection title="Result" aside={testResult.matchedRuleName ? `rule: ${testResult.matchedRuleName}` : "library default"}>
                <div className="flex flex-wrap gap-1.5">
                  <Chip tone={testResult.matchedRuleName ? "ok" : "muted"}>{testResult.matchedRuleName ? `Rule: ${testResult.matchedRuleName}` : "Default root"}</Chip>
                  <Chip tone={testResult.preferredTransferMode === "hardlink" ? "info" : "muted"}>{testResult.preferredTransferMode === "hardlink" ? "Single-copy link" : testResult.preferredTransferMode}</Chip>
                  <Chip tone={testResult.hardlinkAvailable ? "ok" : "warn"}>{testResult.hardlinkAvailable ? "Single-copy link available" : "Copy required"}</Chip>
                </div>
                <p className="text-[length:var(--type-body-sm)] text-muted-foreground">{testResult.explanation}</p>
                <dl className="grid grid-cols-[110px_1fr] gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
                  <dt className="text-muted-foreground">Source</dt>
                  <dd className="break-all font-mono text-[length:var(--type-caption)]">{testResult.sourcePath}</dd>
                  <dt className="text-muted-foreground">Destination</dt>
                  <dd className="break-all font-mono text-[length:var(--type-caption)]">{testResult.destinationPath}</dd>
                </dl>
                {testResult.decisionSteps.length ? (
                  <ol className="grid list-decimal gap-1 pl-5 text-[length:var(--type-caption)] text-muted-foreground">
                    {testResult.decisionSteps.map((step, index) => (
                      <li key={index}>{step}</li>
                    ))}
                  </ol>
                ) : null}
                {testResult.warnings.map((warning) => (
                  <p key={warning} className="text-[length:var(--type-caption)] text-warning">{warning}</p>
                ))}
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>

      <ConfirmDialog open={confirmRemove} onOpenChange={setConfirmRemove} title={`Delete “${editing?.name ?? form.name}”?`} description="Titles already imported stay where they are. New imports use the next matching rule or the library folder." confirmLabel="Delete rule" busy={busy} onConfirm={() => void handleRemove()} />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this rule haven't been saved."
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

function defaultRoot(mediaType: "movies" | "tv", settings: PlatformSettingsSnapshot) {
  return (mediaType === "movies" ? settings.movieRootPath : settings.seriesRootPath) ?? "";
}
function emptyForm(settings: PlatformSettingsSnapshot): RuleForm {
  return { name: "", mediaType: "movies", matchKind: "genre", matchValue: "", rootPath: defaultRoot("movies", settings), folderTemplate: "", priority: "100", isEnabled: true };
}
function formFrom(rule: DestinationRuleItem): RuleForm {
  return { name: rule.name, mediaType: rule.mediaType === "tv" ? "tv" : "movies", matchKind: rule.matchKind, matchValue: rule.matchValue, rootPath: rule.rootPath, folderTemplate: rule.folderTemplate ?? "", priority: String(rule.priority), isEnabled: rule.isEnabled };
}
function sameForm(a: RuleForm, b: RuleForm) {
  return (Object.keys(a) as (keyof RuleForm)[]).every((key) => a[key] === b[key]);
}
function emptyTestForm(settings: PlatformSettingsSnapshot): TestForm {
  return {
    mediaType: "movies",
    title: "Dune Part Two",
    year: "2024",
    sourcePath: settings.downloadsPath || "D:\\Downloads\\complete\\Dune.Part.Two.2024.2160p.WEB-DL.mkv",
    fileName: "Dune.Part.Two.2024.2160p.WEB-DL.mkv",
    genres: "Sci-Fi, Drama",
    tags: "4k",
    studio: "",
    originalLanguage: "en"
  };
}
function splitValues(value: string) {
  return value.split(",").map((item) => item.trim()).filter(Boolean);
}
