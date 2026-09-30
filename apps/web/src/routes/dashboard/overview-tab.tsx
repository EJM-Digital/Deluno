/**
 * Overview (#1082): one true sentence per area, each leading into its tab; the
 * one list of things that need you, each with its fix button; and the latest
 * activity in plain words. A fact appears once: the numbers live in the tabs.
 */
import type { JSX } from "react";
import { ChevronRight } from "lucide-react";
import { Link } from "react-router-dom";
import { OnboardingBanner } from "../../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../../components/shell/setup-progress-ladder";
import { Button } from "../../components/ui/button";
import { StatusLed, type LedTone } from "../../components/ui/status-led";
import type { DashboardActivityItem, DashboardSummary } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { ago, bytesText, plural, speedText, until, uptimeText } from "./format";
import { useDashboardAttention } from "./use-dashboard-attention";
import { Panel, Plain } from "./parts";
import { useSetupState } from "./sources";

interface AreaLine {
  id: string;
  label: string;
  text: string;
  tone: LedTone;
  /** The tab this area lives on. */
  to: string;
  /** An extra button, when the honest next step is a different screen. */
  action?: { label: string; to: string };
}

const NOT_AVAILABLE = "Not available just now. It will fill in when Deluno has it.";

/** One sentence per area. Exported so a test can read every sentence without a DOM. */
export function buildAreaLines(summary: DashboardSummary): AreaLine[] {
  const lines: AreaLine[] = [];

  const library = summary.library;
  if (!library) {
    lines.push({ id: "library", label: "Library", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=library" });
  } else if (library.movies.total + library.shows.total === 0) {
    lines.push({
      id: "library",
      label: "Library",
      text: "Nothing in your library yet.",
      tone: "idle",
      to: "/?tab=library",
      action: { label: "Add a movie", to: "/movies?add=true" }
    });
  } else {
    const missing = library.movies.missing + library.shows.missing;
    const upgradable = library.movies.upgradable + library.shows.upgradable;
    const rest = missing + upgradable === 0
      ? "Everything monitored has the quality you asked for."
      : `${missing} missing, ${upgradable} could be upgraded.`;
    lines.push({
      id: "library",
      label: "Library",
      text: `${plural(library.movies.total, "movie")} and ${plural(library.shows.total, "show")}. ${rest}`,
      tone: "ok",
      to: "/?tab=library"
    });
  }

  const downloads = summary.downloads;
  if (!downloads) {
    lines.push({ id: "downloads", label: "Downloads", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=downloads" });
  } else if (!downloads.hasReading) {
    lines.push({ id: "downloads", label: "Downloads", text: "No download client has reported yet.", tone: "idle", to: "/?tab=downloads" });
  } else {
    const moving = downloads.active > 0
      ? `${downloads.active} downloading${downloads.queued > 0 ? `, ${downloads.queued} queued` : ""}`
      : downloads.queued > 0 ? `Nothing moving, ${downloads.queued} queued` : "Nothing downloading";
    lines.push({
      id: "downloads",
      label: "Downloads",
      text: `${speedText(downloads.downloadMbps)} down. ${moving}${downloads.stalled > 0 ? `, ${downloads.stalled} stalled` : ""}.`,
      tone: downloads.stalled > 0 ? "warn" : downloads.active > 0 ? "info" : "ok",
      to: "/?tab=downloads"
    });
  }

  const automation = summary.automation;
  if (!automation) {
    lines.push({ id: "automation", label: "Automation", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=system" });
  } else {
    const cycle = automation.lastSearchCycle;
    const nextTimes = automation.libraries.map((entry) => entry.nextSearchUtc).filter((value): value is string => Boolean(value)).sort();
    const last = cycle
      ? `Last search ${ago(cycle.completedUtc ?? cycle.startedUtc)} in ${cycle.libraryName}, ${plural(cycle.queued, "title")} sent to download.`
      : "No search has run yet.";
    lines.push({
      id: "automation",
      label: "Automation",
      text: nextTimes.length > 0 ? `${last} Next search ${until(nextTimes[0])}.` : last,
      tone: automation.libraries.some((entry) => entry.lastError) ? "warn" : "ok",
      to: "/?tab=system"
    });
  }

  const indexers = summary.indexers;
  const clients = summary.downloads?.clients ?? [];
  if (!indexers) {
    lines.push({ id: "indexers", label: "Indexers & clients", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=downloads" });
  } else if (indexers.total === 0 && clients.length === 0) {
    lines.push({
      id: "indexers",
      label: "Indexers & clients",
      text: "No indexers or download clients yet.",
      tone: "idle",
      to: "/?tab=downloads",
      action: { label: "Add them", to: "/indexers/indexers" }
    });
  } else {
    const reachable = clients.filter((client) => client.reachable).length;
    lines.push({
      id: "indexers",
      label: "Indexers & clients",
      text: `${indexers.healthy} of ${plural(indexers.total, "indexer")} answering. ${reachable} of ${plural(clients.length, "download client")} reachable.`,
      tone: indexers.failing > 0 || reachable < clients.length ? "warn" : "ok",
      to: "/?tab=downloads"
    });
  }

  const processors = summary.processors;
  if (!processors) {
    lines.push({ id: "processing", label: "Processing", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=downloads" });
  } else if (processors.total === 0) {
    lines.push({ id: "processing", label: "Processing", text: "No processor is set up. Finished downloads go straight to import.", tone: "idle", to: "/?tab=downloads" });
  } else {
    lines.push({
      id: "processing",
      label: "Processing",
      text: `${processors.healthy} of ${plural(processors.total, "processor")} ready. ${plural(processors.waiting, "download")} waiting for one.`,
      tone: processors.failing > 0 ? "warn" : "ok",
      to: "/?tab=downloads"
    });
  }

  const retention = summary.retention;
  if (!retention) {
    lines.push({ id: "retention", label: "Retention", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=library" });
  } else {
    const next = retention.nextLeaving[0];
    const text = retention.leavingSoon > 0 && next
      ? `${plural(retention.leavingSoon, "title")} leaving soon. First is ${next.title}, ${until(next.removeAfterUtc)}.`
      : retention.removedLast7Days > 0
        ? `Nothing is due to leave. ${plural(retention.removedLast7Days, "title")} removed this week.`
        : "Nothing is due to leave.";
    lines.push({ id: "retention", label: "Retention", text, tone: retention.leavingSoon > 0 ? "info" : "idle", to: "/?tab=library" });
  }

  const subtitles = summary.subtitles;
  if (!subtitles) {
    lines.push({ id: "subtitles", label: "Subtitles", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=library" });
  } else {
    lines.push({
      id: "subtitles",
      label: "Subtitles",
      text: subtitles.wanted + subtitles.held === 0
        ? "No titles are waiting for subtitles."
        : `${plural(subtitles.wanted, "title")} waiting for subtitles${subtitles.held > 0 ? `, ${plural(subtitles.held, "file")} held back` : ""}.`,
      tone: subtitles.wanted > 0 ? "info" : "idle",
      to: "/?tab=library"
    });
  }

  const requests = summary.requests;
  if (!requests) {
    lines.push({ id: "requests", label: "Requests", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=library" });
  } else if (requests.seerrServers === 0 && requests.importLists === 0) {
    lines.push({ id: "requests", label: "Requests", text: "No request server or lists connected.", tone: "idle", to: "/?tab=library" });
  } else {
    const parts = [
      requests.seerrServers > 0 ? `${requests.seerrHealthy} of ${plural(requests.seerrServers, "request server")} answering` : null,
      requests.importLists > 0 ? `${plural(requests.importLists, "list")} added ${plural(requests.listTitlesAddedLast7Days, "title")} this week` : null
    ].filter(Boolean);
    lines.push({ id: "requests", label: "Requests", text: `${parts.join(". ")}.`, tone: requests.seerrFailing > 0 ? "warn" : "ok", to: "/?tab=library" });
  }

  const storage = summary.storage;
  if (!storage) {
    lines.push({ id: "storage", label: "Storage", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=system" });
  } else {
    const readable = storage.volumes.filter((volume) => volume.available && volume.freeBytes !== null);
    const least = [...readable].sort((left, right) => (left.freeBytes ?? 0) - (right.freeBytes ?? 0))[0];
    const unreadable = storage.volumes.length - readable.length;
    lines.push({
      id: "storage",
      label: "Storage",
      text: storage.volumes.length === 0
        ? "No drives have been read yet."
        : `${plural(storage.volumes.length, "drive")} watched${least ? `. Least free: ${least.label}, ${bytesText(least.freeBytes)}` : ""}${unreadable > 0 ? `. ${plural(unreadable, "drive")} can't be read` : ""}.`,
      tone: unreadable > 0 ? "warn" : "ok",
      to: "/?tab=system"
    });
  }

  const system = summary.system;
  if (!system) {
    lines.push({ id: "system", label: "System & updates", text: NOT_AVAILABLE, tone: "idle", to: "/?tab=system" });
  } else {
    lines.push({
      id: "system",
      label: "System & updates",
      text: `Ready, up ${uptimeText(system.uptimeSeconds)}. Version ${system.version}${system.updateAvailable ? `, ${system.latestVersion ?? "a newer version"} is available` : ", up to date"}.`,
      tone: system.internetOutage ? "warn" : "ok",
      to: "/?tab=system"
    });
  }

  return lines;
}

function entityLink(item: DashboardActivityItem): string {
  const kind = item.relatedEntityType?.toLowerCase() ?? "";
  if (!item.relatedEntityId) return "/activity";
  if (kind.includes("movie")) return `/movies/${item.relatedEntityId}`;
  if (kind.includes("series") || kind.includes("show")) return `/tv/${item.relatedEntityId}`;
  return "/activity";
}

export function OverviewTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const setup = useSetupState();
  const attention = useDashboardAttention(summary, setup.needs);
  const needs = attention.items;
  const latest = summary?.activity?.recent ?? [];
  const showSetup = setup.ready && !setup.status.isComplete;

  function dismissOnboarding() {
    void authedFetch("/api/setup/progress", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ lastCompletedStep: setup.progress.lastCompletedStep, isSkipped: true, isCompleted: setup.progress.isCompleted })
    }).catch(() => undefined);
  }

  return (
    <div className="flex flex-col gap-[var(--grid-gap)] lg:h-full lg:min-h-0">
      {showSetup ? (
        <div className="flex shrink-0 flex-col gap-[var(--grid-gap)]">
          <OnboardingBanner isSetupSuppressed={setup.progress.isSkipped || setup.progress.isCompleted} onDismiss={dismissOnboarding} />
          <SetupProgressLadder status={setup.status} />
        </div>
      ) : null}

      <div className="grid gap-[var(--grid-gap)] lg:min-h-0 lg:flex-1 lg:grid-cols-[minmax(0,1.25fr)_minmax(0,1fr)_minmax(0,1fr)] lg:grid-rows-[minmax(0,1fr)]">
        <Panel title="Areas" count="each opens its tab">
          {summary ? (
            <ul>
              {buildAreaLines(summary).map((line) => (
                <li key={line.id} className="relative flex min-h-[52px] items-center gap-3 border-b border-hairline px-[var(--card-pad-x)] py-2 transition-colors last:border-b-0 hover:bg-primary/[0.05]">
                  <StatusLed tone={line.tone} />
                  <div className="min-w-0 flex-1">
                    <Link
                      to={line.to}
                      className="block text-[length:var(--type-body-sm)] font-semibold text-foreground after:absolute after:inset-0 focus-visible:outline-none focus-visible:after:ring-2 focus-visible:after:ring-inset focus-visible:after:ring-ring"
                    >
                      {line.label}
                    </Link>
                    <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">{line.text}</p>
                  </div>
                  {line.action ? (
                    <Button asChild size="sm" variant="outline" className="relative z-10 shrink-0">
                      <Link to={line.action.to}>{line.action.label}</Link>
                    </Button>
                  ) : null}
                  <ChevronRight aria-hidden className="h-4 w-4 shrink-0 text-muted-foreground/50" />
                </li>
              ))}
            </ul>
          ) : (
            <Plain>Waiting for the numbers from Deluno.</Plain>
          )}
        </Panel>

        <Panel title="Needs you" className="max-lg:order-first" count={attention.counts.overview === 0 ? "nothing right now" : String(attention.counts.overview)}>
          {needs.length === 0 ? (
            <Plain>Nothing needs you. Anything that does will be listed here, each with a button that fixes it.</Plain>
          ) : (
            <ul>
              {needs.map((row) => (
                <li key={row.id} className="flex min-h-[52px] items-center gap-3 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
                  <StatusLed tone={row.tone === "bad" ? "bad" : "warn"} />
                  <div className="min-w-0 flex-1">
                    <p className="text-[length:var(--type-body-sm)] font-medium leading-snug text-foreground">{row.title}</p>
                    <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">{row.text}</p>
                  </div>
                  <Button asChild size="sm" variant="outline" className="shrink-0">
                    <Link to={row.to} aria-label={`${row.action}: ${row.title}`}>{row.action}</Link>
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel title="Latest" count="what Deluno just did" to="/activity" toLabel="All activity">
          {latest.length === 0 ? (
            <Plain>Nothing has happened yet. What Deluno does shows up here as it works.</Plain>
          ) : (
            <ul>
              {latest.map((item) => (
                <li key={item.id} className="border-b border-hairline last:border-b-0">
                  <Link
                    to={entityLink(item)}
                    className="block px-[var(--card-pad-x)] py-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                  >
                    <span className="block text-[length:var(--type-body-sm)] leading-snug text-foreground">{item.message}</span>
                    <span className="block text-[length:var(--type-caption)] text-muted-foreground">
                      {item.label} · {ago(item.createdUtc)}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}
