/**
 * Colour presentation for title marks.
 *
 * The non-colour glyph is always present. This preference only changes the
 * palette, so a copied screenshot remains understandable even when the
 * recipient has not enabled the preference.
 */
import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";

export type ColorMode = "standard" | "impaired";

const STORAGE_KEY = "deluno-color-mode";
const DEFAULT: ColorMode = "standard";

/**
 * The alternate values are applied as inline root variables rather than as a
 * second copy of the design tokens in CSS. That keeps the normal light/dark
 * theme tokens single-source while still allowing every consumer — including
 * inline SVG strokes and poster bars — to swap palette atomically.
 */
export const COLOR_IMPAIRED_MARK_PALETTE = {
  "--mark-missing": "0 76% 34%",
  "--mark-downloading": "220 82% 54%",
  "--mark-upgrade": "155 54% 63%",
  "--mark-quality-met": "43 100% 48%",
  "--mark-airing": "315 64% 36%",
  "--mark-upcoming": "270 62% 72%",
  "--mark-missing-surface": "0 76% 34%",
  "--mark-downloading-surface": "220 82% 44%",
  "--mark-upgrade-surface": "155 54% 38%",
  "--mark-airing-surface": "315 64% 40%",
  "--mark-upcoming-surface": "270 62% 52%",
  "--mark-leaf": "43 100% 58%",
  "--mark-leaf-high": "49 100% 86%",
  "--mark-leaf-deep": "42 100% 46%"
} as const;

function applyMarkPalette(mode: ColorMode) {
  const root = document.documentElement;
  for (const [property, value] of Object.entries(COLOR_IMPAIRED_MARK_PALETTE)) {
    if (mode === "impaired") root.style.setProperty(property, value);
    else root.style.removeProperty(property);
  }
}

export function isColorMode(value: unknown): value is ColorMode {
  return value === "standard" || value === "impaired";
}

interface ColorModeContextValue {
  colorMode: ColorMode;
  setColorMode: (mode: ColorMode) => void;
}

const ColorModeContext = createContext<ColorModeContextValue | null>(null);

function readStored(): ColorMode {
  if (typeof window === "undefined") return DEFAULT;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return isColorMode(raw) ? raw : DEFAULT;
  } catch {
    return DEFAULT;
  }
}

export function ColorModeProvider({ children }: { children: ReactNode }) {
  const [colorMode, setColorModeState] = useState<ColorMode>(() => readStored());

  useEffect(() => {
    document.documentElement.dataset.colorMode = colorMode;
    document.body.dataset.colorMode = colorMode;
    applyMarkPalette(colorMode);
    try {
      window.localStorage.setItem(STORAGE_KEY, colorMode);
    } catch {
      /* Storage is optional; the preference still applies for this session. */
    }
  }, [colorMode]);

  const setColorMode = useCallback((mode: ColorMode) => setColorModeState(mode), []);

  return (
    <ColorModeContext.Provider value={{ colorMode, setColorMode }}>
      {children}
    </ColorModeContext.Provider>
  );
}

export function useColorMode() {
  const context = useContext(ColorModeContext);
  if (!context) {
    return {
      colorMode: DEFAULT,
      setColorMode: () => {
        /* noop: provider missing */
      }
    } as ColorModeContextValue;
  }
  return context;
}
