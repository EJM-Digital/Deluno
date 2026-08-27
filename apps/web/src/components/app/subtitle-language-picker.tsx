import { X } from "lucide-react";
import { Select } from "../ui/select";
import { SegmentedControl } from "../ui/segmented-control";
import type { SubtitleLanguageOption } from "../../lib/api";

/**
 * Which subtitle languages a library wants, in the order it wants them.
 *
 * Order is the setting, not decoration: under `first` it is what "the first one
 * you can get" means, and under `all` it is the order Deluno tries providers
 * in. So the languages are a list you build, not a set of checkboxes.
 *
 * The mode control only appears once there are two languages, because with one
 * language "all of them" and "the first one" are the same sentence, and a
 * control whose two answers mean one thing is worse than no control.
 *
 * The names come from the server (`GET /api/subtitle-languages`), which is the
 * same table that parses what ffprobe reports and what a subtitle file beside a
 * video is called. A second copy here would let the picker offer a language the
 * parser would silently drop on save.
 */
export function SubtitleLanguagePicker({
  languages,
  mode,
  options,
  disabled,
  onChange
}: {
  languages: string[];
  mode: "all" | "first";
  options: SubtitleLanguageOption[];
  disabled?: boolean;
  onChange: (next: { languages: string[]; mode: "all" | "first" }) => void;
}) {
  const nameOf = (code: string) => options.find((option) => option.code === code)?.name ?? code.toUpperCase();
  const unselected = options.filter((option) => !languages.includes(option.code));

  // The list is served, so it can fail to arrive. Saying so beats a disabled
  // control with no explanation under it.
  if (options.length === 0) {
    return (
      <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">
        Deluno could not load the list of languages. Reload the page; if it keeps happening, check that Deluno is reachable.
      </p>
    );
  }

  return (
    <div className="grid gap-3">
      {languages.length > 0 ? (
        <ul className="flex flex-wrap items-center gap-1.5">
          {languages.map((code, index) => (
            <li key={code}>
              <span className="inline-flex h-7 items-center gap-1.5 rounded-full border border-hairline bg-surface-2 pl-2.5 pr-1 text-[length:var(--type-caption)] font-semibold text-foreground">
                {mode === "first" ? <span className="text-muted-foreground tabular-nums">{index + 1}</span> : null}
                {nameOf(code)}
                <button
                  type="button"
                  disabled={disabled}
                  aria-label={`Stop wanting ${nameOf(code)} subtitles`}
                  onClick={() => onChange({ languages: languages.filter((item) => item !== code), mode })}
                  className="grid h-5 w-5 place-items-center rounded-full text-muted-foreground transition-colors hover:bg-surface-3 hover:text-foreground disabled:opacity-50"
                >
                  <X className="h-3 w-3" />
                </button>
              </span>
            </li>
          ))}
        </ul>
      ) : null}

      <Select
        value=""
        disabled={disabled || unselected.length === 0}
        aria-label="Add a subtitle language"
        placeholder={languages.length === 0 ? "Choose a language…" : "Add another language…"}
        options={unselected.map((option) => ({ value: option.code, label: option.name }))}
        onChange={(event) => {
          const code = event.target.value;
          if (code) onChange({ languages: [...languages, code], mode });
        }}
      />

      {languages.length > 1 ? (
        <SegmentedControl
          aria-label="How many of these languages a file needs"
          value={mode}
          disabled={disabled}
          onValueChange={(next) => onChange({ languages, mode: next })}
          options={[
            { value: "all", label: "All of them" },
            { value: "first", label: "First one found" }
          ]}
        />
      ) : null}

      <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">
        {describe(languages.map(nameOf), mode)}
      </p>
    </div>
  );
}

/**
 * The consequence, in a sentence, under the control that causes it. Written out
 * with the actual language names rather than "the selected languages", because
 * the whole difference between the two modes is what happens to the second one.
 */
function describe(names: string[], mode: "all" | "first"): string {
  if (names.length === 0) {
    return "No subtitles wanted here. Deluno will not look for any, and titles on this shelf show no subtitle bar.";
  }

  if (names.length === 1) {
    return `Every file on this shelf gets ${names[0]} subtitles.`;
  }

  const listed = `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
  return mode === "all"
    ? `Every file on this shelf gets ${listed} — all of them, on every file.`
    : `Every file on this shelf gets ${names[0]}, or the next one on the list if ${names[0]} cannot be found. Just one, never two.`;
}
