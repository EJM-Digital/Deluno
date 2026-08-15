import { useEffect, useMemo, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import {
  Activity as ActivityIcon,
  Clock,
  LoaderCircle,
  Pause,
  Play,
  RotateCw,
  Search,
  SkipForward,
  Zap
} from "lucide-react";
import {
  fetchJson,
  type LibraryItem,
  type LibraryAutomationStateItem,
  type PlatformSettingsSnapshot,
  type SearchCycleRunItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { PageHero } from "../components/shell/page-hero";
import { EmptyState } from "../components/shell/empty-state";
import { RouteSkeleton } from "../components/shell/skeleton";
import { toast } from "../components/shell/toaster";
import { cn } from "../lib/utils";

interface SearchCyclesLoaderData {
  automationStates: LibraryAutomationStateItem[];
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  searchCycles: SearchCycleRunItem[];
}

interface SearchCycleNotesSummary {
  apiCallCount: number;
  queuedReleaseBytes: number;
}

export async function searchCyclesLoader(): Promise<SearchCyclesLoaderData> {
  const [automationStates, libraries, settings, searchCycles] = await Promise.all([
    fetchJson<LibraryAutomationStateItem[]>("/api/library-automation"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<SearchCycleRunItem[]>("/api/search-cycles?take=50")
  ]);

  return { automationStates, libraries, searchCycles, settings };
}

function parseCycleNotes(notesJson: string | null): SearchCycleNotesSummary {
  if (!notesJson) {
    return { apiCallCount: 0, queuedReleaseBytes: 0 };
  }

  try {
    const parsed = JSON.parse(notesJson) as Record<string, unknown>;
    const apiCallCount = typeof parsed.apiCallCount === "number" ? parsed.apiCallCount : 0;
    const queuedReleaseBytes = typeof parsed.queuedReleaseBytes === "number" ? parsed.queuedReleaseBytes : 0;
    return { apiCallCount, queuedReleaseBytes };
  } catch {
    return { apiCallCount: 0, queuedReleaseBytes: 0 };
  }
}

function formatBytes(bytes: number): string {
  if (bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / 1024 ** exponent;
  const rounded = exponent === 0 ? value.toFixed(0) : value.toFixed(1);
  return `${rounded} ${units[exponent]}`;
}

export function SearchCyclesPage() {
  const loaderData = useLoaderData() as SearchCyclesLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;

  const { automationStates, libraries, searchCycles, settings } = loaderData;
  const revalidator = useRevalidator();
  const [globalBusy, setGlobalBusy] = useState(false);
  const [cleanupBusy, setCleanupBusy] = useState(false);
  const [cleanupPolicy, setCleanupPolicy] = useState(() => ({
    strikeThreshold: settings.downloadHealthStrikeThreshold,
    blockRelease: settings.cleanupBlockReleaseAfterThreshold,
    queueReplacement: settings.cleanupQueueReplacementAfterThreshold,
    removeClientEntry: settings.cleanupRemoveClientEntryAfterThreshold,
    purgePayload: settings.cleanupPurgePayloadAfterThreshold
  }));

  useEffect(() => {
    const timer = window.setInterval(() => {
      revalidator.revalidate();
    }, 10000);
    return () => window.clearInterval(timer);
  }, [revalidator]);

  useEffect(() => {
    setCleanupPolicy({
      strikeThreshold: settings.downloadHealthStrikeThreshold,
      blockRelease: settings.cleanupBlockReleaseAfterThreshold,
      queueReplacement: settings.cleanupQueueReplacementAfterThreshold,
      removeClientEntry: settings.cleanupRemoveClientEntryAfterThreshold,
      purgePayload: settings.cleanupPurgePayloadAfterThreshold
    });
  }, [settings]);

  const automationByType = useMemo(() => {
    const grouped: Record<string, LibraryAutomationStateItem[]> = {
      tv: [],
      movies: []
    };
    automationStates.forEach((state) => {
      const key = state.mediaType === "tv" ? "tv" : "movies";
      grouped[key].push(state);
    });
    return grouped;
  }, [automationStates]);

  const dueForSearch = useMemo(
    () =>
      automationStates.filter(
        (state) =>
          state.status !== "paused" &&
          (!state.nextSearchUtc || new Date(state.nextSearchUtc) <= new Date())
      ).length,
    [automationStates]
  );

  const activeSearches = useMemo(
    () => automationStates.filter((state) => state.status === "running").length,
    [automationStates]
  );

  const cycleCostSummary = useMemo(() => {
    return searchCycles.reduce(
      (summary, cycle) => {
        const notes = parseCycleNotes(cycle.notesJson);
        summary.apiCalls += notes.apiCallCount;
        summary.queuedBytes += notes.queuedReleaseBytes;
        return summary;
      },
      { apiCalls: 0, queuedBytes: 0 }
    );
  }, [searchCycles]);

  const toggleGlobalAutomation = async () => {
    setGlobalBusy(true);
    const isEnabling = !settings.autoStartJobs;
    try {
      const response = await authedFetch("/api/settings/automation", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ isEnabled: isEnabling })
      });
      if (!response.ok) throw new Error("Could not update global automation.");
      toast.success(isEnabling ? "Deluno automation resumed." : "Deluno automation paused. Existing external downloads are unchanged.");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not update global automation.");
    } finally {
      setGlobalBusy(false);
    }
  };

  const saveCleanupPolicy = async () => {
    setCleanupBusy(true);
    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          ...settings,
          downloadHealthStrikeThreshold: Math.max(1, Math.min(20, Math.round(cleanupPolicy.strikeThreshold || 3))),
          cleanupBlockReleaseAfterThreshold: cleanupPolicy.blockRelease,
          cleanupQueueReplacementAfterThreshold: cleanupPolicy.queueReplacement,
          cleanupRemoveClientEntryAfterThreshold: cleanupPolicy.removeClientEntry,
          cleanupPurgePayloadAfterThreshold: cleanupPolicy.purgePayload
        })
      });
      if (!response.ok) throw new Error("Could not save failed-download handling.");
      toast.success("Failed-download handling saved.");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not save failed-download handling.");
    } finally {
      setCleanupBusy(false);
    }
  };

  return (
    <div className="space-y-[var(--page-gap)]">
      {/* ═══════ HERO ═══════ */}
      <PageHero
        eyebrow="Deluno automation"
        eyebrowIcon={<Search className="h-3 w-3 text-primary" />}
        title="Choose what Deluno should do next"
        subtitle={
          <>
            Deluno looks for missing releases and allowed upgrades, then sends approved matches to your download client. {" "}
            <span className="font-semibold text-foreground">{activeSearches}</span> working now ·{" "}
            <span className={cn("font-semibold", dueForSearch > 0 ? "text-warning" : "text-success")}>
              {dueForSearch > 0 ? `${dueForSearch} due` : "all caught up"}
            </span>
          </>
        }
      />

      <div className={cn("flex flex-wrap items-center justify-between gap-[var(--grid-gap)] rounded-2xl border p-4", settings.autoStartJobs ? "border-success/25 bg-success/[0.04]" : "border-warning/30 bg-warning/[0.05]")}>
        <div>
          <p className="font-semibold text-foreground">Global automation is {settings.autoStartJobs ? "running" : "paused"}</p>
          <p className="mt-1 text-sm text-muted-foreground">
            {settings.autoStartJobs
              ? "Deluno can process scheduled searches, imports, and retries."
              : "Deluno will keep queued work safe until you resume it. External download clients are unchanged."}
          </p>
        </div>
        <Button type="button" variant={settings.autoStartJobs ? "outline" : "default"} className="gap-2" disabled={globalBusy} onClick={() => void toggleGlobalAutomation()}>
          {globalBusy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : settings.autoStartJobs ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4" />}
          {settings.autoStartJobs ? "Pause all automation" : "Resume automation"}
        </Button>
      </div>

      <section className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-col gap-[var(--grid-gap)] lg:flex-row lg:items-start lg:justify-between">
          <div className="max-w-2xl">
            <p className="text-xs font-bold uppercase tracking-[0.16em] text-primary">Automation & recovery</p>
            <h2 className="mt-1 font-display text-lg font-semibold text-foreground">Failed download handling</h2>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">Choose what happens when the same release repeatedly fails health checks. Deluno records every strike and only removes a client item or payload when the configured path is proven to be Deluno-owned.</p>
          </div>
          <div className="w-full max-w-xs">
            <label className="block text-sm font-semibold text-foreground">Act after this many strikes
              <input type="number" min={1} max={20} value={cleanupPolicy.strikeThreshold} onChange={(event) => setCleanupPolicy((current) => ({ ...current, strikeThreshold: Number(event.target.value) }))} className="mt-2 h-10 w-full rounded-xl border border-hairline bg-background px-3 text-foreground outline-none focus:border-primary/50" />
            </label>
          </div>
        </div>
        <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <CleanupOption label="Block this release" copy="Do not select the same failed release again." checked={cleanupPolicy.blockRelease} onChange={(checked) => setCleanupPolicy((current) => ({ ...current, blockRelease: checked }))} />
          <CleanupOption label="Search for a replacement" copy="Queue one bounded replacement search using normal budgets." checked={cleanupPolicy.queueReplacement} onChange={(checked) => setCleanupPolicy((current) => ({ ...current, queueReplacement: checked }))} />
          <CleanupOption label="Remove client entry" copy="Remove the failed item from its download client when supported." checked={cleanupPolicy.removeClientEntry} onChange={(checked) => setCleanupPolicy((current) => ({ ...current, removeClientEntry: checked }))} />
          <CleanupOption label="Purge residual files" copy="Delete the failed payload only in approved Deluno-owned paths." checked={cleanupPolicy.purgePayload} onChange={(checked) => setCleanupPolicy((current) => ({ ...current, purgePayload: checked }))} />
        </div>
        <div className="mt-4 flex justify-end"><Button type="button" disabled={cleanupBusy} onClick={() => void saveCleanupPolicy()}>{cleanupBusy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null} Save failed-download handling</Button></div>
      </section>

      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-wrap items-center gap-3 text-sm text-muted-foreground">
          <Clock className="h-4 w-4" />
          Deluno shares searches fairly across your libraries so it does not overwhelm your sources.
          <Badge variant="info" className="font-mono text-xs">
            ~{cycleCostSummary.apiCalls} source checks
          </Badge>
          <Badge variant="success" className="font-mono text-xs">
            {formatBytes(cycleCostSummary.queuedBytes)} sent to downloads
          </Badge>
        </div>
      </div>

      <div className="grid gap-[var(--page-gap)]">
        {/* TV Shows */}
        {automationByType.tv.length > 0 && (
          <LibraryAutomationSection
            title="TV automation"
            libraries={automationByType.tv}
            librarySettings={libraries}
            onRevalidate={() => revalidator.revalidate()}
          />
        )}

        {/* Movies */}
        {automationByType.movies.length > 0 && (
          <LibraryAutomationSection
            title="Movie automation"
            libraries={automationByType.movies}
            librarySettings={libraries}
            onRevalidate={() => revalidator.revalidate()}
          />
        )}

        {automationStates.length === 0 && (
          <EmptyState
            icon={Search}
            title="Nothing to automate yet"
            description="Add a movie or TV library, then Deluno can keep missing and upgrade candidates moving for you."
          />
        )}
      </div>

      {/* Search History */}
      {searchCycles.length > 0 && (
        <SearchHistorySection cycles={searchCycles} />
      )}
    </div>
  );
}

