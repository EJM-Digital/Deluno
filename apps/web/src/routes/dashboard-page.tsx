/**
 * The Dashboard (#1087): two tabs in the page header, Live and System.
 * Each tab fits one screen. Both read one summary request
 * (`GET /api/dashboard/summary`, built from stored numbers only) and refetch it
 * when the server says it changed, so switching a tab never asks for anything
 * and never blanks the page (#1081).
 *
 * The tab is in the address (`?tab=system`), so a tab can be linked; Live carries
 * no `tab` at all. The four old values still work: `overview`, `library` and
 * `downloads` open Live, and `setup-health` opens System.
 */
import { useCallback, useLayoutEffect, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useSearchParams } from "react-router-dom";
import { RouteSkeleton } from "../components/shell/skeleton";
import { Button } from "../components/ui/button";
import { ListEmpty } from "../components/ui/list-card";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { queryClient, startSources } from "../lib/route-cache";
import { RealtimeGroups, useSignalREvent, useSignalRResync } from "../lib/use-signalr";
import { cn } from "../lib/utils";
import { LiveTab } from "./dashboard/live-tab";
import { SystemTab } from "./dashboard/system-tab";
import type { DashboardSummary } from "../lib/api";
import { liveSources, refreshSummary, setupSources, summaryQuery, useDashboardSummary, useSetupState } from "./dashboard/sources";
import { useDashboardAttention } from "./dashboard/use-dashboard-attention";

export const DASHBOARD_TABS = ["live", "system"] as const;
export type DashboardTab = (typeof DASHBOARD_TABS)[number];

const TAB_LABELS: Record<DashboardTab, string> = {
  live: "Live",
  system: "System"
};

/** The tab each older tab name now opens. */
const OLD_TABS: Record<string, DashboardTab> = { now: "live", overview: "live", library: "live", downloads: "live", "setup-health": "system", setup: "system" };

/** An unknown or missing `?tab=` is Live; an old tab name opens the tab its content moved to. */
export function tabFromSearch(search: string | URLSearchParams): DashboardTab {
  const value = (typeof search === "string" ? new URLSearchParams(search) : search).get("tab") ?? "";
  if ((DASHBOARD_TABS as readonly string[]).includes(value)) return value as DashboardTab;
  return OLD_TABS[value] ?? "live";
}

/**
 * Only a change of screen reloads the Dashboard's data. Switching a tab changes
 * the query string and nothing else, and every tab already reads what the page
 * holds, so the router has nothing to fetch. A form submission still
 * revalidates, as React Router would by default.
 */
export function dashboardShouldRevalidate({ currentUrl, nextUrl, formMethod, defaultShouldRevalidate }: {
  currentUrl: URL;
  nextUrl: URL;
  formMethod?: string;
  defaultShouldRevalidate: boolean;
}): boolean {
  if (formMethod) return defaultShouldRevalidate;
  if (currentUrl.pathname === nextUrl.pathname) return false;
  return defaultShouldRevalidate;
}

/**
 * Starts what the page reads and returns at once (#1081): the summary, what the
 * setup checklist needs, and what the chosen tab reads beyond the summary.
 */
export function dashboardLoader(args?: { request?: Request }) {
  void queryClient.prefetchQuery(summaryQuery);
  startSources(setupSources);
  if (args?.request && tabFromSearch(new URL(args.request.url).searchParams) === "live") startSources(liveSources);
  return { cached: true as const };
}

function TabBar({ active, badges, onSelect, className }: {
  active: DashboardTab;
  badges: Record<DashboardTab, number>;
  onSelect: (tab: DashboardTab) => void;
  className?: string;
}) {
  return (
    <div role="tablist" aria-label="Dashboard sections" className={cn("flex items-center gap-1", className)}>
      {DASHBOARD_TABS.map((tab) => {
        const selected = tab === active;
        const count = badges[tab];
        return (
          <Button
            key={tab}
            type="button"
            role="tab"
            id={`dashboard-tab-${tab}`}
            aria-selected={selected}
            aria-controls="dashboard-tabpanel"
            variant="ghost"
            size="sm"
            onClick={() => onSelect(tab)}
            className={cn(
              "px-3.5",
              selected ? "border border-primary/40 bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary" : "border border-transparent"
            )}
          >
            {TAB_LABELS[tab]}
            {count > 0 ? (
              <span
                aria-label={`${count} ${count === 1 ? "thing needs" : "things need"} a look`}
                className="inline-flex h-[18px] min-w-[18px] items-center justify-center rounded-full bg-warning/20 px-1.5 text-[length:var(--type-micro)] font-bold tabular-nums text-warning"
              >
                {count}
              </span>
            ) : null}
          </Button>
        );
      })}
    </div>
  );
}

