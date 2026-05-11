import { useState } from "react";
import { LoaderCircle, ShieldCheck, ShieldX } from "lucide-react";
import { Button } from "../ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../ui/card";
import { Badge } from "../ui/badge";
import type { SeriesEpisodeInventoryItem } from "../../lib/api";

interface EpisodeMonitoringWidgetProps {
  episodes: SeriesEpisodeInventoryItem[];
  selectedCount: number;
  onMonitor: (monitored: boolean) => void;
  isBusy: boolean;
}

export function EpisodeMonitoringWidget({
  episodes,
  selectedCount,
  onMonitor,
  isBusy
}: EpisodeMonitoringWidgetProps) {
  const monitoredCount = episodes.filter((e) => e.monitored).length;
  const unmonitoredCount = episodes.length - monitoredCount;
  const selectedMonitored = episodes.filter(
    (e) => e.monitored && selectedCount > 0
  ).length;

  const stats = [
    { label: "Monitored", value: monitoredCount, total: episodes.length },
    { label: "Unmonitored", value: unmonitoredCount, total: episodes.length }
  ];

  return (
    <Card>
      <CardHeader>
        <CardTitle>Episode monitoring status</CardTitle>
        <CardDescription>
          {selectedCount > 0
            ? `${selectedCount} episode${selectedCount === 1 ? "" : "s"} selected`
            : "Overview of episode monitoring across this series"}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          {stats.map((stat) => (
            <div key={stat.label} className="rounded-lg border border-hairline bg-surface-1 p-3">
              <p className="text-xs text-muted-foreground">{stat.label}</p>
              <p className="text-xl font-semibold text-foreground mt-1">
                {stat.value}
                <span className="text-sm text-muted-foreground ml-1">/ {stat.total}</span>
              </p>
              <div className="mt-2 h-1.5 rounded-full bg-surface-2 overflow-hidden">
                <div
                  className="h-full bg-primary rounded-full transition-all"
                  style={{ width: `${(stat.value / stat.total) * 100}%` }}
                />
              </div>
            </div>
          ))}
        </div>

        {selectedCount > 0 && (
          <div className="rounded-lg border border-hairline bg-surface-1 p-3">
            <p className="text-xs text-muted-foreground mb-2">Selected episodes</p>
            <div className="flex flex-wrap gap-2 mb-3">
              <Badge variant="info">{selectedCount} selected</Badge>
              {selectedMonitored > 0 && (
                <Badge variant="success">{selectedMonitored} monitored</Badge>
              )}
            </div>
          </div>
        )}

        <div className="flex flex-col gap-2">
          <Button
            onClick={() => onMonitor(true)}
            disabled={isBusy || selectedCount === 0}
            className="w-full"
          >
            {isBusy ? (
              <LoaderCircle className="h-4 w-4 animate-spin mr-2" />
            ) : (
              <ShieldCheck className="h-4 w-4 mr-2" />
            )}
            Monitor selected
          </Button>
          <Button
            onClick={() => onMonitor(false)}
            variant="outline"
            disabled={isBusy || selectedCount === 0}
            className="w-full"
          >
            {isBusy ? (
              <LoaderCircle className="h-4 w-4 animate-spin mr-2" />
            ) : (
              <ShieldX className="h-4 w-4 mr-2" />
            )}
            Unmonitor selected
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
