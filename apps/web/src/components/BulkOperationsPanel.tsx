import React, { useState } from "react";
import "./BulkOperationsPanel.css";

export interface BulkOperationResult {
  itemId: string;
  itemTitle: string;
  succeeded: boolean;
  errorMessage?: string;
  metadata?: Record<string, string | null>;
}

export interface BulkOperationResponse {
  totalProcessed: number;
  successCount: number;
  failureCount: number;
  operation: string;
  results: BulkOperationResult[];
}

interface BulkOperationsPanelProps {
  selectedIds: string[];
  mediaType: "movie" | "series";
  onOperationStart?: () => void;
  onOperationComplete?: (response: BulkOperationResponse) => void;
  onClose?: () => void;
}

/**
 * Bulk operations panel for movies and series
 * Supports: remove, quality, monitoring, search
 */
export function BulkOperationsPanel({
  selectedIds,
  mediaType,
  onOperationStart,
  onOperationComplete,
  onClose,
}: BulkOperationsPanelProps) {
  const [operation, setOperation] = useState<string>("monitoring");
  const [monitored, setMonitored] = useState<boolean>(true);
  const [qualityProfileId, setQualityProfileId] = useState<string>("");
  const [isExecuting, setIsExecuting] = useState(false);
  const [operationResult, setOperationResult] = useState<
    BulkOperationResponse | null
  >(null);
  const [error, setError] = useState<string | null>(null);

  const isValid =
    selectedIds.length > 0 &&
    operation &&
    (operation !== "quality" || qualityProfileId);

  const handleExecute = async () => {
    if (!isValid) {
      setError("Please select an operation and required parameters.");
      return;
    }

    setIsExecuting(true);
    setError(null);
    onOperationStart?.();

    try {
      const endpoint = `/api/${mediaType === "movie" ? "movies" : "series"}/bulk`;
      const body = {
        [mediaType === "movie" ? "movieIds" : "seriesIds"]: selectedIds,
        operation,
        ...(operation === "monitoring" && { monitored }),
        ...(operation === "quality" && { qualityProfileId }),
      };

      const response = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(
          errorData.message || `Operation failed: ${response.statusText}`
        );
      }

      const result: BulkOperationResponse = await response.json();
      setOperationResult(result);
      onOperationComplete?.(result);
    } catch (err) {
      const errorMsg =
        err instanceof Error ? err.message : "Operation failed";
      setError(errorMsg);
    } finally {
      setIsExecuting(false);
    }
  };

  if (operationResult) {
    return (
      <div className="bulk-operations-panel">
        <div className="panel-header">
          <h3>Bulk Operation Result</h3>
          <button
            className="close-button"
            onClick={onClose}
            aria-label="Close panel"
          >
            ✕
          </button>
        </div>

        <div className="operation-result">
          <div className="result-summary">
            <div className="result-stat">
              <span className="stat-label">Total Processed:</span>
              <span className="stat-value">{operationResult.totalProcessed}</span>
            </div>
            <div className="result-stat success">
              <span className="stat-label">Successful:</span>
              <span className="stat-value">{operationResult.successCount}</span>
            </div>
            <div className="result-stat failure">
              <span className="stat-label">Failed:</span>
              <span className="stat-value">{operationResult.failureCount}</span>
            </div>
            <div className="result-stat">
              <span className="stat-label">Operation:</span>
              <span className="stat-value capitalize">
                {operationResult.operation}
              </span>
            </div>
          </div>

          {operationResult.results.length > 0 && (
            <div className="result-details">
              <h4>Details</h4>
              <div className="result-list">
                {operationResult.results.map((result) => (
                  <div
                    key={result.itemId}
                    className={`result-item ${
                      result.succeeded ? "success" : "failure"
                    }`}
                  >
                    <span className="result-status">
                      {result.succeeded ? "✓" : "✕"}
                    </span>
                    <div className="result-content">
                      <div className="result-title">{result.itemTitle}</div>
                      {result.errorMessage && (
                        <div className="result-error">{result.errorMessage}</div>
                      )}
                      {result.metadata && Object.keys(result.metadata).length > 0 && (
                        <div className="result-metadata">
                          {Object.entries(result.metadata).map(([key, value]) => (
                            <span key={key} className="metadata-item">
                              {key}: {value}
                            </span>
                          ))}
                        </div>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          <button className="action-button primary" onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="bulk-operations-panel">
      <div className="panel-header">
        <h3>Bulk Operations</h3>
        <button
          className="close-button"
          onClick={onClose}
          aria-label="Close panel"
        >
          ✕
        </button>
      </div>

      <div className="panel-content">
        <div className="operation-info">
          <div className="info-stat">
            <span className="label">Selected {mediaType}s:</span>
            <span className="value">{selectedIds.length}</span>
          </div>
        </div>

        <div className="operation-selector">
          <label htmlFor="operation-select">Operation:</label>
          <select
            id="operation-select"
            value={operation}
            onChange={(e) => setOperation(e.target.value)}
            disabled={isExecuting}
          >
            <option value="monitoring">Update Monitoring</option>
            <option value="quality">Update Quality Profile</option>
            <option value="search">Search</option>
            <option value="remove">Remove</option>
          </select>
        </div>

        {operation === "monitoring" && (
          <div className="operation-config">
            <label htmlFor="monitored-toggle">Monitored:</label>
            <select
              id="monitored-toggle"
              value={monitored ? "yes" : "no"}
              onChange={(e) => setMonitored(e.target.value === "yes")}
              disabled={isExecuting}
            >
              <option value="yes">Yes</option>
              <option value="no">No</option>
            </select>
          </div>
        )}

        {operation === "quality" && (
          <div className="operation-config">
            <label htmlFor="quality-profile">Quality Profile ID:</label>
            <input
              id="quality-profile"
              type="text"
              placeholder="Enter quality profile ID"
              value={qualityProfileId}
              onChange={(e) => setQualityProfileId(e.target.value)}
              disabled={isExecuting}
            />
          </div>
        )}

        {operation === "remove" && (
          <div className="operation-warning">
            <strong>⚠ Warning:</strong> This will remove {selectedIds.length}{" "}
            {mediaType}{mediaType === "movie" ? "" : ""}s from your library. This
            action cannot be undone.
          </div>
        )}

        {error && <div className="error-message">{error}</div>}

        <div className="panel-actions">
          <button
            className="action-button primary"
            onClick={handleExecute}
            disabled={!isValid || isExecuting}
          >
            {isExecuting ? "Processing..." : "Execute"}
          </button>
          <button
            className="action-button secondary"
            onClick={onClose}
            disabled={isExecuting}
          >
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
