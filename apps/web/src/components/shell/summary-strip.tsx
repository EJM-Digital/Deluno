import * as React from "react";
import { Link } from "react-router-dom";
import { cn } from "../../lib/utils";
import { AttentionDot, type Severity } from "./attention-dot";

export interface SummaryTile {
  id: string;
  label: string;
  value: React.ReactNode;
  hint?: React.ReactNode;
  severity?: Severity;
  to?: string;
  onClick?: () => void;
  icon?: React.ComponentType<{ className?: string }>;
}

export function SummaryStrip({
  tiles,
  className
}: {
  tiles: SummaryTile[];
  className?: string;
}) {
  return (
    <div
      role="list"
      className={cn(
        "flex gap-3 overflow-x-auto snap-x snap-mandatory px-0 no-scrollbar scroll-fade-x",
        "md:grid md:grid-cols-[repeat(auto-fit,minmax(180px,1fr))] md:overflow-visible md:snap-none",
        className
      )}
    >
      {tiles.map((tile) => (
        <SummaryTileCard key={tile.id} tile={tile} />
      ))}
    </div>
  );
}

function SummaryTileCard({ tile }: { tile: SummaryTile }) {
  const Icon = tile.icon;
  const borderTone =
    tile.severity === "danger"
      ? "border-state-danger/40"
      : tile.severity === "warn"
        ? "border-state-warn/40"
        : tile.severity === "ok"
          ? "border-state-ok/30"
          : tile.severity === "info"
            ? "border-state-info/30"
            : "border-hairline";

  const body = (
    <div
      className={cn(
        "relative flex min-w-[180px] snap-start flex-col gap-1.5 rounded-2xl border bg-card p-3.5 text-left transition-colors",
        borderTone,
        (tile.to || tile.onClick) &&
          "hover:bg-card-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      )}
    >
      <div className="flex items-center justify-between gap-2 text-xs text-muted-foreground">
        <span className="inline-flex items-center gap-2">
          {Icon ? <Icon className="h-3.5 w-3.5" /> : null}
          <span className="uppercase tracking-[0.12em]">{tile.label}</span>
        </span>
        {tile.severity ? <AttentionDot severity={tile.severity} /> : null}
      </div>
      <p className="tabular font-display text-2xl font-semibold text-foreground leading-tight">
        {tile.value}
      </p>
      {tile.hint ? (
        <p className="text-xs text-muted-foreground">{tile.hint}</p>
      ) : null}
    </div>
  );

  if (tile.to) {
    return (
      <Link role="listitem" to={tile.to} className="block focus:outline-none">
        {body}
      </Link>
    );
  }
  if (tile.onClick) {
    return (
      <button
        role="listitem"
        type="button"
        onClick={tile.onClick}
        className="block text-left focus:outline-none"
      >
        {body}
      </button>
    );
  }
  return <div role="listitem">{body}</div>;
}
