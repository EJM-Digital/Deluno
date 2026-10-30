/**
 * Getting a screen ready before it is asked for (#1081).
 *
 * Every screen is its own chunk. A click used to start downloading the chunk,
 * and only then the screen's data. Now the chunk is fetched as soon as the
 * pointer or keyboard focus reaches the sidebar link (and for the main screens
 * once the app is idle after sign-in), and on hover the screen's own loader is
 * asked to start its queries, which lands them in the shared cache.
 */

type LoaderModule = Record<string, unknown>;

interface PrefetchTarget {
  /** True for the addresses this entry opens. */
  match: (path: string) => boolean;
  load: () => Promise<LoaderModule>;
  /** The export that starts the screen's queries and returns at once. */
  startExport?: string;
}

const targets: PrefetchTarget[] = [
  { match: (path) => path === "/", load: () => import("../routes/dashboard-page"), startExport: "dashboardLoader" },
  { match: (path) => path === "/movies", load: () => import("../routes/library-page"), startExport: "moviesLoader" },
  { match: (path) => path === "/tv", load: () => import("../routes/library-page"), startExport: "showsLoader" },
  { match: (path) => path === "/queue", load: () => import("../routes/queue-page"), startExport: "queueLoader" },
  { match: (path) => path.startsWith("/indexers") && !path.startsWith("/indexers/history") && !path.startsWith("/indexers/subtitle") && !path.startsWith("/indexers/media-servers") && !path.startsWith("/indexers/companion"), load: () => import("../routes/connections-screen"), startExport: "indexersLoader" },
  { match: (path) => path === "/system" || path.startsWith("/system/") && !/^\/system\/(api|docs|security)/.test(path), load: () => import("../routes/system-page"), startExport: "systemLoader" },
  { match: (path) => path === "/calendar", load: () => import("../routes/calendar-page"), startExport: "calendarLoader" },
  { match: (path) => path === "/collections", load: () => import("../routes/collections-page") },
  { match: (path) => path === "/discover", load: () => import("../routes/discover-page") },
  { match: (path) => path === "/search", load: () => import("../routes/search-page") },
  { match: (path) => path.startsWith("/search-cycles"), load: () => import("../routes/search-cycles-page") },
  { match: (path) => path.startsWith("/settings"), load: () => import("../routes/settings-overview-page") }
];

function requestFor(to: string): { request: Request } {
  return { request: new Request(new URL(to, window.location.origin)) };
}

function targetFor(to: string): PrefetchTarget | undefined {
  const path = to.split(/[?#]/)[0].replace(/\/+$/, "") || "/";
  return targets.find((entry) => entry.match(path));
}

/** Screens whose chunks are fetched once the app is idle after sign-in. */
const IDLE_PATHS = ["/", "/movies", "/tv", "/queue", "/indexers/indexers", "/system", "/calendar"] as const;

/**
 * Fetches the chunk for an address, and with `data` starts the screen's own
 * queries too. Never throws and never waits: it is a hint.
 */
export function prefetchRoute(to: string, options: { data?: boolean } = {}): void {
  const entry = targetFor(to);
  if (!entry) return;

  void entry.load().then((module) => {
    if (!options.data || !entry.startExport) return;
    const start = module[entry.startExport];
    // The address goes with the hint: In progress reads its history from different places than its downloads.
    if (typeof start === "function") (start as (args?: unknown) => unknown)(requestFor(to));
  }).catch(() => undefined);
}

/** The pointer or focus handlers a navigation link spreads on to prefetch its screen. */
export function prefetchHandlers(to: string) {
  const warm = () => prefetchRoute(to, { data: true });
  return { onPointerEnter: warm, onFocus: warm, onTouchStart: warm } as const;
}

let idleScheduled = false;

/**
 * After sign-in, when the browser has nothing better to do, fetch the chunks
 * of the main screens one at a time so the first click on any of them finds
 * its code already there.
 */
export function prefetchMainRoutesWhenIdle(): void {
  if (idleScheduled || typeof window === "undefined") return;
  idleScheduled = true;

  const idle = (callback: () => void) => {
    const request = (window as Window & { requestIdleCallback?: (fn: () => void, options?: { timeout: number }) => number }).requestIdleCallback;
    if (request) request.call(window, callback, { timeout: 3_000 });
    else window.setTimeout(callback, 1_200);
  };

  const queue = [...IDLE_PATHS];
  const next = () => {
    const path = queue.shift();
    if (!path) return;
    prefetchRoute(path);
    idle(next);
  };
  idle(next);
}
