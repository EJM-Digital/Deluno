import { cn } from "../../lib/utils";

export type DelunoNavGlyphKind =
  | "dashboard"
  | "movies"
  | "shows"
  | "schedule"
  | "transfers"
  | "automation"
  | "activity"
  | "setup"
  | "library"
  | "connections"
  | "plans"
  | "quality"
  | "size"
  | "scoring"
  | "destinations"
  | "discover"
  | "search"
  | "recovery"
  | "subtitles"
  | "system";

/**
 * Deluno's product icon language from the selected specific icon pack.
 * Keep these line icons simple so they stay readable in nav, command, and setup UI.
 * The surrounding navigation item owns the colour so idle and selected states
 * cannot disagree with the area accent.
 */
export function DelunoNavGlyph({
  kind,
  className
}: {
  kind: DelunoNavGlyphKind;
  className?: string;
}) {
  const shared = {
    stroke: "currentColor",
    strokeWidth: 2.2,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const
  };
  const iconClassName = cn("h-5 w-5", className);

  return (
    <svg viewBox="0 0 24 24" fill="none" aria-hidden="true" className={iconClassName}>
      {/*
        A frame with two lines of text along its bottom edge — which is what a
        subtitle looks like, and stays legible at 20px where a speech bubble or a
        letter "CC" does not.
      */}
      {kind === "subtitles" ? <>
        <rect x="3" y="5" width="18" height="14" rx="2" {...shared} />
        <path d="M7 15h5" {...shared} />
        <path d="M15 15h2" {...shared} />
        <path d="M7 11h2" {...shared} />
        <path d="M12 11h5" {...shared} />
      </> : null}
      {kind === "dashboard" ? <>
        <path d="M4 13h6V4H4z" {...shared} />
        <path d="M14 20h6V4h-6z" {...shared} />
        <path d="M4 20h6v-3H4z" {...shared} />
      </> : null}
      {kind === "movies" ? <>
        <path d="M5 6h14v12H5z" {...shared} />
        <path d="M8 6v12" {...shared} />
        <path d="M16 6v12" {...shared} />
        <path d="M5 10h14" {...shared} />
        <path d="M5 14h14" {...shared} />
      </> : null}
      {kind === "shows" ? <>
        <rect x="4" y="5" width="16" height="11" rx="2" {...shared} />
        <path d="M9 20h6" {...shared} />
        <path d="M12 16v4" {...shared} />
      </> : null}
      {kind === "schedule" ? <>
        <path d="M7 7V4" {...shared} />
        <path d="M17 7V4" {...shared} />
        <rect x="4" y="6" width="16" height="14" rx="2" {...shared} />
        <path d="M4 11h16" {...shared} />
        <path d="M8 15h2" {...shared} />
        <path d="M14 15h2" {...shared} />
      </> : null}
      {kind === "transfers" ? <>
        <path d="M6 4h12v8H6z" {...shared} />
        <path d="M8 16h8" {...shared} />
        <path d="M12 12v8" {...shared} />
        <path d="M9 19l3 2 3-2" {...shared} />
      </> : null}
      {kind === "automation" ? <>
        <path d="M12 5v14" {...shared} />
        <path d="M5 12h14" {...shared} />
        <path d="M8 8l8 8" {...shared} />
        <path d="M16 8l-8 8" {...shared} />
      </> : null}
      {kind === "activity" ? <>
        <path d="M4 14h5l3-8 3 12 2-4h3" {...shared} />
        <path d="M4 20h16" {...shared} />
      </> : null}
      {kind === "setup" ? <>
        <path d="M3 7h7l2 2h9v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" {...shared} />
        <path d="M7 13h10" {...shared} />
        <path d="M7 16h6" {...shared} />
      </> : null}
      {kind === "library" ? <>
        <path d="M3 7h7l2 2h9v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" {...shared} />
        <path d="M7 13h10" {...shared} />
        <path d="M7 16h6" {...shared} />
      </> : null}
      {kind === "connections" ? <>
        <circle cx="6" cy="8" r="2" {...shared} />
        <circle cx="18" cy="16" r="2" {...shared} />
        <path d="M8 8h4a4 4 0 0 1 4 4v2" {...shared} />
        <path d="M18 6v5" {...shared} />
        <path d="M15.5 8.5L18 6l2.5 2.5" {...shared} />
      </> : null}
      {kind === "plans" ? <>
        <rect x="5" y="5" width="14" height="4" rx="1.5" {...shared} />
        <rect x="3" y="10" width="18" height="4" rx="1.5" {...shared} />
        <rect x="6" y="15" width="12" height="4" rx="1.5" {...shared} />
      </> : null}
      {kind === "quality" ? <>
        <circle cx="12" cy="12" r="8" {...shared} />
        <circle cx="12" cy="12" r="3" {...shared} />
        <path d="M12 4v3" {...shared} />
        <path d="M20 12h-3" {...shared} />
      </> : null}
      {kind === "size" ? <>
        <path d="M6 5v14" {...shared} />
        <path d="M18 5v14" {...shared} />
        <path d="M9 7h6" {...shared} />
        <path d="M9 17h6" {...shared} />
        <path d="M12 9v6" {...shared} />
      </> : null}
      {kind === "scoring" ? <>
        <path d="M4 18h16" {...shared} />
        <path d="M6 15l4-4 3 3 5-7" {...shared} />
        <path d="M18 7h-4" {...shared} />
        <path d="M18 7v4" {...shared} />
      </> : null}
      {kind === "destinations" ? <>
        <path d="M4 6h6l2 3h8v9H4z" {...shared} />
        <path d="M7 18c5-1 5-9 10-10" {...shared} />
        <path d="M17 8h-3" {...shared} />
        <path d="M17 8v3" {...shared} />
      </> : null}
      {kind === "discover" ? <>
        <circle cx="10" cy="10" r="5" {...shared} />
        <path d="M14 14l5 5" {...shared} />
        <path d="M18 7v4" {...shared} />
        <path d="M16 9h4" {...shared} />
      </> : null}
      {kind === "search" ? <>
        <circle cx="10" cy="10" r="5" {...shared} />
        <path d="M14 14l5 5" {...shared} />
        <path d="M18 7v4" {...shared} />
        <path d="M16 9h4" {...shared} />
      </> : null}
      {kind === "recovery" ? <>
        <path d="M12 5v14" {...shared} />
        <path d="M5 12h14" {...shared} />
        <path d="M8 8l8 8" {...shared} />
        <path d="M16 8l-8 8" {...shared} />
      </> : null}
      {kind === "system" ? <>
        <path d="M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z" {...shared} />
        <path d="M4 12h2" {...shared} />
        <path d="M18 12h2" {...shared} />
        <path d="M12 4v2" {...shared} />
        <path d="M12 18v2" {...shared} />
        <path d="M6.6 6.6 8 8" {...shared} />
        <path d="M16 16l1.4 1.4" {...shared} />
        <path d="M17.4 6.6 16 8" {...shared} />
        <path d="M8 16l-1.4 1.4" {...shared} />
      </> : null}
    </svg>
  );
}
