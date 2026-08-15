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
  | "discover"
  | "recovery"
  | "system";

/**
 * The Deluno navigation language. These are product marks, not an icon-library
 * grab bag: every mark uses the same rounded-path geometry and the same media
 * flow vocabulary (collection → decision → destination).
 */
export function DelunoNavGlyph({ kind, className }: { kind: DelunoNavGlyphKind; className?: string }) {
  const shared = {
    stroke: "currentColor",
    strokeWidth: 1.75,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const
  };

  return (
    <svg viewBox="0 0 24 24" fill="none" aria-hidden="true" className={cn("h-5 w-5", className)}>
      {kind === "dashboard" ? <>
        <rect x="3.5" y="3.5" width="7" height="7" rx="2" {...shared} />
        <rect x="13.5" y="3.5" width="7" height="7" rx="2" {...shared} />
        <rect x="3.5" y="13.5" width="7" height="7" rx="2" {...shared} />
        <path d="M15.5 17h5M18 14.5v5" {...shared} />
      </> : null}
      {kind === "movies" ? <>
        <rect x="3" y="4" width="18" height="16" rx="3" {...shared} />
        <path d="M7 4v16M17 4v16M3 9h4M17 9h4M3 15h4M17 15h4" {...shared} />
        <path d="m10.5 10 4 2-4 2v-4Z" fill="currentColor" stroke="none" />
      </> : null}
      {kind === "shows" ? <>
        <rect x="4" y="5" width="13" height="11" rx="2.5" {...shared} />
        <path d="M8 19h10a2 2 0 0 0 2-2V9M7.5 8.5h5M7.5 12h3" {...shared} />
        <circle cx="15" cy="13" r="1.5" fill="currentColor" stroke="none" />
      </> : null}
      {kind === "schedule" ? <>
        <rect x="4" y="5" width="16" height="15" rx="3" {...shared} />
        <path d="M8 3.5v3M16 3.5v3M4 10h16M8 14h.01M12 14h.01M16 14h.01M12 17h.01" {...shared} />
      </> : null}
      {kind === "transfers" ? <>
        <path d="M4 7.5h10.5a3 3 0 0 1 3 3V12" {...shared} />
        <path d="m15 9 2.5 3L15 15" {...shared} />
        <path d="M20 16.5H9.5a3 3 0 0 1-3-3V12" {...shared} />
        <path d="m9 15-2.5-3L9 9" {...shared} />
      </> : null}
      {kind === "automation" ? <>
        <circle cx="6" cy="7" r="2" {...shared} />
        <circle cx="18" cy="7" r="2" {...shared} />
        <circle cx="12" cy="17" r="2" {...shared} />
        <path d="m7.7 8.1 2.9 6.1M16.3 8.1l-2.9 6.1M8 7h8" {...shared} />
      </> : null}
      {kind === "activity" ? <>
        <path d="M4 17h3l2.1-7 3.4 10 2.2-6H20" {...shared} />
        <circle cx="4" cy="17" r="1.25" fill="currentColor" stroke="none" />
        <circle cx="20" cy="14" r="1.25" fill="currentColor" stroke="none" />
      </> : null}
      {kind === "setup" ? <>
        <rect x="3.5" y="4" width="6.5" height="6.5" rx="2" {...shared} />
        <rect x="14" y="13.5" width="6.5" height="6.5" rx="2" {...shared} />
        <path d="M10 7.25h2a3 3 0 0 1 3 3v3.25M8.2 16.75h3.3a3.5 3.5 0 0 0 3.5-3.5v-1" {...shared} />
        <path d="m12.4 13.1 2.6 2.6 2.6-2.6" {...shared} />
      </> : null}
      {kind === "library" ? <>
        <path d="M3.5 7.5h6l1.8 2h9.2v8.2A2.3 2.3 0 0 1 18.2 20H5.8a2.3 2.3 0 0 1-2.3-2.3V7.5Z" {...shared} />
        <path d="M3.5 10h17M7.5 14h4M7.5 17h7" {...shared} />
      </> : null}
      {kind === "connections" ? <>
        <circle cx="6" cy="8" r="2.25" {...shared} />
        <circle cx="18" cy="6" r="2.25" {...shared} />
        <circle cx="15" cy="17" r="2.25" {...shared} />
        <path d="m7.9 8.8 7.9-2M7.7 9.7l5.5 5.7M17.6 8l-1.8 6.8" {...shared} />
      </> : null}
      {kind === "plans" ? <>
        <path d="M5 5.5h14M5 12h14M5 18.5h14" {...shared} />
        <circle cx="8" cy="5.5" r="1.75" fill="currentColor" stroke="none" />
        <circle cx="15" cy="12" r="1.75" fill="currentColor" stroke="none" />
        <circle cx="11" cy="18.5" r="1.75" fill="currentColor" stroke="none" />
      </> : null}
      {kind === "quality" ? <>
        <path d="M12 3.5 19.5 8v8L12 20.5 4.5 16V8L12 3.5Z" {...shared} />
        <path d="m12 8 1.2 2.5 2.8.4-2 2 .5 2.8-2.5-1.3-2.5 1.3.5-2.8-2-2 2.8-.4L12 8Z" {...shared} />
      </> : null}
      {kind === "discover" ? <>
        <circle cx="12" cy="12" r="8" {...shared} />
        <path d="m15.7 8.3-2 5.4-5.4 2 2-5.4 5.4-2Z" {...shared} />
        <circle cx="12" cy="12" r="1" fill="currentColor" stroke="none" />
      </> : null}
      {kind === "recovery" ? <>
        <path d="M19 9a7.5 7.5 0 1 0 .2 6" {...shared} />
        <path d="M19 4.5V9h-4.5" {...shared} />
        <path d="M8.5 15.5h7M12 12v7" {...shared} />
      </> : null}
      {kind === "system" ? <>
        <path d="M12 3.5 19 7v5c0 4.4-2.9 7.4-7 8.5-4.1-1.1-7-4.1-7-8.5V7l7-3.5Z" {...shared} />
        <path d="M9.5 12h5M12 9.5v5" {...shared} />
      </> : null}
    </svg>
  );
}
