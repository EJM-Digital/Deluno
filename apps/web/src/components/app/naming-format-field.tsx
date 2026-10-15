import { useState } from "react";
import { Check } from "lucide-react";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { cn } from "../../lib/utils";

export type NamingFormatKind = "movie-folder" | "series-folder" | "movie-file" | "episode-file" | "daily-episode-file" | "anime-episode-file" | "destination-movie" | "destination-series";

interface FormatPreset {
  label: string;
  value: string;
  hint: string;
  description: string;
}

interface FormatToken {
  label: string;
  value: string;
}

interface NamingFormatFieldProps {
  value: string;
  onChange: (value: string) => void;
  kind: NamingFormatKind;
  placeholder?: string;
  className?: string;
  showExamples?: boolean;
  onCustomModeChange?: (active: boolean, draftValue?: string, previousValue?: string) => void;
}

const PRESETS: Record<NamingFormatKind, FormatPreset[]> = {
  "movie-folder": [
    { label: "Title and year", value: "{Movie Title} ({Release Year})", hint: "Blade Runner 2049 (2017)", description: "Best default. Clear, compatible, and easy to scan." },
    { label: "Title, year, IMDb", value: "{Movie Title} ({Release Year}) [{IMDb ID}]", hint: "Blade Runner 2049 (2017) [tt1856101]", description: "Adds a unique ID to avoid remakes or duplicate titles being mixed up." },
    { label: "Title only", value: "{Movie Title}", hint: "Blade Runner 2049", description: "Shortest format. Use only if your library never has duplicate titles." }
  ],
  "series-folder": [
    { label: "Title and year", value: "{Series Title} ({Series Year})", hint: "Severance (2022)", description: "Best default for TV. Keeps reboots and same-name shows separate." },
    { label: "Title, year, TVDb", value: "{Series Title} ({Series Year}) [tvdb-{TVDb ID}]", hint: "Severance (2022) [tvdb-371980]", description: "Most precise. Useful if you sync with tools that understand TVDb IDs." },
    { label: "Title only", value: "{Series Title}", hint: "Severance", description: "Cleanest format, but easier to confuse with remakes." }
  ],
  "movie-file": [
    { label: "Title, year and what it is", value: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]", hint: "Arrival (2016) [WEB 1080p] [AMZN]", description: "Best default. Keeps the quality and the guide formats Deluno matched, so the file still says what it is." },
    { label: "Title and year", value: "{Movie Title} ({Release Year})", hint: "Arrival (2016)", description: "Shortest. The file name then says nothing about the release it came from." },
    { label: "With the release group", value: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]-{Release Group}", hint: "Arrival (2016) [WEB 1080p] [AMZN]-FLUX", description: "Adds who made the release, the way most release names end." }
  ],
  "episode-file": [
    { label: "Standard episode", value: "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}", hint: "Severance - S01E01 - Good News About Hell", description: "Best default. Human-readable and compatible with media servers." },
    { label: "With quality", value: "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title} [{Quality}]", hint: "Severance - S01E01 - Good News About Hell [WEB-DL 1080p]", description: "Adds the quality after the episode title." },
    { label: "Episode code only", value: "S{Season:00}E{Episode:00} - {Episode Title}", hint: "S01E01 - Good News About Hell", description: "Compact. Useful when files already sit inside the series folder." }
  ],
  "daily-episode-file": [
    { label: "Standard daily episode", value: "{Series Title} - {Air-Date} - {Episode Title}", hint: "The Daily Show - 2022-09-30 - Guest Episode", description: "Best default for dated shows. The date is the episode's own identity, not a season/episode guess." },
    { label: "With quality", value: "{Series Title} - {Air-Date} - {Episode Title} [{Quality}]", hint: "The Daily Show - 2022-09-30 - Guest Episode [WEB-DL 1080p]", description: "Adds the quality after the guest or episode title." },
    { label: "Date only", value: "{Series Title} - {Air-Date}", hint: "The Daily Show - 2022-09-30", description: "Compact. Useful when a dated show rarely has a distinct episode title." }
  ],
  "anime-episode-file": [
    { label: "Standard anime episode", value: "{Series Title} - {absolute:000} - {Episode Title}", hint: "One Piece - 042 - Ship Overboard!", description: "Best default for absolute-numbered shows: one running episode count instead of season/episode." },
    { label: "With quality", value: "{Series Title} - {absolute:000} - {Episode Title} [{Quality}]", hint: "One Piece - 042 - Ship Overboard! [WEB-DL 1080p]", description: "Adds the quality after the episode's name." },
    { label: "Season and absolute", value: "{Series Title} - S{Season:00}E{Episode:00} - {absolute:000} - {Episode Title}", hint: "One Piece - S01E42 - 042 - Ship Overboard!", description: "Keeps season/episode for media servers while still recording the absolute count." }
  ],
  "destination-movie": [
    { label: "Use movie default", value: "{Movie Title} ({Release Year})", hint: "Arrival (2016)", description: "Use the same naming style as the global movie setting." },
    { label: "Genre grouped", value: "{Genre}\\{Movie Title} ({Release Year})", hint: "Sci-Fi\\Arrival (2016)", description: "Creates a genre folder before the movie folder." },
    { label: "Quality grouped", value: "{Quality Profile}\\{Movie Title} ({Release Year})", hint: "4K\\Arrival (2016)", description: "Separates folders by the quality policy that matched." }
  ],
  "destination-series": [
    { label: "Use series default", value: "{Series Title} ({Series Year})", hint: "The Bear (2022)", description: "Use the same naming style as the global TV setting." },
    { label: "Genre grouped", value: "{Genre}\\{Series Title} ({Series Year})", hint: "Comedy\\The Bear (2022)", description: "Creates a genre folder before the series folder." },
    { label: "Network grouped", value: "{Network}\\{Series Title} ({Series Year})", hint: "FX\\The Bear (2022)", description: "Separates TV folders by network or service." }
  ]
};

