import React from "react";
import "./SearchScoringBreakdown.css";

export interface ScoringBreakdownData {
  releaseName: string;
  decisionStatus: "selected" | "rejected" | "override" | "pending";
  totalScore: number;
  customFormatScore: number;
  qualityDelta: number;
  seederScore: number;
  sizeScore: number;
  quality: string;
  releaseGroup?: string;
  meetsCutoff: boolean;
  indexerName: string;
  summary: string;
  decisionReasons?: string[];
  riskFlags?: string[];
  seeders?: number;
  sizeBytes?: number;
  estimatedBitrateMbps?: number;
}

interface SearchScoringBreakdownProps {
  data: ScoringBreakdownData;
  className?: string;
  expanded?: boolean;
  onToggleExpanded?: (expanded: boolean) => void;
}

/**
 * Detailed scoring breakdown display for search results.
 * Shows point-by-point rationale for why a release was selected or rejected.
 */
export function SearchScoringBreakdown({
  data,
  className = "",
  expanded = false,
  onToggleExpanded,
}: SearchScoringBreakdownProps) {
  const [isExpanded, setIsExpanded] = React.useState(expanded);

  const handleToggle = () => {
    const newState = !isExpanded;
    setIsExpanded(newState);
    onToggleExpanded?.(newState);
  };

  const formatBytes = (bytes: number) => {
    const sizes = ["B", "KB", "MB", "GB", "TB"];
    let len = bytes;
    let order = 0;
    while (len >= 1024 && order < sizes.length - 1) {
      order++;
      len = len / 1024;
    }
    return `${len.toFixed(2)} ${sizes[order]}`;
  };

  const getScoreColor = (score: number, max: number = 100) => {
    const percentage = (score / max) * 100;
    if (percentage >= 80) return "score-excellent";
    if (percentage >= 60) return "score-good";
    if (percentage >= 40) return "score-fair";
    return "score-poor";
  };

  return (
    <div className={`search-scoring-breakdown ${className}`.trim()}>
      <div className="scoring-header" onClick={handleToggle}>
        <div className="header-title">
          <h4 className="release-title">{data.releaseName}</h4>
          <span className={`decision-badge decision-${data.decisionStatus}`}>
            {data.decisionStatus === "selected" && "✓ Selected"}
            {data.decisionStatus === "rejected" && "✗ Rejected"}
            {data.decisionStatus === "override" && "⚠ Override"}
            {data.decisionStatus === "pending" && "⏳ Pending"}
          </span>
        </div>
        <div className="header-score">
          <div className={`total-score ${getScoreColor(data.totalScore)}`}>
            {data.totalScore}
          </div>
          <button
            className="expand-button"
            aria-expanded={isExpanded}
            aria-label={isExpanded ? "Collapse" : "Expand"}
          >
            {isExpanded ? "▼" : "▶"}
          </button>
        </div>
      </div>

      {isExpanded && (
        <div className="scoring-details">
          <div className="summary-section">
            <p className="release-summary">{data.summary}</p>
            {data.releaseGroup && (
              <p className="release-group">
                <span className="group-label">Release Group:</span> {data.releaseGroup}
              </p>
            )}
          </div>

          {/* Score Components */}
          <div className="score-components">
            <h5>Score Breakdown</h5>
            <div className="score-grid">
              <div className={`score-item ${getScoreColor(data.customFormatScore, 100)}`}>
                <span className="score-label">Custom Formats</span>
                <span className="score-value">{data.customFormatScore}</span>
              </div>
              <div className={`score-item ${getScoreColor(data.qualityDelta + 50, 100)}`}>
                <span className="score-label">Quality Delta</span>
                <span className="score-value">{data.qualityDelta > 0 ? "+" : ""}{data.qualityDelta}</span>
              </div>
              <div className={`score-item ${getScoreColor(data.seederScore, 100)}`}>
                <span className="score-label">Seeders</span>
                <span className="score-value">{data.seederScore}</span>
              </div>
              <div className={`score-item ${getScoreColor(data.sizeScore, 100)}`}>
                <span className="score-label">File Size</span>
                <span className="score-value">{data.sizeScore}</span>
              </div>
            </div>
          </div>

          {/* Release Info */}
          <div className="release-info">
            <h5>Release Information</h5>
            <div className="info-grid">
              <div className="info-item">
                <span className="info-label">Quality:</span>
                <span className="info-value">{data.quality}</span>
              </div>
              <div className="info-item">
                <span className="info-label">Indexer:</span>
                <span className="info-value">{data.indexerName}</span>
              </div>
              {data.seeders !== undefined && (
                <div className="info-item">
                  <span className="info-label">Seeders:</span>
                  <span className="info-value">{data.seeders}</span>
                </div>
              )}
              {data.sizeBytes !== undefined && (
                <div className="info-item">
                  <span className="info-label">Size:</span>
                  <span className="info-value">{formatBytes(data.sizeBytes)}</span>
                </div>
              )}
              {data.estimatedBitrateMbps !== undefined && (
                <div className="info-item">
                  <span className="info-label">Est. Bitrate:</span>
                  <span className="info-value">{data.estimatedBitrateMbps.toFixed(1)} Mbps</span>
                </div>
              )}
              <div className="info-item">
                <span className="info-label">Meets Cutoff:</span>
                <span className={`info-value ${data.meetsCutoff ? "cutoff-yes" : "cutoff-no"}`}>
                  {data.meetsCutoff ? "✓ Yes" : "✗ No"}
                </span>
              </div>
            </div>
          </div>

          {/* Decision Reasons */}
          {data.decisionReasons && data.decisionReasons.length > 0 && (
            <div className="decision-reasons">
              <h5>Decision Reasons</h5>
              <ul className="reasons-list">
                {data.decisionReasons.map((reason, index) => (
                  <li key={index} className="reason-item">
                    {reason}
                  </li>
                ))}
              </ul>
            </div>
          )}

          {/* Risk Flags */}
          {data.riskFlags && data.riskFlags.length > 0 && (
            <div className="risk-flags">
              <h5>⚠ Risk Flags</h5>
              <div className="flags-list">
                {data.riskFlags.map((flag, index) => (
                  <span key={index} className="risk-flag">
                    {flag}
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
