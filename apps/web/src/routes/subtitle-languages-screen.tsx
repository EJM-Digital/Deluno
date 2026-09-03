import { useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Check } from "lucide-react";
import { toast } from "sonner";
import { fetchJson } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { configurationNavAreas } from "../components/app/settings-shell";
import { SubtitleLanguagePicker } from "../components/app/subtitle-language-picker";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerSection } from "../components/ui/drawer";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { MenuSelect } from "../components/ui/menu-select";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Switch } from "../components/ui/switch";
import type { LibraryItem, SubtitleContentModificationPolicy, SubtitleLanguageOption, SubtitleTimingPolicy } from "../lib/api/types/resources";

const TABS = configurationNavAreas.find((area) => area.to === "/settings/libraries")?.items ?? [];

export async function subtitleLanguagesLoader() {
  const [libraries, languages] = await Promise.all([
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<SubtitleLanguageOption[]>("/api/subtitle-languages")
  ]);
  return { libraries, languages };
}

interface Form {
  languages: string[];
  mode: "all" | "first";
  unknownLanguage: string;
  embeddedCounts: boolean;
  contentPolicy: SubtitleContentModificationPolicy;
  timingPolicy: SubtitleTimingPolicy;
  /** Comma-separated while being typed; split into terms on save. */
  mustContain: string;
  mustNotContain: string;
  omitLanguageCode: boolean;
}

const EMPTY_CONTENT_POLICY: SubtitleContentModificationPolicy = {
  stripHearingImpairedAnnotations: false,
  removeStyleTags: false,
  removeEmoji: false,
  normalizeWhitespace: false,
  fixAllUppercase: false,
  fixOcrErrors: false,
  cueColour: null,
  reverseRightToLeftPunctuation: false
};

const EMPTY_TIMING_POLICY: SubtitleTimingPolicy = {
  enabled: true,
  syncOnlyBelow: "made-for-this-file",
  maxOffsetSeconds: 60,
  requiredPeakSigma: 3,
  excludedProviders: null,
  repairFramerate: true
};

/**
 * Splits a typed list into terms. Commas because a release name can carry a
 * space and a group name often does — splitting on whitespace would turn
 * "Blu-ray Remux" into two separate refusals that each match far more.
 */
function terms(typed: string): string[] {
  return typed
    .split(",")
    .map((term) => term.trim())
    .filter((term) => term.length > 0);
}

/**
 * Which subtitles each library wants.
 *
 * <p><b>Every library on one screen.</b> These settings used to live inside each
 * library's edit form, which meant "English on everything, Japanese on anime"
 * was something you could only work out by opening two forms and remembering the
 * first. James, on where the growing pile of subtitle settings should go:
 * <i>"you can select the library you want to apply it to"</i> — and a list you
 * read across is the better version of selecting.</p>
 *
 * <p>It is the same list-and-drawer grammar as every other collection page in
 * Deluno, which is what lets #321's remaining settings — sync thresholds,
 * content modification, adaptive searching — land as more rows in the drawer
 * rather than another screen.</p>
 */