const TOKENS: Record<NamingFormatKind, FormatToken[]> = {
  "movie-folder": [
    { label: "Movie title", value: "{Movie Title}" },
    { label: "Release year", value: "{Release Year}" },
    { label: "IMDb ID", value: "{IMDb ID}" },
    { label: "Quality profile", value: "{Quality Profile}" }
  ],
  "series-folder": [
    { label: "Series title", value: "{Series Title}" },
    { label: "Series year", value: "{Series Year}" },
    { label: "Network", value: "{Network}" },
    { label: "TVDb ID", value: "{TVDb ID}" }
  ],
  "episode-file": [
    { label: "Series title", value: "{Series Title}" },
    { label: "Season 01", value: "{Season:00}" },
    { label: "Episode 01", value: "{Episode:00}" },
    { label: "Episode title", value: "{Episode Title}" },
    { label: "Quality", value: "{Quality}" },
    { label: "Custom formats", value: "{Custom Formats}" },
    { label: "Release group", value: "{Release Group}" },
    { label: "Air date", value: "{Air-Date}" },
    { label: "Absolute episode 001", value: "{absolute:000}" },
    { label: "Original title", value: "{Original Title}" },
    { label: "Original filename", value: "{Original Filename}" },
    { label: "Release hash", value: "{Release Hash}" },
    { label: "Video codec", value: "{MediaInfo VideoCodec}" },
    { label: "Audio codec", value: "{MediaInfo AudioCodec}" },
    { label: "Audio channels", value: "{MediaInfo AudioChannels}" },
    { label: "Dynamic range", value: "{MediaInfo VideoDynamicRangeType}" },
    { label: "Bit depth", value: "{MediaInfo VideoBitDepth}" }
  ],
  "movie-file": [
    { label: "Movie title", value: "{Movie Title}" },
    { label: "Release year", value: "{Release Year}" },
    { label: "Quality", value: "{Quality}" },
    { label: "Custom formats", value: "{Custom Formats}" },
    { label: "Release group", value: "{Release Group}" },
    { label: "Edition", value: "{Edition}" },
    { label: "Original title", value: "{Original Title}" },
    { label: "Original filename", value: "{Original Filename}" },
    { label: "Release hash", value: "{Release Hash}" },
    { label: "Video codec", value: "{MediaInfo VideoCodec}" },
    { label: "Audio codec", value: "{MediaInfo AudioCodec}" },
    { label: "Audio channels", value: "{MediaInfo AudioChannels}" },
    { label: "Dynamic range", value: "{MediaInfo VideoDynamicRangeType}" },
    { label: "Bit depth", value: "{MediaInfo VideoBitDepth}" }
  ],
  // #761: dated and absolute-numbered shows use the same token family as a
  // standard episode file, just led by the token that actually identifies
  // that episode.
  "daily-episode-file": [
    { label: "Series title", value: "{Series Title}" },
    { label: "Air date", value: "{Air-Date}" },
    { label: "Episode title", value: "{Episode Title}" },
    { label: "Quality", value: "{Quality}" },
    { label: "Custom formats", value: "{Custom Formats}" },
    { label: "Release group", value: "{Release Group}" }
  ],
  "anime-episode-file": [
    { label: "Series title", value: "{Series Title}" },
    { label: "Absolute episode 001", value: "{absolute:000}" },
    { label: "Season 01", value: "{Season:00}" },
    { label: "Episode 01", value: "{Episode:00}" },
    { label: "Episode title", value: "{Episode Title}" },
    { label: "Quality", value: "{Quality}" },
    { label: "Custom formats", value: "{Custom Formats}" },
    { label: "Release group", value: "{Release Group}" }
  ],
  "destination-movie": [
    { label: "Movie title", value: "{Movie Title}" },
    { label: "Release year", value: "{Release Year}" },
    { label: "Genre", value: "{Genre}" },
    { label: "Quality profile", value: "{Quality Profile}" },
    { label: "Tag", value: "{Tag}" }
  ],
  "destination-series": [
    { label: "Series title", value: "{Series Title}" },
    { label: "Series year", value: "{Series Year}" },
    { label: "Genre", value: "{Genre}" },
    { label: "Network", value: "{Network}" },
    { label: "Tag", value: "{Tag}" }
  ]
};

