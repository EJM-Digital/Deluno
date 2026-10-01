/**
 * The one small tile of the right column (#1098): an indexer, a download client or
 * a connection. Every tile is the same template, a light and a name on top, a line
 * of what it did under it, and, for a client, a thin speed bar. A tile flashes when
 * the thing it stands for is used: asked (blue), answered (green) or failed (red);
 * a switched-off one is grey.
 */
import { createContext, useContext, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { motionAllowed } from "../../../lib/page-motion";
import { MIN_TILE_REM } from "./layout";
import { cn } from "../../../lib/utils";
import type { Tone } from "../../../lib/status-tones";
import { LiveCard as Panel } from "./live-card";

/** How the tiles of the right column are drawn: `cols` across when the column is held to the window (otherwise as many as fit), and `compact` as a light and a name on one line. */
export interface TileLayout {
  cols?: number;
  compact: boolean;
}

export const TileLayoutContext = createContext<TileLayout>({ compact: false });

export type TileState = "asked" | "answered" | "failed" | "busy" | "lit" | "off";

const STATE_CLASS: Record<TileState, string> = {
  asked: "border-info/80 shadow-[0_0_0_3px_hsl(var(--info)/0.15)]",
  answered: "border-success/70 bg-success/10",
  failed: "border-destructive/80 bg-destructive/10",
  busy: "border-[hsl(var(--stage-process)/0.8)] shadow-[0_0_0_3px_hsl(var(--stage-process)/0.15)]",
  lit: "border-success/70 bg-success/10",
  off: "opacity-50"
};

const DOT_CLASS: Record<Tone, string> = { ok: "bg-success", warn: "bg-warning", bad: "bg-destructive", info: "bg-info", idle: "bg-muted-foreground/50" };

export function Tile({ to, name, tone, state, pulse = false, meta, bar, label }: {
  to: string;
  name: string;
  tone: Tone;
  state?: TileState | null;
  /** The light beats: being asked, moving, working. */
  pulse?: boolean;
  meta: ReactNode;
  /** A thin bar under the meta, 0 to 100. */
  bar?: number;
  /** The accessible name: "NZBgeek: 46 of 100 today". */
  label: string;
}) {
  const { compact } = useContext(TileLayoutContext);
  return (
    <li className="min-w-0">
      <Link
        to={to}
        aria-label={label}
        title={label}
        data-state={state ?? undefined}
        className={cn(
          "block min-w-0 rounded-[10px] border border-hairline bg-surface-2/60 px-2.5 transition-[border-color,box-shadow,background-color] duration-300 hover:bg-primary/[0.06] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          compact ? "py-[3px]" : "py-1.5",
          state ? STATE_CLASS[state] : null
        )}
      >
        <span className="flex min-w-0 items-center gap-1.5 text-[length:var(--type-caption)] font-semibold leading-tight text-foreground">
          <span aria-hidden className={cn("h-2 w-2 shrink-0 rounded-full", DOT_CLASS[state === "asked" ? "info" : tone], pulse && motionAllowed() && "live-blink")} />
          <span className="min-w-0 truncate">{name}</span>
        </span>
        {compact ? null : <span className="mt-0.5 block truncate text-[length:var(--type-micro)] leading-tight text-muted-foreground">{meta}</span>}
        {bar !== undefined && !compact ? (
          <span aria-hidden className="mt-1 block h-[3px] overflow-hidden rounded-full bg-surface-3">
            <span className="live-gauge block h-full rounded-full bg-info" style={{ width: `${Math.max(0, Math.min(100, bar))}%` }} />
          </span>
        ) : null}
      </Link>
    </li>
  );
}

/** A group of tiles in a card of its own: the title, a count line, a link to where it is managed, and the tiles in a grid that fills the width it has. */
export function TileGroup({ title, count, to, toLabel = "Manage", empty, children }: {
  title: string;
  count: ReactNode;
  to: string;
  toLabel?: string;
  /** What the card says when it has no tiles. */
  empty: string;
  children: ReactNode[] | ReactNode;
}) {
  const { cols, compact } = useContext(TileLayoutContext);
  const none = Array.isArray(children) ? children.length === 0 : !children;
  return (
    <Panel title={title} count={count} to={to} toLabel={toLabel} iconOnly className="shrink-0">
      {none ? (
        <p className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-body-sm)] text-muted-foreground">{empty}</p>
      ) : (
        <ul
          className={cn("grid px-[var(--card-pad-x)]", compact ? "gap-1 py-1" : "gap-1.5 py-2.5")}
          style={{ gridTemplateColumns: cols ? `repeat(${cols}, minmax(0, 1fr))` : `repeat(auto-fill, minmax(${MIN_TILE_REM + 1}rem, 1fr))` }}
        >
          {children}
        </ul>
      )}
    </Panel>
  );
}
