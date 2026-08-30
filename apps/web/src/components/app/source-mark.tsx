/**
 * A rating source, wearing its own colours.
 *
 * <p>Every score on the page was labelled in the same grey uppercase type —
 * TMDB, IMDB, RT, METACRITIC — so telling four numbers apart meant reading four
 * words, when the whole reason for showing four is a glance. Radarr and Sonarr
 * both draw the marks; James: <i>"why are we not using a TMDB or IMDB logo for
 * our stuff"</i>.</p>
 *
 * <p>These are drawn, not fetched: each is a coloured badge in the source's own
 * palette with its own wordmark, rendered inline. That matters twice over —
 * nothing on this page reaches out to a third-party asset host to draw itself,
 * and the badge is a font-relative box so it scales with the line it sits on
 * rather than being a bitmap that goes soft. A source Deluno does not have a
 * mark for falls back to its label, which is what every one of them used to
 * be.</p>
 */
import { cn } from "../../lib/utils";

interface SourceMarkProps {
  /** `tmdb`, `imdb`, `rotten_tomatoes`, `metacritic`. */
  source: string;
  /** Shown when there is no mark for this source, and as the accessible name. */
  label: string;
  className?: string;
}

/** Brand colours, and the word each one actually writes. */
const MARKS: Record<string, { text: string; className: string }> = {
  // TMDb's own palette: the teal-to-cyan sweep from its wordmark.
  tmdb: { text: "TMDB", className: "bg-gradient-to-r from-[#0d253f] via-[#01b4e4] to-[#90cea1] text-white" },
  imdb: { text: "IMDb", className: "bg-[#f5c518] text-black" },
  rotten_tomatoes: { text: "RT", className: "bg-[#fa320a] text-white" },
  metacritic: { text: "MC", className: "bg-[#ffcc33] text-black" }
};

export function SourceMark({ source, label, className }: SourceMarkProps) {
  const mark = MARKS[source];

  if (!mark) {
    return (
      <span className={cn("text-[length:var(--type-caption)] font-bold uppercase tracking-[0.12em] text-muted-foreground", className)}>
        {label}
      </span>
    );
  }

  return (
    <span
      // `title` rather than visually-hidden text: the wordmark IS the name, so a
      // screen reader reading "TMDB" is reading the right thing already.
      title={label}
      className={cn(
        "inline-flex shrink-0 select-none items-center rounded-[0.2em] px-[0.45em] py-[0.1em]",
        "text-[length:var(--type-caption)] font-black leading-none tracking-tight",
        mark.className,
        className
      )}
    >
      {mark.text}
    </span>
  );
}