/** What each kind of format names, so the help under each section says its own thing (#1162). */
const KIND_NOUN: Record<NamingFormatKind, string> = {
  "movie-folder": "movie folders",
  "series-folder": "show folders",
  "movie-file": "movie files",
  "episode-file": "episode files",
  "daily-episode-file": "daily-show episode files",
  "anime-episode-file": "anime episode files",
  "destination-movie": "movie destination folders",
  "destination-series": "show destination folders"
};

export function NamingFormatField({
  value,
  onChange,
  kind,
  placeholder,
  className,
  showExamples = true,
  onCustomModeChange
}: NamingFormatFieldProps) {
  const presets = PRESETS[kind];
  const selectedPreset = presets.find((preset) => preset.value === value);
  const example = selectedPreset?.hint ?? previewNamingFormat(value || placeholder || "");
  const [customSelected, setCustomSelected] = useState(() => !selectedPreset && value.trim().length > 0);
  const isCustom = !selectedPreset && (customSelected || value.trim().length > 0);
  const selectedValue = selectedPreset?.value ?? (isCustom ? "__custom__" : "");
  const options = [
    ...presets.map((preset) => ({
      ...preset,
      label: preset.label === "Title and year" ? "Title + year" : preset.label === "Title, year, IMDb" || preset.label === "Title, year, TVDb" ? "Title + ID" : preset.label
    })),
    {
      label: "Custom pattern",
      value: "__custom__",
      hint: "Build your own",
      description: `Build your own pattern for ${KIND_NOUN[kind]} from tokens.`
    }
  ];

  function choosePreset(nextValue: string) {
    if (nextValue === "__custom__") {
      setCustomSelected(true);
      onCustomModeChange?.(true, isCustom ? value : "", value);
      if (!isCustom) onChange("");
      return;
    }
    setCustomSelected(false);
    onCustomModeChange?.(false);
    onChange(nextValue);
  }

  return (
    <div className={cn("grid gap-3", className)}>
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className="text-(length:--type-caption) font-medium text-foreground">Format</span>
        <span className="text-(length:--type-caption) text-muted-foreground">{`Choose how Deluno names ${KIND_NOUN[kind]}.`}</span>
      </div>
      <div
        role="radiogroup"
        aria-label={`${kind} naming style`}
        className="overflow-hidden rounded-[10px] border border-hairline bg-surface-1"
      >
        {options.map((option, index) => {
          const active = selectedValue === option.value;
          return (
            <button
              key={option.value}
              type="button"
              role="radio"
              aria-label={option.label}
              aria-checked={active}
              onClick={() => choosePreset(option.value)}
              className={cn(
                "flex w-full items-start gap-3 border-b border-hairline px-3.5 py-3 text-left transition-colors last:border-b-0 focus-visible:z-10 focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
                active ? "bg-primary/8" : "hover:bg-surface-2/70"
              )}
            >
              <span className={cn("mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border", active ? "border-primary bg-primary text-primary-foreground" : "border-hairline bg-background text-transparent")}>
                <Check aria-hidden className="h-3 w-3" />
              </span>
              <span className="min-w-0 flex-1">
                <span className={cn("flex items-center gap-2 text-(length:--type-caption) font-semibold", active ? "text-primary" : "text-foreground")}>
                  <span className="truncate">{option.label}</span>
                  {index === 0 ? <span className="shrink-0 text-(length:--type-micro) font-semibold uppercase tracking-[0.08em] text-primary">Recommended</span> : null}
                </span>
                <span className="mt-0.5 block text-(length:--type-caption) leading-snug text-muted-foreground">{option.description}</span>
              </span>
              {showExamples ? (
                <span className="hidden max-w-[42%] shrink-0 text-right md:block">
                  <span className="block text-(length:--type-micro) font-semibold uppercase tracking-[0.08em] text-muted-foreground">Example</span>
                  <code className="mt-0.5 block truncate text-(length:--type-caption) text-foreground">{option.hint}</code>
                </span>
              ) : null}
            </button>
          );
        })}
      </div>

      {showExamples ? (
        <div className="flex min-w-0 items-baseline gap-3 text-(length:--type-caption)">
          <span className="shrink-0 font-semibold uppercase tracking-[0.08em] text-muted-foreground">Preview</span>
          <code className="truncate text-foreground">{example || "Choose a style to see a preview."}</code>
        </div>
      ) : null}
    </div>
  );
}

