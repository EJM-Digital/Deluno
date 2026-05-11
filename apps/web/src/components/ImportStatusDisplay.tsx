import React, { useEffect, useState } from "react";
import {
  useSignalREvent,
  ImportStatusEvent,
  useSignalRStatus,
} from "../lib/use-signalr";

export interface ImportStatus extends ImportStatusEvent {
  timestamp?: string;
}

interface ImportStatusDisplayProps {
  entityId?: string;
  className?: string;
}

/**
 * Real-time import status display component.
 * Shows progress bar, status, imported path, and failure reason.
 * Updates via SignalR events from backend.
 */
export function ImportStatusDisplay({
  entityId,
  className = "",
}: ImportStatusDisplayProps) {
  const [imports, setImports] = useState<Map<string, ImportStatus>>(
    new Map()
  );
  const signalRStatus = useSignalRStatus();

  // Listen for import status events
  useSignalREvent("ImportStatus", (event: ImportStatusEvent) => {
    setImports((prev) => {
      const updated = new Map(prev);
      updated.set(event.id, {
        ...event,
        timestamp: new Date().toISOString(),
      });
      return updated;
    });
  });

  // Filter to specific entity if provided
  const visibleImports = entityId
    ? Array.from(imports.values()).filter((i) => i.id === entityId)
    : Array.from(imports.values());

  if (visibleImports.length === 0 && entityId) {
    return null;
  }

  return (
    <div className={`import-status-display ${className}`.trim()}>
      {visibleImports.map((importItem) => (
        <div key={importItem.id} className="import-status-item">
          <div className="status-header">
            <h4 className="release-title">{importItem.releaseName}</h4>
            <span className={`status-badge status-${importItem.status}`}>
              {importItem.status === "importing" && "↻ Importing"}
              {importItem.status === "completed" && "✓ Completed"}
              {importItem.status === "failed" && "✗ Failed"}
            </span>
          </div>

          <div className="progress-bar-container">
            <div className="progress-bar">
              <div
                className="progress-fill"
                style={{ width: `${importItem.progress}%` }}
              >
                {importItem.progress > 5 && (
                  <span className="progress-text">{importItem.progress}%</span>
                )}
              </div>
            </div>
          </div>

          {importItem.importedPath && (
            <div className="import-info">
              <span className="info-label">📁 Imported to:</span>
              <span className="info-value">{importItem.importedPath}</span>
            </div>
          )}

          {importItem.failureReason && (
            <div className="failure-info">
              <span className="failure-label">Error:</span>
              <span className="failure-value">{importItem.failureReason}</span>
            </div>
          )}

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
