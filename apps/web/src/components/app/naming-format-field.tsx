import { useState } from "react";
import { Check, ChevronDown, Code2, Wand2 } from "lucide-react";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { cn } from "../../lib/utils";

type NamingFormatKind = "movie-folder" | "series-folder" | "episode-file" | "destination-movie" | "destination-series";

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
  className
}: NamingFormatFieldProps) {
  const presets = PRESETS[kind];
  const tokens = TOKENS[kind];
  const selectedPreset = presets.find((preset) => preset.value === value);
  const example = selectedPreset?.hint ?? previewFormat(value || placeholder || "");
  const [advancedOpen, setAdvancedOpen] = useState(!selectedPreset);

  function insertToken(token: string) {
    const separator = value.trim().length === 0 || value.endsWith(" ") || value.endsWith("\\") ? "" : " ";
    onChange(`${value}${separator}${token}`);
  }

  return (
    <div className={cn("space-y-3", className)}>
      <div className="grid gap-2 lg:grid-cols-3">
        {presets.map((preset) => {
          const active = preset.value === value;
          const recommended = preset === presets[0];
          return (
            <button
              key={preset.value}
              type="button"
              onClick={() => onChange(preset.value)}
              className={cn(
                "group rounded-xl border px-3 py-2.5 text-left transition-all",
                active
                  ? "border-primary/35 bg-primary/8 text-foreground"
                  : "border-hairline bg-background/25 text-muted-foreground hover:border-primary/25 hover:bg-surface-2"
              )}
            >
              <span className="flex items-center justify-between gap-2">
                <span className="flex min-w-0 items-center gap-2">
                  <span className="truncate text-sm font-semibold text-foreground">{preset.label}</span>
                  {recommended ? (
                    <span className="rounded-full border border-primary/25 bg-primary/10 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-primary">
                      Recommended
                    </span>
                  ) : null}
                </span>
                {active ? <Check className="h-4 w-4 text-primary" /> : null}
              </span>
              <span className="mt-1 block truncate text-xs text-muted-foreground">{preset.hint}</span>
            </button>
          );
        })}
      </div>

      <div className="rounded-xl border border-primary/15 bg-primary/5 px-3 py-2 text-sm text-muted-foreground">
        <span className="font-semibold text-foreground">Example:</span>{" "}
        <span className="font-mono text-foreground">{example || "Choose a preset or add tokens."}</span>
      </div>

      <div className="rounded-xl border border-hairline bg-background/25">
        <button
          type="button"
          onClick={() => setAdvancedOpen((open) => !open)}
          className="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm text-muted-foreground hover:text-foreground"
        >
          <span className="flex items-center gap-2">
            <Code2 className="h-3.5 w-3.5" />
            Advanced pattern and tokens
          </span>
          <ChevronDown className={cn("h-4 w-4 transition-transform", advancedOpen && "rotate-180")} />
        </button>

        {advancedOpen ? (
          <div className="border-t border-hairline p-3">
            <Input
              value={value}
              onChange={(event) => onChange(event.target.value)}
              placeholder={placeholder}
              className="font-mono"
            />
            <div className="mt-3 flex flex-wrap gap-1.5">
              {tokens.map((token) => (
                <Button key={token.value} type="button" variant="outline" size="sm" onClick={() => insertToken(token.value)}>
                  <Wand2 className="h-3.5 w-3.5" />
                  {token.label}
                </Button>
              ))}
            </div>
          </div>
        ) : null}
      </div>
    </div>
  );
}

function previewFormat(format: string) {
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