function LibraryAutomationSection({
  title,
  libraries,
  librarySettings,
  onRevalidate
}: {
  title: string;
  libraries: LibraryAutomationStateItem[];
  librarySettings: LibraryItem[];
  onRevalidate: () => void;
}) {
  const [triggering, setTriggering] = useState<Set<string>>(new Set());
  const [skipping, setSkipping] = useState<Set<string>>(new Set());
  const [toggling, setToggling] = useState<Set<string>>(new Set());

  const handleTriggerSearch = async (libraryId: string, libraryName: string) => {
    setTriggering((prev) => new Set([...prev, libraryId]));
    try {
      const response = await authedFetch(`/api/libraries/${libraryId}/search-now`, {
        method: "POST"
      });

      if (!response.ok) {
        throw new Error("Could not trigger search");
      }

      toast.success(`Deluno will search ${libraryName} next.`);
      onRevalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Search trigger failed");
    } finally {
      setTriggering((prev) => {
        const next = new Set(prev);
        next.delete(libraryId);
        return next;
      });
    }
  };

  const handleToggleAutomation = async (state: LibraryAutomationStateItem) => {
    const library = librarySettings.find((item) => item.id === state.libraryId);
    if (!library) {
      toast.error("This library could not be found.");
      return;
    }

    setToggling((prev) => new Set([...prev, library.id]));
    const enabling = !library.autoSearchEnabled;
    try {
      const response = await authedFetch(`/api/libraries/${library.id}/automation`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          autoSearchEnabled: enabling,
          missingSearchEnabled: library.missingSearchEnabled,
          upgradeSearchEnabled: library.upgradeSearchEnabled,
          searchIntervalHours: library.searchIntervalHours,
          retryDelayHours: library.retryDelayHours,
          maxItemsPerRun: library.maxItemsPerRun,
          searchWindowStartHour: library.searchWindowStartHour,
          searchWindowEndHour: library.searchWindowEndHour
        })
      });

      if (!response.ok) throw new Error("Could not update automation.");
      toast.success(enabling ? `Automation resumed for ${library.name}.` : `Automation paused for ${library.name}.`);
      onRevalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Automation update failed.");
    } finally {
      setToggling((prev) => {
        const next = new Set(prev);
        next.delete(library.id);
        return next;
      });
    }
  };

  const handleSkipCycle = async (libraryId: string, libraryName: string) => {
    setSkipping((prev) => new Set([...prev, libraryId]));
    try {
      const response = await authedFetch(`/api/libraries/${libraryId}/skip-cycle`, {
        method: "POST"
      });

      if (!response.ok) {
        throw new Error("Could not skip this search cycle");
      }

      toast.success(`Deluno will skip ${libraryName} this time.`);
      onRevalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Skip cycle failed");
    } finally {
      setSkipping((prev) => {
        const next = new Set(prev);
        next.delete(libraryId);
        return next;
      });
    }
  };

  return (
    <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
      <h2 className="mb-[var(--grid-gap)] font-display text-lg font-semibold text-foreground">{title}</h2>
      <div className="space-y-3">
        {libraries.map((library) => (
          <LibraryAutomationCard
            key={library.libraryId}
            state={library}
            onTrigger={() => handleTriggerSearch(library.libraryId, library.libraryName)}
            isTriggering={triggering.has(library.libraryId)}
            onSkip={() => handleSkipCycle(library.libraryId, library.libraryName)}
            isSkipping={skipping.has(library.libraryId)}
            onToggle={() => handleToggleAutomation(library)}
            isToggling={toggling.has(library.libraryId)}
            isAutomationEnabled={librarySettings.find((item) => item.id === library.libraryId)?.autoSearchEnabled ?? false}
          />
        ))}
      </div>
    </div>
  );
}