export function SubtitleLanguagesPage() {
  const { libraries, languages } = useLoaderData() as { libraries: LibraryItem[]; languages: SubtitleLanguageOption[] };
  const revalidator = useRevalidator();

  const [editing, setEditing] = useState<LibraryItem | null>(null);
  const [form, setForm] = useState<Form>({
    languages: [],
    mode: "all",
    unknownLanguage: "",
    embeddedCounts: true,
    contentPolicy: EMPTY_CONTENT_POLICY,
    timingPolicy: EMPTY_TIMING_POLICY,
    mustContain: "",
    mustNotContain: "",
    omitLanguageCode: false
  });
  const [busy, setBusy] = useState(false);

  const asking = libraries.filter((library) => (library.subtitleLanguages ?? []).length > 0);

  function open(library: LibraryItem) {
    setEditing(library);
    setForm({
      languages: library.subtitleLanguages ?? [],
      mode: library.subtitleLanguageMode === "first" ? "first" : "all",
      unknownLanguage: library.subtitleUnknownLanguage ?? "",
      embeddedCounts: library.subtitleEmbeddedCounts ?? true,
      contentPolicy: { ...EMPTY_CONTENT_POLICY, ...(library.subtitleContentPolicy ?? {}) },
      timingPolicy: { ...EMPTY_TIMING_POLICY, ...(library.subtitleTimingPolicy ?? {}) },
      mustContain: (library.subtitleNamePolicy?.mustContain ?? []).join(", "),
      mustNotContain: (library.subtitleNamePolicy?.mustNotContain ?? []).join(", "),
      omitLanguageCode: library.subtitleOmitLanguageCode ?? false
    });
  }

  async function save() {
    if (!editing) return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/libraries/${editing.id}/subtitles`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...form, namePolicy: { mustContain: terms(form.mustContain), mustNotContain: terms(form.mustNotContain) } })
      });
      if (!response.ok) throw new Error("Those subtitle settings could not be saved.");

      // Saving enqueues nothing. It changes what is wanted; the library cycle
      // decides when to act (DESIGN-002 rule 3), and saying so here is the
      // difference between a screen that looks broken for an hour and one that
      // told you what would happen.
      toast.success(`${editing.name} saved. Deluno will read and fetch on its next cycle.`);
      setEditing(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Those subtitle settings could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={TABS} />

      <ListCard
        title="Subtitle languages"
        count={asking.length ? `${asking.length} of ${libraries.length} ${libraries.length === 1 ? "library" : "libraries"} asking` : undefined}
      >
        {libraries.length === 0 ? (
          <ListEmpty
            title="No libraries yet"
            description="Subtitle languages are set per library, so make one under Media Management first."
          />
        ) : (
          <ListTable
            columns={[
              { label: "Library" },
              { label: "Languages" },
              { label: "How many per file" },
              { label: "Unnamed subtitles" },
              { label: "Asking", width: LIST_TRACK.status, mobile: true }
            ]}
          >
            {libraries.map((library) => {
              const wanted = library.subtitleLanguages ?? [];
              return (
                <ListRow key={library.id} onClick={() => open(library)} selected={editing?.id === library.id}>
                  <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                  <ListCell
                    primary={wanted.length ? wanted.map((code) => nameOf(code, languages)).join(", ") : "None"}
                    secondary={wanted.length ? undefined : "Nothing is fetched for this shelf"}
                  />
                  <ListCell
                    primary={wanted.length === 0 ? "—" : library.subtitleLanguageMode === "first" ? "Any one of them" : "All of them"}
                  />
                  <ListCell
                    primary={library.subtitleUnknownLanguage ? nameOf(library.subtitleUnknownLanguage, languages) : "Left unknown"}
                    secondary={library.subtitleEmbeddedCounts === false ? "Embedded tracks do not count" : undefined}
                  />
                  <ListCell mobile>
                    <Chip tone={wanted.length ? "ok" : "idle"}>{wanted.length ? `${wanted.length}` : "Off"}</Chip>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={editing !== null}
        onOpenChange={(open) => !open && setEditing(null)}
        title={editing?.name ?? "Library"}
        description="Which subtitles this shelf wants, and what already counts as having one."
        footer={
          <>
            <span className="text-[length:var(--type-caption)] text-muted-foreground">
              Saving queues nothing. The library cycle reads and fetches on its own schedule.
            </span>
            <Button type="button" size="sm" onClick={() => void save()} disabled={busy} className="gap-1.5">
              <Check className="h-3.5 w-3.5" />
              {busy ? "Saving…" : "Save"}
            </Button>
          </>
        }
      >
        {editing ? (
          <>
            <DrawerSection title="Languages">
              <SubtitleLanguagePicker
                languages={form.languages}
                mode={form.mode}
                options={languages}
                disabled={busy}
                onChange={(next) => setForm({ ...form, languages: next.languages, mode: next.mode })}
              />
            </DrawerSection>

            {/*
              What already counts as having one. Only shown once a language is
              wanted, because neither question means anything on a shelf that has
              not asked for subtitles — and a settings screen that shows every
              switch whatever the state is how a panel becomes a wall.
            */}
            {form.languages.length > 0 ? (
              <>
                <DrawerSection title="What counts as having one">
                <Field
                  label="A subtitle with no language in its name"
                  help="Deluno does not guess. A bare Movie.srt counts for nothing unless you say what it is — reading it as your first language would be right most of the time, and silently wrong the rest."
                >
                  <MenuSelect
                    label="Unknown subtitle language"
                    value={form.unknownLanguage}
                    onChange={(value: string) => setForm({ ...form, unknownLanguage: value })}
                    options={[
                      { value: "", label: "Leave it unknown", hint: "It counts for nothing. This is the default." },
                      ...languages.map((language) => ({
                        value: language.code,
                        label: language.name,
                        hint: `Treat an unnamed subtitle as ${language.name}`
                      }))
                    ]}
                    className="max-w-sm"
                  />
                </Field>

                <Field
                  label="Name subtitles after the video only"
                  help="Writes Film.srt instead of Film.en.srt, for players that only load a subtitle sharing the video's exact name. The file no longer says what language it is, so only turn this on where one language is wanted — Deluno reads it back as whatever this shelf treats an unnamed language as."
                >
                  <Switch
                    checked={form.omitLanguageCode}
                    disabled={busy}
                    onCheckedChange={(omitLanguageCode) => setForm({ ...form, omitLanguageCode })}
                  />
                </Field>

                <Field
                  label="Count subtitles inside the video"
                  help="On by default. Turn it off to fetch a file beside the video even when the container already has the language — an embedded track cannot be swapped or corrected, and some players ignore them."
                >
                  <Switch
                    checked={form.embeddedCounts}
                    disabled={busy}
                    onCheckedChange={(embeddedCounts) => setForm({ ...form, embeddedCounts })}
                  />
                </Field>
                </DrawerSection>

                <DrawerSection title="Timing repair">
                  <p className="text-[length:var(--type-caption)] text-muted-foreground">
                    Deluno only moves a subtitle when the audio has a clear match. The default repairs subtitles below “made for this file”; narrow that to “same source” or turn it off when this shelf should never rewrite timing.
                  </p>
                  <PolicySwitch
                    label="Repair subtitle timing automatically"
                    help="Runs in the separate local timing lane after a fetched subtitle is written. A confident zero-offset result is left alone."
                    checked={form.timingPolicy.enabled}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, timingPolicy: { ...form.timingPolicy, enabled: value } })}
                  />
                  <PolicySwitch
                    label="Repair a subtitle written for another framerate"
                    help="A subtitle timed against a 25 fps copy drifts further out the longer the film runs, which moving it cannot fix. Deluno rewrites the whole timeline instead, and only when doing so clearly fits the audio better."
                    checked={form.timingPolicy.repairFramerate}
                    disabled={busy || !form.timingPolicy.enabled}
                    onCheckedChange={(value) => setForm({ ...form, timingPolicy: { ...form.timingPolicy, repairFramerate: value } })}
                  />
                  <Field
                    label="Repair subtitles below"
                    help="Same source is the safer choice. Made for this file includes same-source subtitles and is the default because those are the files most likely to need a cross-release timing correction."
                  >
                    <MenuSelect
                      label="Timing threshold"
                      value={form.timingPolicy.syncOnlyBelow}
                      onChange={(value: string) => setForm({ ...form, timingPolicy: { ...form.timingPolicy, syncOnlyBelow: value } })}
                      options={[
                        { value: "same-source", label: "Same source", hint: "Only repair subtitles with no source match." },
                        { value: "made-for-this-file", label: "Made for this file", hint: "Repair any subtitle below the exact-file match." }
                      ]}
                      className="max-w-sm"
                    />
                  </Field>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Field
                      label="Maximum offset"
                      help="Search window in seconds, from 1 to 300."
                    >
                      <Input
                        type="number"
                        min={1}
                        max={300}
                        step={1}
                        value={form.timingPolicy.maxOffsetSeconds}
                        disabled={busy}
                        onChange={(event) => {
                          const value = Number(event.target.value);
                          if (Number.isFinite(value)) setForm({ ...form, timingPolicy: { ...form.timingPolicy, maxOffsetSeconds: value } });
                        }}
                      />
                    </Field>
                    <Field
                      label="Confidence required"
                      help="Peak strength in sigma, from 1 to 10. The default is 3."
                    >
                      <Input
                        type="number"
                        min={1}
                        max={10}
                        step={0.1}
                        value={form.timingPolicy.requiredPeakSigma}
                        disabled={busy}
                        onChange={(event) => {
                          const value = Number(event.target.value);
                          if (Number.isFinite(value)) setForm({ ...form, timingPolicy: { ...form.timingPolicy, requiredPeakSigma: value } });
                        }}
                      />
                    </Field>
                  </div>
                  <Field
                    label="Exclude providers from timing repair"
                    help="Comma-separated provider keys. The subtitle is still saved and counted; only the automatic timing pass is skipped. Provider keys are shown under Find & Download → Subtitle providers."
                  >
                    <Input
                      value={(form.timingPolicy.excludedProviders ?? []).join(", ")}
                      disabled={busy}
                      onChange={(event) => setForm({
                        ...form,
                        timingPolicy: {
                          ...form.timingPolicy,
                          excludedProviders: event.target.value.split(",").map((provider) => provider.trim()).filter(Boolean)
                        }
                      })}
                      placeholder="For example: opensubtitles, subdl"
                    />
                  </Field>
                </DrawerSection>

                <DrawerSection title="Which releases to take">
                  <p className="text-[length:var(--type-caption)] text-muted-foreground">
                    Language and hearing-impaired are decided above. This is about the release
                    itself — the words in its name. Separate terms with commas. Leave both empty
                    and Deluno takes the best match it finds.
                  </p>
                  <Field
                    label="Only take releases naming"
                    help="Any one term is enough. A subtitle whose provider gives no release name is never refused by this."
                  >
                    <Input
                      value={form.mustContain}
                      placeholder="e.g. NTb, FLUX"
                      disabled={busy}
                      onChange={(event) => setForm({ ...form, mustContain: event.target.value })}
                    />
                  </Field>
                  <Field
                    label="Never take releases naming"
                    help="Any one term refuses the release, and this is checked before the must-have list."
                  >
                    <Input
                      value={form.mustNotContain}
                      placeholder="e.g. HDTV, CAM"
                      disabled={busy}
                      onChange={(event) => setForm({ ...form, mustNotContain: event.target.value })}
                    />
                  </Field>
                </DrawerSection>

                <DrawerSection title="After download">
                  <p className="text-[length:var(--type-caption)] text-muted-foreground">
                    These named cleanups change subtitle text only. Timing and provider matching stay untouched.
                  </p>
                  <PolicySwitch
                    label="Remove hearing-impaired annotations"
                    help="Removes recognised sound and music annotations such as [MUSIC] or (door closes)."
                    checked={form.contentPolicy.stripHearingImpairedAnnotations}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, contentPolicy: { ...form.contentPolicy, stripHearingImpairedAnnotations: value } })}
                  />
                  <PolicySwitch
                    label="Remove style tags"
                    help="Removes common italic, bold, font, ruby, and WebVTT cue tags."
                    checked={form.contentPolicy.removeStyleTags}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, contentPolicy: { ...form.contentPolicy, removeStyleTags: value } })}
                  />
                  <PolicySwitch
                    label="Remove emoji"
                    help="Removes emoji and their presentation marks from cue text."
                    checked={form.contentPolicy.removeEmoji}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, contentPolicy: { ...form.contentPolicy, removeEmoji: value } })}
                  />
                  <PolicySwitch
                    label="Normalize whitespace"
                    help="Trims cue lines and collapses repeated spaces without changing timing."
                    checked={form.contentPolicy.normalizeWhitespace}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, contentPolicy: { ...form.contentPolicy, normalizeWhitespace: value } })}
                  />
                  <PolicySwitch
                    label="Fix all-uppercase text"
                    help="Converts long all-uppercase cue lines to sentence case. Short acronyms are left alone."
                    checked={form.contentPolicy.fixAllUppercase}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, contentPolicy: { ...form.contentPolicy, fixAllUppercase: value } })}
                  />
                  <PolicySwitch
                    label="Fix OCR mistakes"
                    help="Repairs the characters a picture-to-text pass gets wrong — a lone l read as I, or 0 and 1 read inside a word as o and l."
                    checked={form.contentPolicy.fixOcrErrors}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, contentPolicy: { ...form.contentPolicy, fixOcrErrors: value } })}
                  />
                  <PolicySwitch
                    label="Move right-to-left punctuation"
                    help="Puts trailing full stops and question marks at the front of Arabic and Hebrew lines, where they belong. Lines with no right-to-left text are untouched."
                    checked={form.contentPolicy.reverseRightToLeftPunctuation}
                    disabled={busy}
                    onCheckedChange={(value) => setForm({ ...form, contentPolicy: { ...form.contentPolicy, reverseRightToLeftPunctuation: value } })}
                  />
                  <Field
                    label="Subtitle colour"
                    help="Some players show every subtitle in white whatever the track says. Leave empty for the player's own colour."
                  >
                    <Input
                      value={form.contentPolicy.cueColour ?? ""}
                      placeholder="e.g. yellow or #ffd400"
                      disabled={busy}
                      onChange={(event) => setForm({
                        ...form,
                        contentPolicy: { ...form.contentPolicy, cueColour: event.target.value.trim() || null }
                      })}
                    />
                  </Field>
                </DrawerSection>
              </>
            ) : null}
          </>
        ) : null}
      </Drawer>
    </div>
  );
}

function PolicySwitch({
  label,
  help,
  checked,
  disabled,
  onCheckedChange
}: {
  label: string;
  help: string;
  checked: boolean;
  disabled: boolean;
  onCheckedChange: (value: boolean) => void;
}) {
  return (
    <Field label={label} help={help}>
      <Switch checked={checked} disabled={disabled} onCheckedChange={onCheckedChange} />
    </Field>
  );
}

function nameOf(code: string, languages: SubtitleLanguageOption[]) {
  return languages.find((language) => language.code === code)?.name ?? code;
}
