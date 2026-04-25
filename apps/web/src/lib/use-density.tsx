/**
 * Display density mode.
 *
 * Persisted values stay backward-compatible with existing installs:
 * compact = Compact, comfortable = Balanced, spacious = Comfortable,
 * expanded = Cinematic.
 */

import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";

export type Density = "compact" | "comfortable" | "spacious" | "expanded";

const STORAGE_KEY = "deluno-density";
const DEFAULT: Density = "comfortable";

export const DENSITY_LABELS: Record<Density, string> = {
  compact: "Compact",
  comfortable: "Balanced",
  spacious: "Comfortable",
  expanded: "Cinematic"
};

export function isDensity(value: unknown): value is Density {
  return value === "compact" || value === "comfortable" || value === "spacious" || value === "expanded";
}

export function densityDisplayName(value: string | null | undefined) {
  return isDensity(value) ? DENSITY_LABELS[value] : DENSITY_LABELS[DEFAULT];
}

interface DensityContextValue {
  density: Density;
  setDensity: (d: Density) => void;
}

const DensityContext = createContext<DensityContextValue | null>(null);

function readStored(): Density {
  if (typeof window === "undefined") return DEFAULT;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (isDensity(raw)) return raw;
  } catch {
    /* noop */
  }
  return DEFAULT;
}

export function DensityProvider({ children }: { children: ReactNode }) {
  const [density, setDensityState] = useState<Density>(() => readStored());

  useEffect(() => {
    document.documentElement.dataset.density = density;
    try {
      window.localStorage.setItem(STORAGE_KEY, density);
    } catch {
      /* noop */
    }
  }, [density]);

  const setDensity = useCallback((d: Density) => setDensityState(d), []);

  return (
    <DensityContext.Provider value={{ density, setDensity }}>
      {children}
    </DensityContext.Provider>
  );
}

export function useDensity() {
  const ctx = useContext(DensityContext);
  if (!ctx) {
    return {
      density: DEFAULT,
      setDensity: () => {
        /* noop: provider missing */
      }
    } as DensityContextValue;
  }
  return ctx;
}