function LibraryAutomationCard({
  state,
  onTrigger,
  isTriggering,
  onSkip,
  isSkipping,
  onToggle,
  isToggling,
  isAutomationEnabled
}: {
  state: LibraryAutomationStateItem;
  onTrigger: () => Promise<void>;
  isTriggering: boolean;
  onSkip: () => Promise<void>;
  isSkipping: boolean;
  onToggle: () => Promise<void>;
  isToggling: boolean;
  isAutomationEnabled: boolean;
}) {
  const nextSearchIn = useMemo(() => {
    if (!state.nextSearchUtc) return null;
    const next = new Date(state.nextSearchUtc);
    const now = new Date();
    const diff = next.getTime() - now.getTime();
    if (diff <= 0) return "now";
    const hours = Math.floor(diff / (1000 * 60 * 60));
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
    if (hours > 0) return `in ${hours}h ${minutes}m`;
    return `in ${minutes}m`;
  }, [state.nextSearchUtc]);

  const statusColor = {
    idle: "text-muted-foreground",
    queued: "text-primary",
    running: "text-primary",
    paused: "text-warning"
  }[state.status] || "text-muted-foreground";

  const statusIcon: Record<string, any> = {
    idle: Clock,
    queued: Zap,
    running: LoaderCircle,
    paused: Pause
  };

  const StatusIcon = statusIcon[state.status] || Clock;
  const explanation = describeAutomationState(state, nextSearchIn);

  return (
    <div className="flex flex-col justify-between gap-3 rounded-xl border border-hairline bg-background/30 p-4 xl:flex-row xl:items-center">
      <div className="flex items-center gap-3">
        <div className={cn("flex h-8 w-8 items-center justify-center rounded-lg", statusColor)}>
          {state.status === "running" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <StatusIcon className="h-4 w-4" />
          )}
        </div>
        <div className="min-w-0">
          <p className="font-semibold text-foreground">{state.libraryName}</p>
          <p className="density-help text-xs text-muted-foreground">
            {explanation}
          </p>
          {state.lastError ? (
            <p className="mt-1 max-w-2xl rounded-md border border-destructive/20 bg-destructive/5 px-2 py-1 text-xs text-destructive">
              Last issue: {state.lastError}
            </p>
          ) : null}
        </div>
      </div>

      <div className="flex items-center gap-2">
        <Badge variant="default" className="font-mono text-xs">
          {formatAutomationStatus(state.status)}
        </Badge>
        <Button
          size="sm"
          variant="ghost"
          disabled={state.status === "running" || isTriggering || isSkipping}
          onClick={() => void onTrigger()}
        >
          {isTriggering ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <RotateCw className="h-4 w-4" />
          )}
          <span className="ml-1">Search now</span>
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={state.status === "running" || isSkipping || isTriggering}
          onClick={() => void onSkip()}
        >
          {isSkipping ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <SkipForward className="h-4 w-4" />
          )}
          <span className="ml-1">Skip this time</span>
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={state.status === "running" || isToggling || isTriggering || isSkipping}
          onClick={() => void onToggle()}
        >
          {isToggling ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : isAutomationEnabled ? (
            <Pause className="h-4 w-4" />
          ) : (
            <Play className="h-4 w-4" />
          )}
          <span className="ml-1">{isAutomationEnabled ? "Pause" : "Resume"}</span>
        </Button>
      </div>
    </div>
  );
}

