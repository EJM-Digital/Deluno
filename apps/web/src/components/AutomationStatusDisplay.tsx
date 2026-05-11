import React, { useEffect, useState } from "react";
import {
  useSignalREvent,
  AutomationStatusEvent,
  useSignalRStatus,
} from "../lib/use-signalr";

export interface AutomationStatus extends AutomationStatusEvent {
  timestamp?: string;
}

interface AutomationStatusDisplayProps {
  automationId?: string;
  className?: string;
}

/**
 * Real-time automation status display component.
 * Shows execution progress, items processed, and scheduling information.
 * Updates via SignalR events from backend.
 */
export function AutomationStatusDisplay({
  automationId,
  className = "",
}: AutomationStatusDisplayProps) {
  const [automations, setAutomations] = useState<Map<string, AutomationStatus>>(
    new Map()
  );
  const signalRStatus = useSignalRStatus();

  // Listen for automation status events
  useSignalREvent("AutomationStatus", (event: AutomationStatusEvent) => {
    const key = `${event.automationId}-${event.libraryId}`;
    setAutomations((prev) => {
      const updated = new Map(prev);
      updated.set(key, {
        ...event,
        timestamp: new Date().toISOString(),
      });
      return updated;
    });
  });

  // Filter to specific automation if provided
  const visibleAutomations = automationId
    ? Array.from(automations.values()).filter((a) => a.automationId === automationId)
    : Array.from(automations.values());

  if (visibleAutomations.length === 0 && automationId) {
    return null;
  }

  const formatDateTime = (isoString: string) => {
    try {
      return new Date(isoString).toLocaleString(undefined, {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      });
    } catch {
      return isoString;
    }
  };

  return (
    <div className={`automation-status-display ${className}`.trim()}>
      {visibleAutomations.map((automation) => {
        const progress = automation.totalItems > 0
          ? (automation.itemsProcessed / automation.totalItems) * 100
          : 0;

        return (
          <div
            key={`${automation.automationId}-${automation.libraryId}`}
            className="automation-status-item"
          >
            <div className="automation-header">
              <div className="automation-title-info">
                <h4 className="automation-name">{automation.automationId}</h4>
                <span className="library-badge">{automation.libraryId}</span>
              </div>
              <span className={`status-badge status-${automation.status}`}>
                {automation.status === "queued" && "⏳ Queued"}
                {automation.status === "running" && "▶ Running"}
                {automation.status === "completed" && "✓ Completed"}
                {automation.status === "failed" && "✗ Failed"}
              </span>
            </div>

            {automation.status === "running" && automation.totalItems > 0 && (
              <>
                <div className="progress-bar-container">
                  <div className="progress-bar">
                    <div
                      className="progress-fill"
                      style={{ width: `${progress}%` }}
                    >
                      {progress > 5 && (
                        <span className="progress-text">{Math.round(progress)}%</span>
                      )}
                    </div>
                  </div>
                </div>

                <div className="progress-stats">
                  <div className="stat-item">
                    <span className="stat-label">Items processed:</span>
                    <span className="stat-value">
                      {automation.itemsProcessed} / {automation.totalItems}
                    </span>
                  </div>
                </div>
              </>
            )}

            <div className="automation-schedule">
              {automation.lastRunUtc && (
                <div className="schedule-item">
                  <span className="schedule-label">Last run:</span>
                  <span className="schedule-value">
                    {formatDateTime(automation.lastRunUtc)}
                  </span>
                </div>
              )}
              {automation.nextRunUtc && (
                <div className="schedule-item">
                  <span className="schedule-label">Next run:</span>
                  <span className="schedule-value">
                    {formatDateTime(automation.nextRunUtc)}
                  </span>
                </div>
              )}
            </div>

            {signalRStatus !== "connected" && (
              <div className="connection-warning">
                ⚠ Real-time updates paused ({signalRStatus})
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
