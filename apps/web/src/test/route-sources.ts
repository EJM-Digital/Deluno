import { vi } from "vitest";

/**
 * What a screen that reads its data through the shared route cache gets in a
 * test: every source already answered, and a revalidate the test can watch.
 * Screens are tested on the data they are handed, not on the cache behind it
 * (that is route-cache.test.tsx's job).
 */
export function readyRouteSources<T>(data: T, revalidate: () => void = vi.fn()) {
  return {
    data,
    ready: true,
    pending: new Set<string>(),
    failed: [] as { name: string; label: string; message: string }[],
    blocked: false,
    isFetching: false,
    refetch: async () => { revalidate(); },
    revalidator: { revalidate, state: "idle" as const }
  };
}
