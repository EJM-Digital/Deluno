/**
 * Hand-drawn SVG illustrations for empty, error, and zero-data states.
 *
 * All illustrations are accent-aware: they inherit `currentColor` and
 * pick up `hsl(var(--primary))` via CSS variables, so changing the
 * global accent re-tints every scene automatically.
 *
 * Design language:
 *   — 3D-stacked cards (film strips / tiles) that echo the Deluno mark
 *   — Floating dots as ambient "data particles"
 *   — Soft radial halo for a premium cinematic feel
 */

import * as React from "react";

interface IllustrationProps {
  className?: string;
  /** Scale down for inline use. @default 120 */
  size?: number;
}

/* ══════════════════════════════════════════════════════
   EMPTY LIBRARY — stacked film tiles with sparkle
══════════════════════════════════════════════════════ */
export function EmptyLibraryArt({ className, size = 140 }: IllustrationProps) {
  return (
    <svg
      width={size}
      height={size * 0.8}
      viewBox="0 0 180 144"
      fill="none"
      aria-hidden
      className={className}
    >
      <defs>
        <linearGradient id="el-card-a" x1="0" y1="0" x2="80" y2="100" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--primary))" stopOpacity="0.9" />
          <stop offset="1" stopColor="hsl(var(--primary-2))" stopOpacity="0.7" />
        </linearGradient>
        <linearGradient id="el-card-b" x1="0" y1="0" x2="0" y2="80" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--surface-2))" />
          <stop offset="1" stopColor="hsl(var(--surface-3))" />
        </linearGradient>
        <radialGradient id="el-halo" cx="90" cy="76" r="72" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--primary))" stopOpacity="0.18" />
          <stop offset="1" stopColor="hsl(var(--primary))" stopOpacity="0" />
        </radialGradient>
        <filter id="el-soft" x="-20%" y="-20%" width="140%" height="140%">
          <feGaussianBlur stdDeviation="2" />
        </filter>
      </defs>

      {/* Ambient halo */}
      <rect width="180" height="144" fill="url(#el-halo)" />

      {/* Back card — dimmed */}
      <g opacity="0.55" transform="rotate(-8 54 70)">
        <rect x="22" y="38" width="58" height="74" rx="10" fill="url(#el-card-b)" stroke="hsl(var(--hairline))" />
        <rect x="30" y="52" width="42" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.35)" />
        <rect x="30" y="60" width="28" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.22)" />
        <rect x="30" y="78" width="42" height="18" rx="3" fill="hsl(var(--muted-foreground) / 0.12)" />
      </g>

      {/* Middle card — dimmed */}
      <g opacity="0.75" transform="rotate(4 124 72)">
        <rect x="98" y="38" width="58" height="74" rx="10" fill="url(#el-card-b)" stroke="hsl(var(--hairline))" />
        <rect x="106" y="52" width="42" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.35)" />
        <rect x="106" y="60" width="28" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.22)" />
        <rect x="106" y="78" width="42" height="18" rx="3" fill="hsl(var(--muted-foreground) / 0.12)" />
      </g>

      {/* Front card — accent-filled */}
      <g filter="url(#el-soft)" opacity="0.35" transform="translate(0 4)">
        <rect x="56" y="28" width="68" height="86" rx="12" fill="hsl(var(--primary))" />
      </g>
      <g>
        <rect x="56" y="24" width="68" height="86" rx="12" fill="url(#el-card-a)" />
        <rect x="56" y="24" width="68" height="86" rx="12" fill="url(#el-card-a)" opacity="0.1" />
        {/* Inner play triangle */}
        <path
          d="M84 54L102 67L84 80V54Z"
          fill="white"
          fillOpacity="0.95"
        />
        {/* Film sprocket holes */}
        <rect x="62" y="32" width="6" height="3" rx="1" fill="white" fillOpacity="0.4" />
        <rect x="112" y="32" width="6" height="3" rx="1" fill="white" fillOpacity="0.4" />
        <rect x="62" y="99" width="6" height="3" rx="1" fill="white" fillOpacity="0.4" />
        <rect x="112" y="99" width="6" height="3" rx="1" fill="white" fillOpacity="0.4" />
      </g>

      {/* Sparkle dots — ambient data particles */}
      <circle cx="30" cy="24" r="2" fill="hsl(var(--primary))" opacity="0.7" />
      <circle cx="160" cy="30" r="1.5" fill="hsl(var(--primary))" opacity="0.5" />
      <circle cx="20" cy="120" r="1.5" fill="hsl(var(--primary))" opacity="0.4" />
      <circle cx="164" cy="118" r="2" fill="hsl(var(--primary))" opacity="0.55" />
      <circle cx="8"  cy="66" r="1" fill="hsl(var(--primary))" opacity="0.45" />
      <circle cx="174" cy="72" r="1" fill="hsl(var(--primary))" opacity="0.45" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   SEARCH ZERO — magnifier over a dimmed grid
