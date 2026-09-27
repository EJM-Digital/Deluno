/**
 * A title's own subtitle-language override (#843) — the same
 * `SubtitleLanguagePicker` a library's own settings screen uses, so setting
 * "this one show needs Japanese, not the library default" reads exactly like
 * setting the library default does. Shared between the movie and show detail
 * pages: a movie's override sits on the movie, a show's on the show — never
 * on one episode — and both catalogues expose the identical
 * `PUT/DELETE .../subtitles/override` pair.
 */
import { useState } from "react";
import { ListCard } from "../ui/list-card";
import { Button } from "../ui/button";
import { toast } from "../shell/toaster";
import { authedFetch } from "../../lib/use-auth";
import { describeRequestFailure } from "../../lib/search-reasons";
import { SubtitleLanguagePicker } from "./subtitle-language-picker";
import type { SubtitleLanguageOption } from "../../lib/api";

export function SubtitleOverrideCard({
  basePath,
  languages,
  mode,
  libraryLanguages,
  options,
  onChanged
}: {
  /** e.g. `/api/movies/{id}` or `/api/series/{id}` */
  basePath: string;
  languages: string[] | null | undefined;
  mode: string | null | undefined;
  /** The library's own languages, shown as what "no override" falls back to. */
  libraryLanguages: string[];
  options: SubtitleLanguageOption[];
  onChanged?: () => void;
}) {
  const hasOverride = (languages?.length ?? 0) > 0;
  const [draft, setDraft] = useState<{ languages: string[]; mode: "all" | "first" }>({
    languages: languages ?? [],
    mode: mode === "first" ? "first" : "all"
  });
  const [busy, setBusy] = useState(false);

  const nameOf = (code: string) => options.find((option) => option.code === code)?.name ?? code.toUpperCase();

  async function save(next: { languages: string[]; mode: "all" | "first" }) {
    setDraft(next);
    if (next.languages.length === 0) {
      return clearOverride();
    }
    setBusy(true);
    let response: Response | null = null;
    try {
      response = await authedFetch(`${basePath}/subtitles/override`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ languages: next.languages, mode: next.mode })
      });
      if (!response.ok) throw new Error("subtitle-override-save-failed");
      toast.success("This title's own subtitle languages are set.");
      onChanged?.();
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "set this title's subtitle-language override" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusy(false);
    }
  }

  async function clearOverride() {
    setBusy(true);
    let response: Response | null = null;
    try {
      response = await authedFetch(`${basePath}/subtitles/override`, { method: "DELETE" });
      if (!response.ok) throw new Error("subtitle-override-clear-failed");
      toast.success("This title now follows its library's subtitle languages again.");
      onChanged?.();
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "clear this title's subtitle-language override" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusy(false);
    }
  }

  return (
    <ListCard
      title="Subtitle languages for this title"
      count={hasOverride ? "Overridden" : "Follows library"}
    >
      <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
        <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">
          {hasOverride
            ? "This title asks for its own subtitle languages instead of its library's."
            : libraryLanguages.length > 0
              ? `Following the library: ${libraryLanguages.map(nameOf).join(", ")}. Add a language below to give this title its own list instead.`
              : "The library asks for no subtitle languages. Add one below to give this title its own list."}
        </p>

        <SubtitleLanguagePicker
          languages={draft.languages}
          mode={draft.mode}
          options={options}
          disabled={busy}
          onChange={(next) => void save(next)}
        />

        {hasOverride ? (
          <div>
            <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => void clearOverride()}>
              Go back to the library default
            </Button>
          </div>
        ) : null}
      </div>
    </ListCard>
  );
}