export function NamingPatternEditor({ kind, value, onChange, placeholder }: { kind: NamingFormatKind; value: string; onChange: (value: string) => void; placeholder?: string }) {
  const tokens = TOKENS[kind];

  function insertToken(token: string) {
    const separator = value.trim().length === 0 || value.endsWith(" ") || value.endsWith("\\") ? "" : " ";
    onChange(`${value}${separator}${token}`);
  }

  return (
    <div className="grid gap-(--grid-gap)">
      <div className="grid gap-1.5">
        <span className="text-(length:--type-caption) font-medium text-foreground">Pattern</span>
        <Input value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} aria-label="Custom pattern" />
      </div>
      <div className="grid gap-2 border-t border-hairline pt-(--grid-gap)">
        <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
          <span className="text-(length:--type-caption) font-medium text-foreground">Available tokens</span>
          <span className="text-(length:--type-caption) text-muted-foreground">Insert values into the pattern.</span>
        </div>
        <div className="flex flex-wrap gap-1.5">
          {tokens.map((token) => (
            <Button key={token.value} type="button" variant="outline" size="sm" onClick={() => insertToken(token.value)}>
              {token.label}
            </Button>
          ))}
        </div>
      </div>
    </div>
  );
}

export function namingStyleLabel(kind: NamingFormatKind, value: string) {
  const preset = PRESETS[kind].find((option) => option.value === value);
  if (!preset) return value.trim() ? "Custom pattern" : "Choose a style";
  if (preset.label === "Title and year") return "Title + year";
  if (preset.label === "Title, year, IMDb" || preset.label === "Title, year, TVDb") return "Title + ID";
  return preset.label;
}

