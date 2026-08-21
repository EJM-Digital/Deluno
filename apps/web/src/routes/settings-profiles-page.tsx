/**
 * Quality profiles — list → drawer.
 *
 *   PageToolbar (Media Plans tabs · New profile)
 *   ListCard  (name · allowed tiers · stops at · used by · status · ›)
 *   Drawer    (Start from · Basics · Quality tiers · Formats [Fine-tune] · Used by · Delete)
 *
 * Tier vocabulary comes from /api/quality-model — the same names Size rules and
 * the decision engine use — so a profile's allowed/cutoff values always resolve.
 *
 * Contracts: GET/POST /api/quality-profiles, PUT/DELETE /api/quality-profiles/{id},
 * POST /api/custom-formats (when a preset needs a format that doesn't exist yet).
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { ArrowDown, ArrowUp, Plus, X } from "lucide-react";
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
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { configurationNavAreas } from "../components/app/settings-shell";
import {
  fetchJson,
  readValidationProblem,
  type CustomFormatItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityModelSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { findBundledCF, QUALITY_PRESETS } from "../lib/trash-guide-data";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";

const TABS = configurationNavAreas.find((area) => area.label === "Media plans")?.items ?? [];

/**
 * Starters expressed in the backend's own tier names, each linked to the TRaSH
 * preset whose recommended custom formats we create on first save.
 */
const PROFILE_STARTERS: { id: string; label: string; mediaType: "movies" | "tv"; allowed: string[]; cutoff: string; summary: string; trashPresetId?: string }[] = [
  { id: "movies-1080p", label: "1080p streaming", mediaType: "movies", allowed: ["WEB 1080p", "Bluray 1080p", "Remux 1080p"], cutoff: "WEB 1080p", summary: "Great quality, small files. The most popular choice.", trashPresetId: "web-1080p" },
  { id: "movies-bluray", label: "1080p Blu-ray", mediaType: "movies", allowed: ["WEB 1080p", "Bluray 1080p", "Remux 1080p"], cutoff: "Bluray 1080p", summary: "Starts at WEB, upgrades to Blu-ray when available.", trashPresetId: "bluray-1080p" },
  { id: "movies-4k", label: "4K streaming", mediaType: "movies", allowed: ["WEB 2160p", "Bluray 2160p", "Remux 2160p"], cutoff: "WEB 2160p", summary: "4K from streaming platforms, without Remux file sizes.", trashPresetId: "web-2160p" },
  { id: "movies-remux", label: "4K Remux", mediaType: "movies", allowed: ["WEB 2160p", "Bluray 2160p", "Remux 2160p"], cutoff: "Remux 2160p", summary: "Uncompromising quality; very large files.", trashPresetId: "remux-2160p" },
  { id: "tv-1080p", label: "1080p TV", mediaType: "tv", allowed: ["WEB 720p", "WEB 1080p", "HDTV 1080p"], cutoff: "WEB 1080p", summary: "Everyday TV at 1080p with a 720p fallback.", trashPresetId: "web-1080p-tv" },
  { id: "tv-4k", label: "4K TV", mediaType: "tv", allowed: ["WEB 1080p", "WEB 2160p", "Bluray 2160p"], cutoff: "WEB 2160p", summary: "4K episodes where available, 1080p otherwise." },
  { id: "tv-anime", label: "Anime", mediaType: "tv", allowed: ["WEB 1080p", "Bluray 1080p", "Remux 1080p"], cutoff: "Bluray 1080p", summary: "Anime-focused sources and release groups.", trashPresetId: "anime-1080p" }
];

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  settings: PlatformSettingsSnapshot;
  qualityModel: QualityModelSnapshot;
}

interface ProfileForm {
  name: string;
  mediaType: "movies" | "tv";
  /** Allowed tiers in preference order, least → most preferred (matches storage). */
  allowed: string[];
  cutoff: string;
  customFormatIds: string[];
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsProfilesLoader(): Promise<LoaderData> {
  const [overview, customFormats, qualityModel] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchJson<QualityModelSnapshot>("/api/quality-model")
  ]);
  return { ...overview, customFormats, qualityModel };
}