══════════════════════════════════════════════════════ */
export function NoResultsArt({ className, size = 140 }: IllustrationProps) {
  return (
    <svg
      width={size}
      height={size * 0.8}
      viewBox="0 0 180 144"
      fill="none"
      aria-hidden
      className={className}
    >
      <defs>
        <radialGradient id="nr-halo" cx="102" cy="66" r="64" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--primary))" stopOpacity="0.18" />
          <stop offset="1" stopColor="hsl(var(--primary))" stopOpacity="0" />
        </radialGradient>
        <linearGradient id="nr-lens" x1="0" y1="0" x2="60" y2="60" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--primary))" />
          <stop offset="1" stopColor="hsl(var(--primary-2))" />
        </linearGradient>
      </defs>

      <rect width="180" height="144" fill="url(#nr-halo)" />

      {/* Dotted grid of items — ghostly */}
      <g opacity="0.4">
        {[0, 1, 2, 3, 4].map((col) =>
          [0, 1, 2].map((row) => (
            <circle
              key={`${col}-${row}`}
              cx={38 + col * 26}
              cy={40 + row * 24}
              r="4"
              fill="hsl(var(--muted-foreground) / 0.35)"
            />
          ))
        )}
      </g>

      {/* Magnifier */}
      <g transform="translate(62 42)">
        <circle cx="30" cy="30" r="28" fill="hsl(var(--background))" opacity="0.8" />
        <circle
          cx="30"
          cy="30"
          r="26"
          stroke="url(#nr-lens)"
          strokeWidth="5"
          fill="hsl(var(--surface-1) / 0.4)"
        />
        {/* Internal highlight */}
        <path
          d="M14 22 Q20 12 30 12"
          stroke="white"
          strokeOpacity="0.5"
          strokeWidth="2"
          strokeLinecap="round"
          fill="none"
        />
        {/* Handle */}
        <rect
          x="48"
          y="48"
          width="28"
          height="8"
          rx="4"
          transform="rotate(40 48 48)"
          fill="url(#nr-lens)"
        />
        {/* Glint */}
        <circle cx="40" cy="20" r="2" fill="white" opacity="0.9" />
      </g>

      {/* Sparkles */}
      <circle cx="30" cy="20" r="1.5" fill="hsl(var(--primary))" opacity="0.6" />
      <circle cx="150" cy="110" r="2" fill="hsl(var(--primary))" opacity="0.55" />
      <circle cx="170" cy="26" r="1" fill="hsl(var(--primary))" opacity="0.5" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   ERROR — broken film strip with warning glow
