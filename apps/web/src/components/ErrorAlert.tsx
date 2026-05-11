import React from "react";
import "./ErrorAlert.css";

export interface ErrorData {
  code: string;
  message: string;
  severity: "info" | "warning" | "error" | "critical";
  isRetryable?: boolean;
  recoverySuggestions?: string[];
  traceId?: string;
  occurredAt?: string;
}

interface ErrorAlertProps {
  error: ErrorData;
  onDismiss?: () => void;
  onRetry?: () => void;
  className?: string;
  showDetails?: boolean;
}

/**
 * Error alert display component with severity levels and recovery suggestions.
 * Shows user-friendly error messages with actionable guidance.
 */
export function ErrorAlert({
  error,
  onDismiss,
  onRetry,
  className = "",
  showDetails = false,
}: ErrorAlertProps) {
  const [isExpanded, setIsExpanded] = React.useState(showDetails);

  const getIcon = () => {
    switch (error.severity) {
      case "info":
        return "ℹ";
      case "warning":
        return "⚠";
      case "error":
        return "✕";
      case "critical":
        return "‼";
      default:
        return "•";
    }
  };

  const getTitle = () => {
    switch (error.severity) {
      case "info":
        return "Information";
      case "warning":
        return "Warning";
      case "error":
        return "Error";
      case "critical":
        return "Critical Error";
      default:
        return "Alert";
    }
  };

  return (
    <div className={`error-alert severity-${error.severity} ${className}`.trim()}>
      <div className="alert-header">
        <div className="alert-title">
          <span className="alert-icon">{getIcon()}</span>
          <h3 className="alert-heading">{getTitle()}</h3>
        </div>
        <button
          className="alert-dismiss"
          onClick={onDismiss}
          aria-label="Dismiss error"
          type="button"
        >
          ✕
        </button>
      </div>

      <div className="alert-content">
        <p className="alert-message">{error.message}</p>

        {error.recoverySuggestions && error.recoverySuggestions.length > 0 && (
          <div className="recovery-suggestions">
            <h4 className="suggestions-title">What you can do:</h4>
            <ul className="suggestions-list">
              {error.recoverySuggestions.map((suggestion, index) => (
                <li key={index} className="suggestion-item">
                  {suggestion}
                </li>
              ))}
            </ul>
          </div>
        )}

        {error.isRetryable && onRetry && (
          <button
            className="retry-button"
            onClick={onRetry}
            type="button"
            aria-label="Retry operation"
          >
            ↻ Retry
          </button>
        )}
      </div>

      {(error.code || error.traceId) && (
        <div className="alert-footer">
          <button
            className="details-toggle"
            onClick={() => setIsExpanded(!isExpanded)}
            aria-expanded={isExpanded}
            type="button"
          >
            {isExpanded ? "▼" : "▶"} Details
          </button>

          {isExpanded && (
            <div className="alert-details">
              {error.code && (
                <div className="detail-row">
                  <span className="detail-label">Code:</span>
                  <code className="detail-value">{error.code}</code>
                </div>
              )}
              {error.traceId && (
                <div className="detail-row">
                  <span className="detail-label">Trace ID:</span>
                  <code className="detail-value">{error.traceId}</code>
                </div>
              )}
              {error.occurredAt && (
                <div className="detail-row">
                  <span className="detail-label">Occurred:</span>
                  <span className="detail-value">
                    {new Date(error.occurredAt).toLocaleString()}
                  </span>
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Container for displaying multiple errors
 */
export function ErrorContainer({
  errors,
  onDismiss,
  className = "",
}: {
  errors: ErrorData[];
  onDismiss?: (code: string) => void;
  className?: string;
}) {
  if (errors.length === 0) {
    return null;
  }

  return (
    <div className={`error-container ${className}`.trim()}>
      {errors.map((error) => (
        <ErrorAlert
          key={error.code}
          error={error}
          onDismiss={() => onDismiss?.(error.code)}
        />
      ))}
    </div>
  );
}
