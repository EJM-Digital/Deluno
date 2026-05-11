import type { SeriesSearchHistoryItem } from "../../lib/api";
import { Badge } from "../ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../ui/card";
import { EmptyState } from "../shell/empty-state";
import { Clock } from "lucide-react";

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

interface EpisodeSearchHistoryProps {
  searches: SeriesSearchHistoryItem[];
  episodeId?: string;
}

export function EpisodeSearchHistory({ searches, episodeId }: EpisodeSearchHistoryProps) {
  const filtered = episodeId
    ? searches.filter((item) => item.episodeId === episodeId)
    : searches.filter((item) => item.episodeId !== null);

  if (!filtered.length) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Episode search history</CardTitle>
          <CardDescription>
            Timeline of search attempts for {episodeId ? "this episode" : "episodes in this series"}.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <EmptyState
            size="sm"
            variant="search"
            title="No episode searches yet"
            description="Episode searches will appear here as they're executed."
          />
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Episode search history</CardTitle>
        <CardDescription>
          Timeline of search attempts for {episodeId ? "this episode" : "episodes in this series"}.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-2">
        <div className="space-y-2 max-h-96 overflow-y-auto">
          {filtered.map((search) => (
            <div key={search.id} className="flex items-start gap-3 rounded-lg border border-hairline bg-surface-1 p-3 text-sm">
              <Clock className="h-4 w-4 mt-0.5 text-muted-foreground flex-shrink-0" />
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap mb-1">
                  {search.seasonNumber !== null && search.episodeNumber !== null && (
                    <p className="text-xs font-mono text-foreground">
                      S{String(search.seasonNumber).padStart(2, "0")}E{String(search.episodeNumber).padStart(2, "0")}
                    </p>
                  )}
                  <Badge variant={search.outcome === "matched" ? "success" : search.outcome === "no_match" ? "default" : "warning"} className="text-xs">
                    {formatSearchOutcome(search.outcome)}
                  </Badge>
                  {search.releaseName && (
                    <span className="text-xs text-muted-foreground truncate">{search.releaseName}</span>
                  )}
                </div>
                {search.indexerName && (
                  <p className="text-xs text-muted-foreground">
                    via {search.indexerName}
                  </p>
                )}
                <p className="text-xs text-muted-foreground mt-1">{formatDateTime(search.createdUtc)}</p>
              </div>
            </div>
          ))}
        </div>
      </CardContent>
    </Card>
  );
}

function formatSearchOutcome(outcome: string): string {
  const map: Record<string, string> = {
    matched: "Matched",
    no_match: "No match",
    error: "Error",
    skipped: "Skipped",
    pending: "Pending"
  };
  return map[outcome] || outcome;
}
