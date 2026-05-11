import React, { useEffect, useState } from "react";
import {
  useSignalREvent,
  SearchProgressEvent,
  useSignalRStatus,
} from "../lib/use-signalr";

export interface SearchProgress extends SearchProgressEvent {
  timestamp?: string;
}

interface SearchProgressDisplayProps {
  entityId?: string;
  className?: string;
}

/**
 * Real-time search progress display component.
 * Shows progress bar, results count, ETA, and status.
 * Updates via SignalR events from backend.
 */
export function SearchProgressDisplay({
  entityId,
  className = "",
}: SearchProgressDisplayProps) {
  const [searches, setSearches] = useState<Map<string, SearchProgress>>(
    new Map()
  );
  const signalRStatus = useSignalRStatus();

  // Listen for search progress events
  useSignalREvent("SearchProgress", (event: SearchProgressEvent) => {
    setSearches((prev) => {
      const updated = new Map(prev);
      updated.set(event.id, {
        ...event,
        timestamp: new Date().toISOString(),
      });
      return updated;
    });
  });

  // Filter to specific entity if provided
  const visibleSearches = entityId
    ? Array.from(searches.values()).filter((s) => s.id === entityId)
    : Array.from(searches.values());

  if (visibleSearches.length === 0 && entityId) {
    return null;
  }

  return (
    <div className={`search-progress-display ${className}`.trim()}>
      {visibleSearches.map((search) => (
        <div key={search.id} className="search-progress-item">
          <div className="progress-header">
            <h4 className="search-title">{search.title}</h4>
            <span className={`status-badge status-${search.status}`}>
              {search.status === "searching" && "🔍 Searching"}
              {search.status === "completed" && "✓ Completed"}
              {search.status === "failed" && "✗ Failed"}
            </span>
          </div>

          <div className="progress-bar-container">
            <div className="progress-bar">
              <div
                className="progress-fill"
                style={{ width: `${search.progress}%` }}
              >
                {search.progress > 5 && (
                  <span className="progress-text">{search.progress}%</span>
                )}
              </div>
            </div>
          </div>

          <div className="progress-details">
            <div className="detail-item">
              <span className="detail-label">Results:</span>
              <span className="detail-value">{search.totalResults}</span>
            </div>
            {search.eta && (
              <div className="detail-item">
                <span className="detail-label">ETA:</span>
                <span className="detail-value">{search.eta}</span>
              </div>
            )}
          </div>

          {signalRStatus !== "connected" && (
            <div className="connection-warning">
              ⚠ Real-time updates paused ({signalRStatus})
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
