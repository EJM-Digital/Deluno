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
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { MenuSelect } from "../components/ui/menu-select";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Switch } from "../components/ui/switch";
import type { LibraryItem, SubtitleLanguageOption } from "../lib/api/types/resources";

const TABS = configurationNavAreas.find((area) => area.to === "/subtitles/languages")?.items ?? [];

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
  const [form, setForm] = useState<Form>({ languages: [], mode: "all", unknownLanguage: "", embeddedCounts: true });
  const [busy, setBusy] = useState(false);

  const asking = libraries.filter((library) => (library.subtitleLanguages ?? []).length > 0);

  function open(library: LibraryItem) {
    setEditing(library);
    setForm({
      languages: library.subtitleLanguages ?? [],
      mode: library.subtitleLanguageMode === "first" ? "first" : "all",
      unknownLanguage: library.subtitleUnknownLanguage ?? "",
      embeddedCounts: library.subtitleEmbeddedCounts ?? true
    });
  }

  async function save() {
    if (!editing) return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/libraries/${editing.id}/subtitles`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(form)
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
            ) : null}
          </>
        ) : null}
      </Drawer>
    </div>
  );
}

function nameOf(code: string, languages: SubtitleLanguageOption[]) {
  return languages.find((language) => language.code === code)?.name ?? code;
}
