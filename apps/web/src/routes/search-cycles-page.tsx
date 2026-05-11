import { useEffect, useMemo, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import {
  Activity as ActivityIcon,
  Clock,
  LoaderCircle,
  Pause,
  RotateCw,
  Search,
  Zap
} from "lucide-react";
import {
  fetchJson,
  type LibraryAutomationStateItem,
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
  searchCycles: SearchCycleRunItem[];
}

export async function searchCyclesLoader(): Promise<SearchCyclesLoaderData> {
  const [automationStates, searchCycles] = await Promise.all([
    fetchJson<LibraryAutomationStateItem[]>("/api/library-automation"),
    fetchJson<SearchCycleRunItem[]>("/api/search-cycles?take=50")
  ]);

  return { automationStates, searchCycles };
}

export function SearchCyclesPage() {
  const loaderData = useLoaderData() as SearchCyclesLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;

  const { automationStates, searchCycles } = loaderData;
  const revalidator = useRevalidator();

  useEffect(() => {
    const timer = window.setInterval(() => {
      revalidator.revalidate();
    }, 10000);
    return () => window.clearInterval(timer);
  }, [revalidator]);

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

  return (
    <div className="space-y-[var(--page-gap)]">
      {/* ═══════ HERO ═══════ */}
      <PageHero
        eyebrow="Search automation"
        eyebrowIcon={<Search className="h-3 w-3 text-primary" />}
        title="Library search schedules and history"
        subtitle={
          <>
            <span className="font-semibold text-foreground">{activeSearches}</span> active ·{" "}
            <span className={cn("font-semibold", dueForSearch > 0 ? "text-warning" : "text-success")}>
              {dueForSearch > 0 ? `${dueForSearch} due` : "all caught up"}
            </span>
          </>
        }
      />

      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <Clock className="h-4 w-4" />
          Searches are fairly scheduled across libraries to avoid overwhelming indexers.
        </div>
      </div>

      <div className="grid gap-[var(--page-gap)]">
        {/* TV Shows */}
        {automationByType.tv.length > 0 && (
          <LibraryAutomationSection
            title="TV Shows"
            libraries={automationByType.tv}
            onRevalidate={() => revalidator.revalidate()}
          />
        )}

        {/* Movies */}
        {automationByType.movies.length > 0 && (
          <LibraryAutomationSection
            title="Movies"
            libraries={automationByType.movies}
            onRevalidate={() => revalidator.revalidate()}
          />
        )}

        {automationStates.length === 0 && (
          <EmptyState
            icon={Search}
            title="No libraries configured"
            description="Add a library to enable search automation."
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
  onRevalidate
}: {
  title: string;
  libraries: LibraryAutomationStateItem[];
  onRevalidate: () => void;
}) {
  const [triggering, setTriggering] = useState<Set<string>>(new Set());

  const handleTriggerSearch = async (libraryId: string, libraryName: string) => {
    setTriggering((prev) => new Set([...prev, libraryId]));
    try {
      const response = await authedFetch(`/api/libraries/${libraryId}/search-now`, {
        method: "POST"
      });

      if (!response.ok) {
        throw new Error("Could not trigger search");
      }

      toast.success(`Search triggered for ${libraryName}`);
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

  return (
    <div className="rounded-2xl border border-hairline bg-surface-1 p-6">
      <h2 className="mb-4 font-display text-lg font-semibold text-foreground">{title}</h2>
      <div className="space-y-3">
        {libraries.map((library) => (
          <LibraryAutomationCard
            key={library.libraryId}
            state={library}
            onTrigger={() => handleTriggerSearch(library.libraryId, library.libraryName)}
            isTriggering={triggering.has(library.libraryId)}
          />
        ))}
      </div>
    </div>
  );
}

function LibraryAutomationCard({
  state,
  onTrigger,
  isTriggering
}: {
  state: LibraryAutomationStateItem;
  onTrigger: () => Promise<void>;
  isTriggering: boolean;
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

  return (
    <div className="flex items-center justify-between rounded-xl border border-hairline bg-background/30 p-4">
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
            {state.status === "paused"
              ? "Search paused"
              : nextSearchIn
                ? `Next: ${nextSearchIn}`
                : "Never scheduled"}
          </p>
        </div>
      </div>

      <div className="flex items-center gap-2">
        <Badge variant="default" className="font-mono text-xs">
          {state.status}
        </Badge>
        <Button
          size="sm"
          variant="ghost"
          disabled={state.status === "running" || isTriggering}
          onClick={() => void onTrigger()}
        >
          {isTriggering ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <RotateCw className="h-4 w-4" />
          )}
          <span className="ml-1">Trigger</span>
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
    <div className="rounded-2xl border border-hairline bg-surface-1 p-6">
      <h2 className="mb-4 flex items-center gap-2 font-display text-lg font-semibold text-foreground">
        <ActivityIcon className="h-5 w-5" />
        Recent search cycles
      </h2>

      <div className="space-y-6">
        {Object.entries(grouped).map(([libraryId, runs]) => (
          <div key={libraryId} className="space-y-2">
            <p className="font-semibold text-foreground">{runs[0]?.libraryName}</p>
            <div className="space-y-1">
              {runs.slice(0, 5).map((run) => (
                <div key={run.id} className="flex items-center justify-between rounded-lg bg-background/30 px-3 py-2 text-sm">
                  <div className="min-w-0">
                    <p className="font-mono text-xs text-muted-foreground">
                      {new Date(run.startedUtc).toLocaleString()}
                    </p>
                    <p className="text-muted-foreground">
                      {run.plannedCount} planned, {run.queuedCount} queued, {run.skippedCount} skipped
                    </p>
                  </div>
                  <Badge variant={run.status === "completed" ? "default" : "info"}>
                    {run.status}
                  </Badge>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
