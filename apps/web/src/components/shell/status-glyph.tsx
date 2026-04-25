/**
 * Deluno custom status glyphs.
 *
 * A curated set of hand-drawn icons for domain-specific states:
 * health, queue activity, library richness, monitoring shield,
 * indexer pulse, and disk usage. Drawn on a 20×20 grid with
 * consistent 1.6 stroke weights so they sit next to Lucide icons
 * without visual friction.
 *
 * All glyphs inherit `currentColor` and pick up `hsl(var(--primary))`
 * through the enclosing `text-*` class. Dual-tone glyphs use
 * `currentColor` for strokes and a low-alpha `currentColor` fill
 * so the accent system recolours them automatically.
 */

import * as React from "react";

export type StatusGlyphTone = "neutral" | "primary" | "success" | "warning" | "danger" | "info";

interface GlyphProps extends React.SVGProps<SVGSVGElement> {
  size?: number;
  tone?: StatusGlyphTone;
  strokeWidth?: number;
}

function base({
  size = 20,
  tone = "neutral",
  strokeWidth = 1.6,
  ...rest
}: GlyphProps) {
  const toneClass =
    tone === "primary" ? "text-primary" :
    tone === "success" ? "text-success" :
    tone === "warning" ? "text-warning" :
    tone === "danger"  ? "text-destructive" :
    tone === "info"    ? "text-info" :
    "text-muted-foreground";
  return {
    width: size,
    height: size,
    viewBox: "0 0 20 20",
    fill: "none",
    strokeWidth,
    stroke: "currentColor",
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    className: toneClass + (rest.className ? ` ${rest.className}` : ""),
    "aria-hidden": true,
    ...rest
  };
}

/* ══════════════════════════════════════════════════════
   HEALTH PULSE — heartbeat for system status tiles
══════════════════════════════════════════════════════ */
export function HealthPulseGlyph(props: GlyphProps) {
  const p = base(props);
  return (
    <svg {...p}>
      {/* Soft outer ring */}
      <circle cx="10" cy="10" r="8.5" opacity="0.18" fill="currentColor" stroke="none" />
      {/* Inner tile */}
      <circle cx="10" cy="10" r="6.25" opacity="0.38" fill="currentColor" stroke="none" />
      {/* Pulse waveform */}
      <path d="M4.5 10 H7 L8.25 7.25 L10.25 12.75 L11.75 9.25 L13 10 H15.5" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   MONITOR SHIELD — dual-layer crest with checkmark
══════════════════════════════════════════════════════ */
export function MonitorShieldGlyph(props: GlyphProps) {
  const p = base(props);
  return (
    <svg {...p}>
      <path
        d="M10 2.5 L3.75 4.75 V10 C3.75 14 6.75 16.5 10 17.5 C13.25 16.5 16.25 14 16.25 10 V4.75 Z"
        fill="currentColor"
        fillOpacity="0.16"
      />
      <path d="M10 2.5 L3.75 4.75 V10 C3.75 14 6.75 16.5 10 17.5 C13.25 16.5 16.25 14 16.25 10 V4.75 Z" />
      <path d="M7.25 9.75 L9.25 11.75 L12.75 7.75" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   QUEUE STACK — stacked strips representing an active queue
══════════════════════════════════════════════════════ */
export function QueueStackGlyph(props: GlyphProps) {
  const p = base(props);
  return (
    <svg {...p}>
      <rect x="3.25" y="4"   width="13.5" height="2.75" rx="1.25" fill="currentColor" fillOpacity="0.18" />
      <rect x="3.25" y="4"   width="13.5" height="2.75" rx="1.25" />
      <rect x="3.25" y="8.5" width="13.5" height="2.75" rx="1.25" fill="currentColor" fillOpacity="0.38" />
      <rect x="3.25" y="8.5" width="13.5" height="2.75" rx="1.25" />
      <rect x="3.25" y="13"  width="13.5" height="2.75" rx="1.25" />
      {/* Active dot on the middle row */}
      <circle cx="14" cy="9.85" r="0.9" fill="currentColor" stroke="none" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   LIBRARY CARDS — stacked poster tiles with play glyph
══════════════════════════════════════════════════════ */
export function LibraryRichGlyph(props: GlyphProps) {
  const p = base(props);
  return (
    <svg {...p}>
      {/* Back card */}
      <rect x="6" y="2.5" width="10.5" height="13" rx="1.75" fill="currentColor" fillOpacity="0.16" />
      <rect x="6" y="2.5" width="10.5" height="13" rx="1.75" />
      {/* Front card */}
      <rect x="3.5" y="5" width="10.5" height="13" rx="1.75" fill="currentColor" fillOpacity="0.38" />
      <rect x="3.5" y="5" width="10.5" height="13" rx="1.75" />
      {/* Play triangle */}
      <path d="M7.5 8.5 L10.75 11 L7.5 13.5 Z" fill="currentColor" stroke="none" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   INDEXER SIGNAL — concentric arcs with center node
══════════════════════════════════════════════════════ */
export function IndexerSignalGlyph(props: GlyphProps) {
  const p = base(props);
  return (
    <svg {...p}>
      <path d="M3.5 13 Q10 5 16.5 13" opacity="0.35" />
      <path d="M5.5 13 Q10 7.5 14.5 13" opacity="0.6" />
      <path d="M7.5 13 Q10 10.25 12.5 13" />
      <circle cx="10" cy="13" r="1.3" fill="currentColor" stroke="none" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   DISK USAGE — drum with level fill
══════════════════════════════════════════════════════ */
export function DiskDrumGlyph({
  percent = 0.66,
  ...props
}: GlyphProps & { percent?: number }) {
  const p = base(props);
  const level = Math.max(0, Math.min(1, percent));
  const yStart = 6 + (1 - level) * 8;
  return (
    <svg {...p}>
      {/* Drum body */}
      <ellipse cx="10" cy="5.25" rx="6" ry="1.75" fill="currentColor" fillOpacity="0.18" />
      <path
        d="M4 5.25 V14.75 Q4 16.5 10 16.5 Q16 16.5 16 14.75 V5.25"
        fill="currentColor"
        fillOpacity="0.1"
      />
      <ellipse cx="10" cy="5.25" rx="6" ry="1.75" />
      <path d="M4 5.25 V14.75 Q4 16.5 10 16.5 Q16 16.5 16 14.75 V5.25" />
      {/* Fill */}
      <path
        d={`M4 ${yStart} V14.75 Q4 16.5 10 16.5 Q16 16.5 16 14.75 V${yStart}`}
        fill="currentColor"
        fillOpacity="0.5"
        stroke="none"
      />
      <ellipse cx="10" cy={yStart} rx="6" ry="1.4" fill="currentColor" fillOpacity="0.9" stroke="none" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   SPARK BEAM — single energetic accent bolt
══════════════════════════════════════════════════════ */
export function SparkBeamGlyph(props: GlyphProps) {
  const p = base(props);
  return (
    <svg {...p}>
      <path
        d="M11 2.75 L4.5 11 H9.5 L8 17.25 L14.75 8.75 H9.75 Z"
        fill="currentColor"
        fillOpacity="0.2"
      />
      <path d="M11 2.75 L4.5 11 H9.5 L8 17.25 L14.75 8.75 H9.75 Z" />
    </svg>
  );
}