/** Radarr/Sonarr's five colon-handling modes (#759); "dash" also matches the plain "Replace Illegal Characters" behaviour every other illegal character gets. */
export type ColonReplacementMode = "smart" | "delete" | "dash" | "space-dash" | "space-dash-space";

const ILLEGAL_FILENAME_CHARACTERS = new Set(["<", ">", ":", '"', "/", "\\", "|", "?", "*"]);

/**
 * Mirrors NamingTemplateRenderer.SanitizeSegment (#757, #759) closely enough
 * to preview it: the same illegal-character set, the same off-switch, the
 * same five colon modes. Used only to demonstrate the setting in this form —
 * the server remains the source of truth for what an import actually writes.
 */
export function sanitizeIllegalCharacters(
  value: string,
  replaceIllegalCharacters: boolean,
  colonReplacementMode: ColonReplacementMode = "smart"
): string {
  let result = "";
  for (let index = 0; index < value.length; index++) {
    const character = value[index];
    if (character === ":") {
      if (!replaceIllegalCharacters) continue;
      const nextIsSpace = value[index + 1] === " ";
      switch (colonReplacementMode) {
        case "delete":
          break;
        case "dash":
          result += "-";
          break;
        case "space-dash":
          result += " -";
          break;
        case "space-dash-space":
          result += " - ";
          break;
        case "smart":
        default:
          if (nextIsSpace) {
            result += " - ";
            index++;
          } else {
            result += "-";
          }
          break;
      }
      continue;
    }
    if (ILLEGAL_FILENAME_CHARACTERS.has(character)) {
      if (replaceIllegalCharacters) result += "-";
      continue;
    }
    result += character;
  }
  return result.trim();
}

export function previewNamingFormat(format: string) {
  return format
    .replaceAll("{Movie Title}", "Arrival")
    .replaceAll("{Release Year}", "2016")
    .replaceAll("{IMDb ID}", "tt2543164")
    .replaceAll("{Quality Profile}", "HD")
    .replaceAll("{Series Title}", "Severance")
    .replaceAll("{Series Year}", "2022")
    .replaceAll("{TVDb ID}", "371980")
    .replaceAll("{Network}", "Apple TV+")
    .replaceAll("{Season:00}", "01")
    .replaceAll("{season:00}", "01")
    .replaceAll("{Episode:00}", "01")
    .replaceAll("{episode:00}", "01")
    .replaceAll("{Episode Title}", "Good News About Hell")
    .replaceAll("{Quality}", "WEB 1080p")
    .replaceAll("{Custom Formats}", "AMZN")
    .replaceAll("{Release Group}", "FLUX")
    .replaceAll("{Edition}", "IMAX")
    .replaceAll("{Genre}", "Sci-Fi")
    .replaceAll("{Tag}", "premium")
    .replaceAll("{Air-Date}", "2022-09-30")
    .replaceAll("{Air Date}", "2022-09-30")
    .replaceAll("{absolute:000}", "042")
    .replaceAll("{Original Title}", "El Laberinto del Fauno")
    .replaceAll("{Original Filename}", "arrival.2016.1080p.web-dl-flux")
    .replaceAll("{Release Hash}", "a1b2c3d4")
    .replaceAll("{MediaInfo VideoCodec}", "H.264")
    .replaceAll("{MediaInfo AudioCodec}", "DTS-HD")
    .replaceAll("{MediaInfo AudioChannels}", "5.1")
    .replaceAll("{MediaInfo VideoDynamicRangeType}", "HDR10")
    .replaceAll("{MediaInfo VideoBitDepth}", "10-bit");
}