/** The header has a slot for the tabs; a page rendered without the header shows them inline. */
function useHeaderSlot(): HTMLElement | null {
  const [slot, setSlot] = useState<HTMLElement | null>(null);
  useLayoutEffect(() => {
    setSlot(document.getElementById("topbar-page-tabs"));
  }, []);
  return slot;
}

/**
 * On a wide screen the page is exactly as tall as the window, so a tab that has
 * too much to show scrolls inside its own panels rather than pushing the page
 * down. On a phone it flows and scrolls as any page does, and a window shorter
 * than 720px scrolls rather than crushing the panels.
 */
function useFitToScreen(element: HTMLDivElement | null) {
  useLayoutEffect(() => {
    if (!element) return undefined;
    const fit = () => {
      if (window.innerWidth < 1024) {
        element.style.height = "";
        return;
      }
      const top = element.getBoundingClientRect().top + window.scrollY;
      const main = element.closest("main");
      const below = main ? parseFloat(getComputedStyle(main).paddingBottom) || 0 : 0;
      element.style.height = `${Math.max(720, Math.floor(window.innerHeight - top - below))}px`;
    };
    fit();
    window.addEventListener("resize", fit);
    // A banner above the page (an outage, say) moves it without a resize.
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(fit);
    observer?.observe(document.body);
    return () => {
      window.removeEventListener("resize", fit);
      observer?.disconnect();
    };
  }, [element]);
}

function TabBody({ tab, summary }: { tab: DashboardTab; summary: DashboardSummary | undefined }): ReactNode {
  return tab === "system" ? <SystemTab summary={summary} /> : <LiveTab summary={summary} />;
}

export function DashboardPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const tab = tabFromSearch(searchParams);
  const summaryQ = useDashboardSummary();
  const summary = summaryQ.data;
  const setup = useSetupState();
  const slot = useHeaderSlot();
  // The page is not drawn until the summary arrives, so the element is kept in
  // state: the fit has to start when it appears, not when the page mounts.
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  useFitToScreen(root);

  // The server pushes the changed summary sections themselves (#1097), and the
  // page writes them into the cache: nothing is fetched after a change.
  // Setup reads a few lists that change on their own events, so those events
  // refresh the setup lists.
  const refreshSetup = useCoalescedRevalidate(() => { void queryClient.invalidateQueries({ queryKey: ["src"] }); }, 1_000);
  useSignalREvent("SettingsChanged", RealtimeGroups.Dashboard, refreshSetup);
  useSignalREvent("QualityProfileChanged", RealtimeGroups.Dashboard, refreshSetup);
  useSignalREvent("PolicySetChanged", RealtimeGroups.Dashboard, refreshSetup);
  useSignalREvent("IndexerChanged", RealtimeGroups.Dashboard, refreshSetup);
  useSignalREvent("DownloadClientChanged", RealtimeGroups.Dashboard, refreshSetup);
  useSignalRResync(() => { void queryClient.invalidateQueries({ queryKey: ["dashboard"] }); void queryClient.invalidateQueries({ queryKey: ["src"] }); });

  const select = useCallback((next: DashboardTab) => {
    setSearchParams((current) => {
      const params = new URLSearchParams(current);
      if (next === "live") params.delete("tab");
      else params.set("tab", next);
      return params;
    }, { replace: false });
  }, [setSearchParams]);

  // Problems are in the bell, counted once for every screen. The System tab says how many of them are about the system; Live shows what's happening, not what needs a person, so it carries no badge.
  const attention = useDashboardAttention(summary, setup.needs);
  const badges: Record<DashboardTab, number> = { live: 0, system: attention.counts.system };

  // Nothing to draw the first time until the summary arrives. After that the
  // last answer stays on screen while a newer one is fetched behind it.
  if (summary === undefined && summaryQ.isError) {
    return (
      <ListEmpty
        title="The Dashboard could not load"
        description="Deluno did not answer. Check that it is running, then try again."
        actions={<Button type="button" onClick={() => { void summaryQ.refetch(); }}>Try again</Button>}
      />
    );
  }
  if (summary === undefined) return <RouteSkeleton />;

  const bar = <TabBar active={tab} badges={badges} onSelect={select} />;

  return (
    <div ref={setRoot} className="flex flex-col gap-[var(--grid-gap)]">
      {slot ? (
        <>
          {createPortal(<TabBar active={tab} badges={badges} onSelect={select} />, slot)}
          <div className="lg:hidden">{bar}</div>
        </>
      ) : (
        bar
      )}
      <div id="dashboard-tabpanel" role="tabpanel" aria-labelledby={`dashboard-tab-${tab}`} className="min-h-0 flex-1 lg:relative">
        <div className="lg:absolute lg:inset-0 lg:overflow-hidden">
          <TabBody tab={tab} summary={summary} />
        </div>
      </div>
    </div>
  );
}
