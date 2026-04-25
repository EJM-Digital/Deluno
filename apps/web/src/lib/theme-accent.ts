/**
 * Accent theme registry + helpers.
 * Applies `data-accent` attribute to <html> and persists choice to localStorage.
 * The CSS for each accent lives in `apps/web/src/index.css` under `[data-accent="..."]`.
 */

export type AccentKey =
  | "cobalt"
  | "indigo"
  | "violet"
  | "crimson"
  | "rose"
  | "amber"
  | "lime"
  | "emerald"
  | "sky"
  | "graphite";

export interface AccentDef {
  key: AccentKey;
  label: string;
  description: string;
  /** Swatch swatches (light, dark) in HSL */
  swatch: { light: string; dark: string };
}

export const ACCENTS: AccentDef[] = [
  {
    key: "cobalt",
    label: "Cobalt",
    description: "Signature premium blue — Linear, Arc, Stripe.",
    swatch: { light: "hsl(222 85% 56%)", dark: "hsl(222 92% 66%)" }
  },
  {
    key: "indigo",
    label: "Indigo",
    description: "Deep enterprise blue-purple — classic Stripe.",
    swatch: { light: "hsl(238 72% 52%)", dark: "hsl(238 88% 70%)" }
  },
  {
    key: "violet",
    label: "Violet",
    description: "Bold 2026 magenta-leaning indigo.",
    swatch: { light: "hsl(258 78% 54%)", dark: "hsl(258 88% 70%)" }
  },
  {
    key: "crimson",
    label: "Crimson",
    description: "Serious saturated red — GitLab, Atlassian depth.",
    swatch: { light: "hsl(354 78% 48%)", dark: "hsl(354 86% 62%)" }
  },
  {
    key: "rose",
    label: "Rose",
    description: "Couture pink for confident brand statements.",
    swatch: { light: "hsl(346 82% 56%)", dark: "hsl(346 92% 68%)" }
  },
  {
    key: "amber",
    label: "Amber",
    description: "Warm editorial gold for hospitality & lifestyle.",
    swatch: { light: "hsl(32 92% 50%)", dark: "hsl(36 96% 62%)" }
  },
  {
    key: "lime",
    label: "Lime",
    description: "Electric neon green — Vercel, Supabase energy.",
    swatch: { light: "hsl(96 74% 42%)", dark: "hsl(96 78% 56%)" }
  },
  {
    key: "emerald",
    label: "Emerald",
    description: "Original Deluno teal — kept for nostalgia.",
    swatch: { light: "hsl(168 82% 40%)", dark: "hsl(168 76% 52%)" }
  },
  {
    key: "sky",
    label: "Sky",
    description: "Airy cyan — fresh docs & data-viz palette.",
    swatch: { light: "hsl(198 88% 46%)", dark: "hsl(198 92% 62%)" }
  },
  {
    key: "graphite",
    label: "Graphite",
    description: "Monochrome with an electric-blue highlight.",
    swatch: { light: "hsl(224 14% 22%)", dark: "hsl(210 18% 92%)" }
  }
];

export const DEFAULT_ACCENT: AccentKey = "cobalt";
export const ACCENT_STORAGE_KEY = "deluno-accent";

export function resolveInitialAccent(): AccentKey {
  if (typeof window === "undefined") return DEFAULT_ACCENT;
  try {
    const stored = localStorage.getItem(ACCENT_STORAGE_KEY) as AccentKey | null;
    if (stored && ACCENTS.some((a) => a.key === stored)) return stored;
  } catch {
    /* ignore */
  }
  return DEFAULT_ACCENT;
}

export function applyAccent(key: AccentKey) {
  if (typeof document === "undefined") return;
  document.documentElement.setAttribute("data-accent", key);
}

export function persistAccent(key: AccentKey) {
  try {
    localStorage.setItem(ACCENT_STORAGE_KEY, key);
  } catch {
    /* ignore */
  }
}
