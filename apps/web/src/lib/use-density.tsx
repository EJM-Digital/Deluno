/**
 * Display density mode — compact / comfortable / spacious.
 *
 * Controls row heights, card padding, and data-table tightness. The
 * mode is stored in localStorage and reflected on `<html data-density>`
 * so CSS (in index.css) can react globally without prop drilling.
 *
 * Usage:
 *   const { density, setDensity } = useDensity();
 *   <DensityProvider>…</DensityProvider>   // once at the app root
 */

import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";

export type Density = "compact" | "comfortable" | "spacious" | "expanded";

const STORAGE_KEY = "deluno-density";
const DEFAULT: Density = "comfortable";

interface DensityContextValue {
  density: Density;
  setDensity: (d: Density) => void;
}

const DensityContext = createContext<DensityContextValue | null>(null);

function readStored(): Density {
  if (typeof window === "undefined") return DEFAULT;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (raw === "compact" || raw === "comfortable" || raw === "spacious" || raw === "expanded") return raw;
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
        /* noop — provider missing */
      }
    } as DensityContextValue;
  }
  return ctx;
}
