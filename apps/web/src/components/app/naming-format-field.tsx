import { useState } from "react";
import { Check } from "lucide-react";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { cn } from "../../lib/utils";

export type NamingFormatKind = "movie-folder" | "series-folder" | "episode-file" | "destination-movie" | "destination-series";

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
  "episode-file": [
    { label: "Standard episode", value: "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}", hint: "Severance - S01E01 - Good News About Hell", description: "Best default. Human-readable and compatible with media servers." },
    { label: "With quality", value: "{Series Title} - S{Season:00}E{Episode:00} - {Episode Title} [{Quality}]", hint: "Severance - S01E01 - Good News About Hell [WEB-DL 1080p]", description: "Shows the imported quality directly in the filename." },
    { label: "Episode code only", value: "S{Season:00}E{Episode:00} - {Episode Title}", hint: "S01E01 - Good News About Hell", description: "Compact. Useful when files already sit inside the series folder." }
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
    { label: "Quality", value: "{Quality}" }
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
      description: "Use tokens to create a format that fits your library."
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
        <span className="text-[length:var(--type-caption)] font-medium text-foreground">Format</span>
        <span className="text-[length:var(--type-caption)] text-muted-foreground">Choose how Deluno names these files and folders.</span>
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
                "flex w-full items-start gap-3 border-b border-hairline px-3.5 py-3 text-left transition-colors last:border-b-0 focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
                active ? "bg-primary/[0.08]" : "hover:bg-surface-2/70"
              )}
            >
              <span className={cn("mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border", active ? "border-primary bg-primary text-primary-foreground" : "border-hairline bg-background text-transparent")}>
                <Check aria-hidden className="h-3 w-3" />
              </span>
              <span className="min-w-0 flex-1">
                <span className={cn("flex items-center gap-2 text-[length:var(--type-caption)] font-semibold", active ? "text-primary" : "text-foreground")}>
                  <span className="truncate">{option.label}</span>
                  {index === 0 ? <span className="shrink-0 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-primary/70">Recommended</span> : null}
                </span>
                <span className="mt-0.5 block text-[length:var(--type-caption)] leading-snug text-muted-foreground">{option.description}</span>
              </span>
              {showExamples ? (
                <span className="hidden max-w-[42%] shrink-0 text-right md:block">
                  <span className="block text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-muted-foreground/70">Example</span>
                  <code className="mt-0.5 block truncate text-[length:var(--type-caption)] text-foreground">{option.hint}</code>
                </span>
              ) : null}
            </button>
          );
        })}
      </div>

      {showExamples ? (
        <div className="flex min-w-0 items-baseline gap-3 text-[length:var(--type-caption)]">
          <span className="shrink-0 font-semibold uppercase tracking-[0.08em] text-muted-foreground/70">Preview</span>
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
    <div className="grid gap-[var(--grid-gap)]">
      <div className="grid gap-1.5">
        <span className="text-[length:var(--type-caption)] font-medium text-foreground">Pattern</span>
        <Input value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} aria-label="Custom pattern" />
      </div>
      <div className="grid gap-2 border-t border-hairline pt-[var(--grid-gap)]">
        <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
          <span className="text-[length:var(--type-caption)] font-medium text-foreground">Available tokens</span>
          <span className="text-[length:var(--type-caption)] text-muted-foreground">Insert values into the pattern.</span>
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
    .replaceAll("{Quality}", "WEB-DL 1080p")
    .replaceAll("{Genre}", "Sci-Fi")
    .replaceAll("{Tag}", "premium");
}