export function SettingsProfilesPage() {
  const { libraries, qualityProfiles, customFormats, qualityModel } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();

  const tiers = useMemo(() => [...qualityModel.tiers].sort((a, b) => b.rank - a.rank), [qualityModel.tiers]);
  const sorted = useMemo(() => [...qualityProfiles].sort((a, b) => a.mediaType.localeCompare(b.mediaType) || a.name.localeCompare(b.name)), [qualityProfiles]);
  const split = useMediaTypeSplit(sorted, (profile) => profile.mediaType);

  const librariesByProfile = useMemo(() => {
    const map = new Map<string, LibraryItem[]>();
    for (const library of libraries) {
      if (!library.qualityProfileId) continue;
      map.set(library.qualityProfileId, [...(map.get(library.qualityProfileId) ?? []), library]);
    }
    return map;
  }, [libraries]);

  /* ---------------------------------------------------------- drawer */
  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<ProfileForm>(() => emptyForm());
  const [initialForm, setInitialForm] = useState<ProfileForm>(() => emptyForm());
  const [starterId, setStarterId] = useState("");
  const [addRecommended, setAddRecommended] = useState(true);
  const [fineTuneOpen, setFineTuneOpen] = useState(false);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [errors, setErrors] = useState<{ name?: string; allowed?: string; cutoff?: string }>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? qualityProfiles.find((profile) => profile.id === mode.id) ?? null : null;
  const dirty = useMemo(() => isOpen && !sameForm(form, initialForm), [isOpen, form, initialForm]);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  const blocker = useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  const availableFormats = useMemo(() => customFormats.filter((format) => format.mediaType === form.mediaType), [customFormats, form.mediaType]);
  const unusedTiers = useMemo(() => tiers.filter((tier) => !form.allowed.includes(tier.name)), [tiers, form.allowed]);
  /** Most-preferred first for display; storage keeps the raw order. */
  const allowedForDisplay = useMemo(() => [...form.allowed].reverse(), [form.allowed]);

  function openCreate() {
    const next = emptyForm();
    setMode({ kind: "create" });
    setForm(next);
    setInitialForm(next);
    setStarterId("");
    setFineTuneOpen(false);
    setSaveState(undefined);
    setErrors({});
  }
  function openEdit(profile: QualityProfileItem) {
    const next = formFrom(profile);
    setMode({ kind: "edit", id: profile.id });
    setForm(next);
    setInitialForm(next);
    setStarterId("");
    setFineTuneOpen(false);
    setSaveState(undefined);
    setErrors({});
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
    const starter = PROFILE_STARTERS.find((item) => item.id === id);
    if (!starter) {
      setForm(emptyForm());
      return;
    }
    setAddRecommended(true);
    setForm((current) => ({
      ...emptyForm(),
      name: current.name.trim() ? current.name : `${starter.mediaType === "tv" ? "TV Shows" : "Movies"} / ${starter.label}`,
      mediaType: starter.mediaType,
      allowed: starter.allowed.filter((name) => tiers.some((tier) => tier.name === name)),
      cutoff: starter.cutoff
    }));
  }

  function setMediaType(mediaType: "movies" | "tv") {
    if (mediaType === form.mediaType) return;
    setForm((current) => ({ ...current, mediaType, customFormatIds: [] }));
  }

  function addTier(name: string) {
    if (!name) return;
    setErrors((current) => ({ ...current, allowed: undefined }));
    setForm((current) => {
      // Keep storage order least → most preferred, by rank.
      const next = [...current.allowed, name].sort((a, b) => rankOf(tiers, a) - rankOf(tiers, b));
      return { ...current, allowed: next, cutoff: current.cutoff || name };
    });
  }
  function removeTier(name: string) {
    setForm((current) => {
      const allowed = current.allowed.filter((tier) => tier !== name);
      return { ...current, allowed, cutoff: current.cutoff === name ? allowed[allowed.length - 1] ?? "" : current.cutoff };
    });
  }
  /** Move within the displayed (most-preferred-first) order. */
  function moveTier(name: string, direction: -1 | 1) {
    setForm((current) => {
      const display = [...current.allowed].reverse();
      const index = display.indexOf(name);
      const target = index + direction;
      if (index < 0 || target < 0 || target >= display.length) return current;
      [display[index], display[target]] = [display[target]!, display[index]!];
      return { ...current, allowed: display.reverse() };
    });
  }
  function toggleFormat(id: string) {
    setForm((current) => ({ ...current, customFormatIds: current.customFormatIds.includes(id) ? current.customFormatIds.filter((item) => item !== id) : [...current.customFormatIds, id] }));
  }

  /* ---------------------------------------------------------- saving */
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    const nextErrors: typeof errors = {};
    if (!form.name.trim()) nextErrors.name = "Give this profile a name.";
    if (!form.allowed.length) nextErrors.allowed = "Allow at least one quality tier.";
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) return;

    setBusy(true);
    setSaveState("saving");
    try {
      const starterFormatIds = mode.kind === "create" && addRecommended ? await ensureRecommendedFormats(starterId, form.mediaType, customFormats) : [];
      const formatIds = [...new Set([...form.customFormatIds, ...starterFormatIds])];
      const payload = {
        name: form.name.trim(),
        mediaType: form.mediaType,
        cutoffQuality: form.cutoff || form.allowed[form.allowed.length - 1],
        allowedQualities: form.allowed.join(", "),
        customFormatIds: formatIds.join(", "),
        upgradeUntilCutoff: form.upgradeUntilCutoff,
        upgradeUnknownItems: form.upgradeUnknownItems
      };
      const response = await authedFetch(mode.kind === "edit" ? `/api/quality-profiles/${mode.id}` : "/api/quality-profiles", { method: mode.kind === "edit" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      if (!response.ok) {
        const problem = await readValidationProblem(response.clone());
        const validationMessage = problem?.errors?.allowedQualities?.[0] ?? problem?.errors?.cutoffQuality?.[0];
        if (problem?.errors?.allowedQualities?.[0] || problem?.errors?.cutoffQuality?.[0]) {
          setErrors((current) => ({
            ...current,
            allowed: problem.errors?.allowedQualities?.[0] ?? current.allowed,
            cutoff: problem.errors?.cutoffQuality?.[0] ?? current.cutoff
          }));
        }
        throw new Error(validationMessage ?? (mode.kind === "edit" ? "Profile could not be saved." : "Profile could not be created."));
      }
      const saved = (await response.json().catch(() => null)) as QualityProfileItem | null;
      if (mode.kind === "create" && saved) setMode({ kind: "edit", id: saved.id });
      const settled = saved ? formFrom(saved) : { ...form, name: payload.name };
      setForm(settled);
      setInitialForm(settled);
      setSaveState("saved");
      setSaveMessage(mode.kind === "create" ? "Profile created" : "Saved just now");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(false);
    }
  }

  async function handleRemove() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/quality-profiles/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("Profile could not be removed.");
      toast.success(`${editing?.name ?? "Profile"} removed`);
      setConfirmRemove(false);
      setInitialForm(form);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Profile could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  /* ---------------------------------------------------------- render */
  const usedBy = editing ? librariesByProfile.get(editing.id) ?? [] : [];
  const recommendedCount = recommendedFormatsFor(starterId).length;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={TABS}
        actions={
          <>
            <MediaTypeFilter value={split.scope} onValueChange={split.setScope} counts={split.counts} />
            <Button type="button" onClick={openCreate}>
              <Plus className="h-4 w-4" />
              New profile
            </Button>
          </>
        }
      />

      <ListCard title="Quality profiles" count={qualityProfiles.length ? `${qualityProfiles.length} ${qualityProfiles.length === 1 ? "profile" : "profiles"}` : undefined}>
        {qualityProfiles.length === 0 ? (
          <ListEmpty
            title="No quality profiles yet"
            description="A profile is the quality ladder a media plan follows: which release tiers are allowed, and where upgrades stop."
            actions={
              <Button type="button" size="sm" onClick={openCreate}>
                <Plus className="h-3.5 w-3.5" />
                New profile
              </Button>
            }
          />
        ) : (
          <ListTable columns={[{ label: "Name" }, { label: "Allowed", width: "minmax(0,1.4fr)" }, { label: "Stops at" }, { label: "Used by" }, { label: "Formats", width: LIST_TRACK.status, mobile: true }]}>
            {split.groups.flatMap((group) => [
              split.showGroups && split.scope === "all" ? <ListGroupHeader key={group.key} label={group.label} count={group.items.length} /> : null,
              ...group.items.map((profile) => {
              const allowed = splitCsv(profile.allowedQualities);
              const formats = splitCsv(profile.customFormatIds);
              const used = librariesByProfile.get(profile.id) ?? [];
              const unknownTiers = allowed.filter((name) => !tiers.some((tier) => tier.name === name));
              return (
                <ListRow key={profile.id} onClick={() => openEdit(profile)} selected={mode.kind === "edit" && mode.id === profile.id}>
                  <ListNameCell name={profile.name} sub={profile.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell primary={[...allowed].reverse().join(" → ") || <span className="text-muted-foreground">None</span>} secondary={unknownTiers.length ? `${unknownTiers.length} tier${unknownTiers.length === 1 ? "" : "s"} not in the quality model` : `${allowed.length} tier${allowed.length === 1 ? "" : "s"}`} />
                  <ListCell primary={profile.cutoffQuality} secondary={profile.upgradeUntilCutoff ? "Upgrades until this tier" : "No upgrades"} />
                  <ListCell numeric primary={used.length ? `${used.length} ${used.length === 1 ? "library" : "libraries"}` : <span className="text-muted-foreground">Not assigned</span>} secondary={used.map((library) => library.name).join(", ") || "Assigned from a library or plan"} />
                  <ListCell mobile>
                    <Chip tone={unknownTiers.length ? "warn" : formats.length ? "info" : "muted"}>{unknownTiers.length ? "Check tiers" : formats.length ? `${formats.length} format${formats.length === 1 ? "" : "s"}` : "No formats"}</Chip>
                  </ListCell>
                </ListRow>
              );
            })
            ])}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={isOpen}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={mode.kind === "create" ? "New quality profile" : editing?.name ?? form.name}
        description={mode.kind === "create" ? "Which release tiers are allowed, and where upgrades stop." : `Quality profile · ${form.mediaType === "tv" ? "TV" : "Movies"} · stops at ${form.cutoff || "—"}`}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create profile" : "Save profile"} onCancel={requestClose} disabled={busy} />}
      >
        {mode.kind === "create" ? (
          <DrawerSection title="Start from">
            <Field label="Starter" help="Based on TRaSH Guide recommendations. Everything below stays editable.">
              <Select value={starterId} onChange={(event) => applyStarter(event.target.value)} options={[{ value: "", label: "Blank profile" }, ...PROFILE_STARTERS.map((starter) => ({ value: starter.id, label: `${starter.label} · ${starter.mediaType === "tv" ? "TV" : "Movies"}` }))]} />
            </Field>
            {starterId ? <p className="-mt-1 text-[length:var(--type-caption)] text-muted-foreground">{PROFILE_STARTERS.find((starter) => starter.id === starterId)?.summary}</p> : null}
            {recommendedCount ? (
              <SwitchRow
                label="Add the recommended formats"
                description={`${recommendedCount} TRaSH-guide scoring rule${recommendedCount === 1 ? "" : "s"} — repack/proper preferences, codec and upscale penalties. Created on save if they don't exist yet.`}
                checked={addRecommended}
                onCheckedChange={setAddRecommended}
              />
            ) : null}
          </DrawerSection>
        ) : null}

        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Profile name" error={errors.name}>
              <Input value={form.name} onChange={(event) => { setErrors((current) => ({ ...current, name: undefined })); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Movies / Standard" autoComplete="off" />
            </Field>
            <Field label="Media type">
              <SegmentedControl<"movies" | "tv"> value={form.mediaType} onValueChange={setMediaType} options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV shows" }]} />
            </Field>
          </FieldRow>
        </DrawerSection>

        <DrawerSection title="Quality tiers" aside={form.allowed.length ? `${form.allowed.length} allowed · best first` : undefined}>
          {allowedForDisplay.length ? (
            <ul className="grid gap-2">
              {allowedForDisplay.map((name, index) => {
                const tier = tiers.find((candidate) => candidate.name === name);
                return (
                  <li key={name} className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-[var(--field-pad-x)]">
                    <span className="w-5 shrink-0 text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{index + 1}</span>
                    <span className="min-w-0 flex-1 truncate text-[length:var(--type-body-sm)] font-medium text-foreground">
                      {name}
                      {tier ? <span className="ml-2 font-normal text-muted-foreground">rank {tier.rank}</span> : <span className="ml-2 font-normal text-warning">not in the quality model</span>}
                    </span>
                    <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Move ${name} up`} disabled={index === 0} onClick={() => moveTier(name, -1)}>
                      <ArrowUp className="h-3.5 w-3.5" />
                    </Button>
                    <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Move ${name} down`} disabled={index === allowedForDisplay.length - 1} onClick={() => moveTier(name, 1)}>
                      <ArrowDown className="h-3.5 w-3.5" />
                    </Button>
                    <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove ${name}`} onClick={() => removeTier(name)}>
                      <X className="h-3.5 w-3.5" />
                    </Button>
                  </li>
                );
              })}
            </ul>
          ) : null}
          {errors.allowed ? <p role="alert" className="text-[length:var(--type-caption)] text-destructive">{errors.allowed}</p> : null}
          <FieldRow>
            <Field label="Add a tier" help="Most preferred at the top.">
              <Select value="" onChange={(event) => addTier(event.target.value)} placeholder={unusedTiers.length ? "Choose a tier" : "All tiers allowed"} options={unusedTiers.map((tier) => ({ value: tier.name, label: `${tier.name} · rank ${tier.rank}` }))} disabled={!unusedTiers.length} />
            </Field>
            <Field label="Stop upgrading at" error={errors.cutoff} help="Deluno stops replacing files once this tier is reached.">
              <Select value={form.cutoff} onChange={(event) => { setErrors((current) => ({ ...current, cutoff: undefined })); setForm((current) => ({ ...current, cutoff: event.target.value })); }} placeholder="Choose a tier" options={allowedForDisplay.map((name) => ({ value: name, label: name }))} disabled={!form.allowed.length} />
            </Field>
          </FieldRow>
          <SwitchRow label="Upgrade until cutoff" description="Keep replacing files until the cutoff tier is reached." checked={form.upgradeUntilCutoff} onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeUntilCutoff: checked }))} />
        </DrawerSection>

        <DrawerSection title="Formats" aside={form.customFormatIds.length ? `${form.customFormatIds.length} selected` : undefined}>
          {availableFormats.length ? (
            <div role="group" aria-label="Custom formats" className="flex flex-wrap gap-1.5">
              {availableFormats.map((format) => {
                const active = form.customFormatIds.includes(format.id);
                return (
                  <button
                    key={format.id}
                    type="button"
                    aria-pressed={active}
                    onClick={() => toggleFormat(format.id)}
                    className={cn(
                      "inline-flex h-7 items-center gap-1.5 rounded-full border px-2.5 text-[length:var(--type-caption)] font-medium transition-colors",
                      "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                      active ? "border-primary/40 bg-primary/12 text-primary" : "border-hairline bg-surface-2 text-muted-foreground hover:border-primary/30 hover:text-foreground"
                    )}
                  >
                    {format.name}
                    <span className={cn("tabular-nums", active ? "text-primary/80" : "text-muted-foreground/70")}>{format.score >= 0 ? `+${format.score}` : format.score}</span>
                  </button>
                );
              })}
            </div>
          ) : (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">No {form.mediaType === "tv" ? "TV" : "movie"} formats yet — add them under Release preferences.</p>
          )}
          <Disclosure title="Fine-tune" summary="Unknown-quality handling" open={fineTuneOpen} onOpenChange={setFineTuneOpen}>
            <SwitchRow label="Upgrade files of unknown quality" description="Replace files Deluno can't identify when a matching release appears." checked={form.upgradeUnknownItems} onCheckedChange={(checked) => setForm((current) => ({ ...current, upgradeUnknownItems: checked }))} />
          </Disclosure>
        </DrawerSection>

        {editing ? (
          <DrawerSection title="Used by" aside={usedBy.length ? `${usedBy.length} ${usedBy.length === 1 ? "library" : "libraries"}` : undefined}>
            {usedBy.length ? (
              <div className="grid gap-2">
                {usedBy.map((library) => (
                  <div key={library.id} className="flex min-h-10 items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-hairline px-[var(--field-pad-x)] text-[length:var(--type-body-sm)]">
                    <span className="truncate font-medium text-foreground">{library.name}</span>
                    <span className="truncate text-[length:var(--type-caption)] text-muted-foreground">{library.defaultPolicySetName ? `via ${library.defaultPolicySetName}` : "direct assignment"}</span>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">Not assigned yet. Libraries pick a profile through their media plan, or directly in Library setup.</p>
            )}
          </DrawerSection>
        ) : null}

        {editing ? (
          <DrawerSection>
            <DrawerDanger title="Delete this profile" description="Libraries and plans using it need another profile." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog open={confirmRemove} onOpenChange={setConfirmRemove} title={`Delete “${editing?.name ?? form.name}”?`} description={usedBy.length ? `${usedBy.length} ${usedBy.length === 1 ? "library uses" : "libraries use"} this profile and will need another one.` : "This profile is not assigned to any library."} confirmLabel="Delete profile" busy={busy} onConfirm={() => void handleRemove()} />
      <ConfirmDialog
        open={confirmDiscard || blocker.state === "blocked"}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
          if (blocker.state === "blocked") blocker.reset();
        }}
        title="Discard unsaved changes?"
        description="Your edits to this profile haven't been saved."
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

/* --------------------------------------------------------------- utils */

function emptyForm(): ProfileForm {
  return { name: "", mediaType: "movies", allowed: [], cutoff: "", customFormatIds: [], upgradeUntilCutoff: true, upgradeUnknownItems: false };
}
function formFrom(profile: QualityProfileItem): ProfileForm {
  return {
    name: profile.name,
    mediaType: profile.mediaType === "tv" ? "tv" : "movies",
    allowed: splitCsv(profile.allowedQualities),
    cutoff: profile.cutoffQuality,
    customFormatIds: splitCsv(profile.customFormatIds),
    upgradeUntilCutoff: profile.upgradeUntilCutoff,
    upgradeUnknownItems: profile.upgradeUnknownItems
  };
}
function sameForm(a: ProfileForm, b: ProfileForm) {
  return a.name === b.name && a.mediaType === b.mediaType && a.cutoff === b.cutoff && a.upgradeUntilCutoff === b.upgradeUntilCutoff && a.upgradeUnknownItems === b.upgradeUnknownItems && a.allowed.join("|") === b.allowed.join("|") && sameIds(a.customFormatIds, b.customFormatIds);
}
function sameIds(a: string[], b: string[]) {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((id) => set.has(id));
}
function splitCsv(value: string | null | undefined) {
  return (value ?? "").split(",").map((item) => item.trim()).filter(Boolean);
}
function recommendedFormatsFor(starterId: string) {
  const trashPresetId = PROFILE_STARTERS.find((starter) => starter.id === starterId)?.trashPresetId;
  if (!trashPresetId) return [];
  return QUALITY_PRESETS.find((preset) => preset.id === trashPresetId)?.recommendedCFs ?? [];
}

/** Create any recommended TRaSH formats this starter needs, and return every id to attach. */
async function ensureRecommendedFormats(starterId: string, mediaType: "movies" | "tv", existing: CustomFormatItem[]) {
  const recommended = recommendedFormatsFor(starterId);
  if (!recommended.length) return [];
  const byTrashId = new Map(existing.filter((format) => format.trashId).map((format) => [format.trashId!, format]));
  const ids: string[] = [];

  for (const { trashId, score } of recommended) {
    const match = byTrashId.get(trashId);
    if (match) {
      ids.push(match.id);
      continue;
    }
    const bundled = findBundledCF(trashId);
    if (!bundled) continue;
    const response = await authedFetch("/api/custom-formats", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: bundled.name,
        mediaType,
        score,
        trashId: bundled.trashId,
        conditions: bundled.patterns.map((pattern) => `regex: ${pattern}`).join("\n"),
        upgradeAllowed: true
      })
    });
    if (!response.ok) throw new Error(`Could not create the “${bundled.name}” format.`);
    const created = (await response.json()) as CustomFormatItem;
    ids.push(created.id);
    byTrashId.set(trashId, created);
  }
  return ids;
}

function rankOf(tiers: { name: string; rank: number }[], name: string) {
  return tiers.find((tier) => tier.name === name)?.rank ?? 0;
}