══════════════════════════════════════════════════════ */
export function ErrorArt({ className, size = 140 }: IllustrationProps) {
  return (
    <svg
      width={size}
      height={size * 0.8}
      viewBox="0 0 180 144"
      fill="none"
      aria-hidden
      className={className}
    >
      <defs>
        <radialGradient id="er-halo" cx="90" cy="72" r="68" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--destructive))" stopOpacity="0.18" />
          <stop offset="1" stopColor="hsl(var(--destructive))" stopOpacity="0" />
        </radialGradient>
        <linearGradient id="er-strip" x1="0" y1="0" x2="0" y2="100" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--surface-2))" />
          <stop offset="1" stopColor="hsl(var(--surface-3))" />
        </linearGradient>
      </defs>

      <rect width="180" height="144" fill="url(#er-halo)" />

      {/* Left fragment */}
      <g transform="rotate(-5 52 74)">
        <rect x="18" y="44" width="60" height="58" rx="10" fill="url(#er-strip)" stroke="hsl(var(--hairline))" />
        <rect x="26" y="54" width="20" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.35)" />
        <rect x="26" y="62" width="38" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.22)" />
        <rect x="26" y="78" width="44" height="16" rx="3" fill="hsl(var(--muted-foreground) / 0.15)" />
        {/* Jagged break edge */}
        <path
          d="M78 44 L72 56 L80 66 L72 78 L78 92 L72 102 L78 102 L80 44Z"
          fill="hsl(var(--background))"
          stroke="hsl(var(--destructive) / 0.5)"
          strokeWidth="1"
        />
      </g>

      {/* Right fragment */}
      <g transform="rotate(6 128 72)">
        <rect x="102" y="44" width="60" height="58" rx="10" fill="url(#er-strip)" stroke="hsl(var(--hairline))" />
        <rect x="116" y="54" width="32" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.35)" />
        <rect x="116" y="62" width="20" height="3" rx="1.5" fill="hsl(var(--muted-foreground) / 0.22)" />
        <rect x="110" y="78" width="44" height="16" rx="3" fill="hsl(var(--muted-foreground) / 0.15)" />
        {/* Jagged break edge */}
        <path
          d="M102 44 L108 56 L100 66 L108 78 L102 92 L108 102 L102 102 L100 44Z"
          fill="hsl(var(--background))"
          stroke="hsl(var(--destructive) / 0.5)"
          strokeWidth="1"
        />
      </g>

      {/* Central warning bolt */}
      <g transform="translate(84 54)">
        <circle cx="8" cy="18" r="18" fill="hsl(var(--destructive))" opacity="0.15" />
        <circle cx="8" cy="18" r="12" fill="hsl(var(--destructive))" opacity="0.2" />
        <path
          d="M10 6 L2 20 L8 20 L6 30 L14 16 L8 16 L10 6Z"
          fill="hsl(var(--destructive))"
        />
      </g>

      {/* Residual sparks */}
      <circle cx="30" cy="24" r="1.5" fill="hsl(var(--destructive))" opacity="0.55" />
      <circle cx="160" cy="26" r="1.5" fill="hsl(var(--destructive))" opacity="0.55" />
      <circle cx="24" cy="120" r="1" fill="hsl(var(--destructive))" opacity="0.5" />
      <circle cx="158" cy="122" r="1" fill="hsl(var(--destructive))" opacity="0.5" />
    </svg>
  );
}

/* ══════════════════════════════════════════════════════
   OFFLINE — disconnected signal tower
══════════════════════════════════════════════════════ */
export function OfflineArt({ className, size = 140 }: IllustrationProps) {
  return (
    <svg
      width={size}
      height={size * 0.8}
      viewBox="0 0 180 144"
      fill="none"
      aria-hidden
      className={className}
    >
      <defs>
        <radialGradient id="of-halo" cx="90" cy="72" r="68" gradientUnits="userSpaceOnUse">
          <stop stopColor="hsl(var(--muted-foreground))" stopOpacity="0.1" />
          <stop offset="1" stopColor="hsl(var(--muted-foreground))" stopOpacity="0" />
        </radialGradient>
      </defs>

      <rect width="180" height="144" fill="url(#of-halo)" />

      {/* Signal arcs — faded */}
      <g stroke="hsl(var(--muted-foreground))" strokeLinecap="round" fill="none" opacity="0.45">
        <path d="M60 70 Q90 48 120 70"   strokeWidth="3" strokeDasharray="2 4" />
        <path d="M50 78 Q90 36 130 78"   strokeWidth="3" strokeDasharray="2 4" opacity="0.65" />
        <path d="M40 86 Q90 22 140 86"   strokeWidth="3" strokeDasharray="2 4" opacity="0.4" />
      </g>

      {/* Tower */}
      <g>
        <rect x="86" y="70" width="8" height="48" rx="2" fill="hsl(var(--muted-foreground) / 0.8)" />
        <circle cx="90" cy="66" r="6" fill="hsl(var(--muted-foreground) / 0.9)" />
        <circle cx="90" cy="66" r="2.5" fill="hsl(var(--destructive))" />
        <rect x="76" y="116" width="28" height="4" rx="1" fill="hsl(var(--muted-foreground) / 0.7)" />
      </g>

      {/* Diagonal break line */}
      <line
        x1="40"
        y1="110"
        x2="140"
        y2="30"
        stroke="hsl(var(--destructive) / 0.7)"
        strokeWidth="3"
        strokeLinecap="round"
      />
    </svg>
  );
}