function SearchHistorySection({ cycles }: { cycles: SearchCycleRunItem[] }) {
  const grouped = useMemo(() => {
    const by: Record<string, SearchCycleRunItem[]> = {};
    cycles.forEach((cycle) => {
      const key = cycle.libraryId;
      if (!by[key]) by[key] = [];
      by[key].push(cycle);
    });
    return by;
  }, [cycles]);

  return (
    <div className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
      <h2 className="mb-[var(--grid-gap)] flex items-center gap-2 font-display text-lg font-semibold text-foreground">
        <ActivityIcon className="h-5 w-5" />
        What Deluno has done
      </h2>

      <div className="space-y-[var(--page-gap)]">
        {Object.entries(grouped).map(([libraryId, runs]) => (
          <div key={libraryId} className="space-y-2">
            <p className="font-semibold text-foreground">{runs[0]?.libraryName}</p>
            <div className="space-y-1">
              {runs.slice(0, 5).map((run) => {
                const notes = parseCycleNotes(run.notesJson);
                return (
                  <div key={run.id} className="flex items-center justify-between rounded-lg bg-background/30 px-3 py-2 text-sm">
                    <div className="min-w-0">
                      <p className="font-mono text-xs text-muted-foreground">
                        {new Date(run.startedUtc).toLocaleString()}
                      </p>
                      <p className="text-muted-foreground">
                        {formatTriggerKind(run.triggerKind)} · {run.plannedCount} considered · {run.queuedCount} sent to downloads · {run.skippedCount} held back
                      </p>
                    </div>
                    <div className="flex flex-col items-end gap-1">
                      <Badge variant={run.status === "completed" ? "default" : "info"}>
                        {run.status}
                      </Badge>
                      <p className="font-mono text-[11px] text-muted-foreground">
                        ~{notes.apiCallCount} calls · {formatBytes(notes.queuedReleaseBytes)}
                      </p>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function CleanupOption({ checked, copy, label, onChange }: { checked: boolean; copy: string; label: string; onChange: (checked: boolean) => void }) {
  return <label className="flex min-h-28 cursor-pointer gap-3 rounded-xl border border-hairline bg-background/40 p-3 text-sm transition hover:border-primary/30"><input className="mt-1" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} /><span><span className="font-semibold text-foreground">{label}</span><span className="mt-1 block leading-relaxed text-muted-foreground">{copy}</span></span></label>;
}

function formatAutomationStatus(status: string) {
  return {
    idle: "Ready",
    queued: "Up next",
    running: "Working",
    paused: "Paused"
  }[status] ?? status;
}

function describeAutomationState(state: LibraryAutomationStateItem, nextSearchIn: string | null) {
  if (state.lastError) return "The last attempt needs attention. Deluno will wait for the library retry rules instead of repeatedly searching.";
  if (state.status === "paused") return "This library is paused. Deluno will not start a background search until you resume it.";
  if (state.status === "running") return "Deluno is evaluating this library now. Approved releases are sent only when they meet the current rules.";
  if (state.searchRequested || state.status === "queued") return "A search has been requested and is waiting for its fair turn in the background queue.";
  if (nextSearchIn) return `Nothing is blocked. The next scheduled search is ${nextSearchIn}.`;
  return "This library is waiting for its first scheduled search.";
}

function formatTriggerKind(triggerKind: string) {
  return triggerKind === "manual" ? "You started this search" : triggerKind === "scheduled" ? "Scheduled search" : "Automation search";
}
