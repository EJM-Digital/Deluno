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
import { useSearchParams } from "react-router-dom";
import { AutomationPausedNotice } from "../components/app/automation-paused-notice";
import { RouteSkeleton } from "../components/shell/skeleton";
import { Button } from "../components/ui/button";
import { HeaderTabs, HeaderTabsPortal, useHeaderSlot } from "../components/ui/header-tabs";
import { ListEmpty } from "../components/ui/list-card";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { queryClient, startSources } from "../lib/route-cache";
import { RealtimeGroups, useSignalREvent, useSignalRResync } from "../lib/use-signalr";
import { cn } from "../lib/utils";
import { LiveTab } from "./dashboard/live-tab";
import { SystemTab } from "./dashboard/system-tab";
import type { DashboardSummary } from "../lib/api";
import { liveSources, refreshLiveExtrasNow, refreshSummary, setupSources, summaryQuery, useDashboardSummary, useSetupState } from "./dashboard/sources";
import "./dashboard/phone-wrap.css";

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

function TabBar({ active, onSelect }: {
  active: DashboardTab;
  onSelect: (tab: DashboardTab) => void;
}) {
  return (
    <HeaderTabs
      role="tablist"
      label="Dashboard sections"
      items={DASHBOARD_TABS.map((tab) => ({
        key: tab,
        label: TAB_LABELS[tab],
        selected: tab === active,
        onSelect: () => onSelect(tab),
        buttonId: `dashboard-tab-${tab}`,
        controls: "dashboard-tabpanel"
      }))}
    />
  );
}

/**
 * On a wide screen the page is exactly as tall as the window, so a tab that has
 * too much to show scrolls inside its own panels rather than pushing the page
 * down. On a phone it flows and scrolls as any page does, and a window shorter
 * than 600px scrolls rather than crushing the panels.
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
      element.style.height = `${Math.max(600, Math.floor(window.innerHeight - top - below))}px`;
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
  // The connections follow the same health push System and the tray follow (#1233). The processors come from the
  // Live extras (past the server's five-second copy), the indexers and clients from their lists; none waits on a timer.
  const readConnectionsAgain = useCoalescedRevalidate(() => {
    void refreshLiveExtrasNow();
    void queryClient.invalidateQueries({ queryKey: liveSources.indexers.key, exact: true });
    void queryClient.invalidateQueries({ queryKey: liveSources.clients.key, exact: true });
    // The stored checks are the Health card's own list; a new problem and its clearing arrive on this same push.
    void queryClient.invalidateQueries({ queryKey: ["src", "system-health"], exact: true });
  }, 150);
  useSignalREvent("HealthChanged", RealtimeGroups.Dashboard, readConnectionsAgain);
  useSignalRResync(() => { void queryClient.invalidateQueries({ queryKey: ["dashboard"] }); void queryClient.invalidateQueries({ queryKey: ["src"] }); });

  const select = useCallback((next: DashboardTab) => {
    setSearchParams((current) => {
      const params = new URLSearchParams(current);
      if (next === "live") params.delete("tab");
      else params.set("tab", next);
      return params;
    }, { replace: false });
  }, [setSearchParams]);

  // Problems are in the bell, counted once by the server for every screen; neither tab carries a number (#1098, #1185).

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

  const bar = <TabBar active={tab} onSelect={select} />;

  return (
    <div ref={setRoot} className="flex flex-col gap-(--grid-gap)">
      {slot ? <HeaderTabsPortal slot={slot}>{bar}</HeaderTabsPortal> : bar}
      <AutomationPausedNotice context="dashboard" />
      {/* The tabs are drawn at the approved mockup's own sizes on every screen (see `.dash-mockup-size` in index.css). */}
      <div id="dashboard-tabpanel" role="tabpanel" aria-labelledby={`dashboard-tab-${tab}`} className="dash-mockup-size min-h-0 flex-1 lg:relative">
        <div className="lg:absolute lg:inset-0 lg:overflow-hidden">
          <TabBody tab={tab} summary={summary} />
        </div>
      </div>
    </div>
  );
}
