/**
 * Deluno Audit Timeline.
 *
 * A rich, filterable, searchable timeline of every system event.
 * Categories: download · import · search · error · notification · system
 * Supports real-time prepend via the `newEvents` prop (from WebSocket).
 *
 * Each entry has:
 *   · Category icon + color-coded left rail
 *   · Message + optional detail
 *   · Relative + absolute timestamp
 *   · Severity badge
 */

import { useMemo, useRef, useState } from "react";
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  Download,
  FileInput,
  Info,
  Search,
  Settings2,
  XCircle
} from "lucide-react";
import { cn } from "../../lib/utils";
import { Input } from "../ui/input";
import type { ActivityEventItem } from "../../lib/api";

/* ── Types ───────────────────────────────────────────────────────── */
type Severity = "info" | "success" | "warning" | "error";
type Category =
  | "download"
  | "import"
  | "search"
  | "error"
  | "notification"
  | "system"
  | string;

/* Unify the backend ActivityEventItem with live WS events */
export interface TimelineEvent {
  id: string;
  message: string;
  category: Category;
  severity: Severity;
  detail?: string;
  createdUtc: string;
}

function apiToTimeline(item: ActivityEventItem): TimelineEvent {
  return {
    id: item.id,
    message: item.message,
    category: item.category ?? "system",
    severity: (item.severity as Severity) ?? "info",
    createdUtc: item.createdUtc
  };
}

/* ── Category config ─────────────────────────────────────────────── */
const CAT_CONFIG: Record<
  string,
  { icon: React.ComponentType<{ className?: string }>; rail: string; label: string }
> = {
  download: { icon: Download, rail: "bg-info", label: "Download" },
  import: { icon: FileInput, rail: "bg-success", label: "Import" },
  search: { icon: Search, rail: "bg-primary", label: "Search" },
  error: { icon: XCircle, rail: "bg-destructive", label: "Error" },
  notification: { icon: Info, rail: "bg-warning", label: "Notification" },
  system: { icon: Settings2, rail: "bg-muted-foreground", label: "System" }
};

const defaultCat = { icon: Info, rail: "bg-muted-foreground", label: "Event" };

const SEV_CONFIG: Record<Severity, { icon: React.ComponentType<{ className?: string }>; text: string; bg: string }> = {
  info: { icon: Info, text: "text-info", bg: "bg-info/10 border-info/20" },
  success: { icon: CheckCircle2, text: "text-success", bg: "bg-success/10 border-success/20" },
  warning: { icon: AlertTriangle, text: "text-warning", bg: "bg-warning/10 border-warning/20" },
  error: { icon: XCircle, text: "text-destructive", bg: "bg-destructive/8 border-destructive/20" }
};

/* ── Helpers ─────────────────────────────────────────────────────── */
function relativeTime(iso: string) {
  const diff = Date.now() - new Date(iso).getTime();
  const secs = Math.floor(diff / 1000);
  if (secs < 60) return "just now";
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  return `${days}d ago`;
}

function absoluteTime(iso: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit"
  }).format(new Date(iso));
}

const ALL_CATEGORIES = Object.keys(CAT_CONFIG);

/* ── Component ───────────────────────────────────────────────────── */
interface AuditTimelineProps {
  events: ActivityEventItem[];
  /** Prepended live events from WebSocket */
  liveEvents?: TimelineEvent[];
  maxVisible?: number;
}

export function AuditTimeline({ events, liveEvents = [], maxVisible = 200 }: AuditTimelineProps) {
  const [query, setQuery] = useState("");
  const [catFilter, setCatFilter] = useState<string>("all");
  const [sevFilter, setSevFilter] = useState<Severity | "all">("all");
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [showCount, setShowCount] = useState(50);
  const topRef = useRef<HTMLDivElement>(null);

  const converted = useMemo(() => events.map(apiToTimeline), [events]);
  const all = useMemo(() => {
    const combined = [...liveEvents, ...converted];
    /* deduplicate by id, keep order */
    const seen = new Set<string>();
    return combined.filter((e) => {
      if (seen.has(e.id)) return false;
      seen.add(e.id);
      return true;
    });
  }, [liveEvents, converted]);

  const filtered = useMemo(() => {
    return all
      .filter((e) => {
        if (catFilter !== "all" && e.category !== catFilter) return false;
        if (sevFilter !== "all" && e.severity !== sevFilter) return false;
        if (query) {
          const q = query.toLowerCase();
          return e.message.toLowerCase().includes(q) || e.category.toLowerCase().includes(q);
        }
        return true;
      })
      .slice(0, maxVisible);
  }, [all, catFilter, sevFilter, query, maxVisible]);

  const visible = filtered.slice(0, showCount);
  const hasMore = filtered.length > showCount;

  function toggleExpand(id: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  return (
    <div className="flex flex-col gap-4">
      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative flex-1 min-w-[180px]">
          <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => { setQuery(e.target.value); setShowCount(50); }}
            placeholder="Search events…"
            className="h-9 pl-8 text-[13px]"
          />
        </div>

        {/* Category filter */}
        <div className="flex flex-wrap gap-1">
          <FilterChip active={catFilter === "all"} onClick={() => { setCatFilter("all"); setShowCount(50); }}>
            All
          </FilterChip>
          {ALL_CATEGORIES.map((cat) => (
            <FilterChip
              key={cat}
              active={catFilter === cat}
              onClick={() => { setCatFilter(cat === catFilter ? "all" : cat); setShowCount(50); }}
            >
              {CAT_CONFIG[cat]?.label ?? cat}
            </FilterChip>
          ))}
        </div>

        {/* Severity filter */}
        <select
          value={sevFilter}
          onChange={(e) => { setSevFilter(e.target.value as Severity | "all"); setShowCount(50); }}
          className="h-9 rounded-xl border border-hairline bg-surface-2 px-3 text-[12.5px] text-foreground outline-none"
        >
          <option value="all">All severities</option>
          <option value="info">Info</option>
          <option value="success">Success</option>
          <option value="warning">Warning</option>
          <option value="error">Error</option>
        </select>
      </div>

      {/* Counter */}
      <p className="text-[11px] uppercase tracking-[0.16em] text-muted-foreground">
        {filtered.length} event{filtered.length !== 1 ? "s" : ""}
        {(query || catFilter !== "all" || sevFilter !== "all") ? " matching current filters" : ""}
      </p>

      {/* Timeline */}
      <div ref={topRef} className="relative">
        {/* Vertical rail */}
        <div className="absolute left-[19px] top-0 bottom-0 w-px bg-hairline" />

        <ol className="space-y-0" aria-label="Audit timeline">
          {visible.map((event, i) => {
            const cat = CAT_CONFIG[event.category] ?? defaultCat;
            const sev = SEV_CONFIG[event.severity] ?? SEV_CONFIG.info;
            const SevIcon = sev.icon;
            const CatIcon = cat.icon;
            const isExp = expanded.has(event.id);
            const isFirst = i === 0 && liveEvents.some((e) => e.id === event.id);

            return (
              <li
                key={event.id}
                className={cn(
                  "relative flex gap-4 pb-5",
                  isFirst && "animate-[reveal-item_0.3s_ease_both]"
                )}
              >
                {/* Rail dot */}
                <div className="relative z-10 flex h-10 w-10 shrink-0 items-center justify-center">
                  <div
                    className={cn(
                      "flex h-7 w-7 items-center justify-center rounded-full border border-hairline bg-card shadow-sm",
                    )}
                  >
                    <CatIcon className={cn("h-3.5 w-3.5", `text-${cat.rail.replace("bg-", "")}`)} />
                  </div>
                </div>

                {/* Content */}
                <div className="min-w-0 flex-1 pt-1">
                  <div className="flex flex-wrap items-start justify-between gap-x-3 gap-y-0.5">
                    <p className="text-[13.5px] font-medium leading-snug text-foreground">
                      {event.message}
                    </p>
                    <div className="flex shrink-0 items-center gap-2">
                      <span
                        className={cn(
                          "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider",
                          sev.bg, sev.text
                        )}
                      >
                        <SevIcon className="h-2.5 w-2.5" />
                        {event.severity}
                      </span>
                    </div>
                  </div>

                  <div className="mt-0.5 flex items-center gap-2 text-[11px] text-muted-foreground">
                    <span className="rounded-full border border-hairline px-1.5 py-0.5 capitalize">
                      {event.category}
                    </span>
                    <span title={absoluteTime(event.createdUtc)}>{relativeTime(event.createdUtc)}</span>
                    <span className="opacity-50">·</span>
                    <span className="font-mono text-[10px]">{absoluteTime(event.createdUtc)}</span>
                  </div>

                  {event.detail ? (
                    <button
                      type="button"
                      onClick={() => toggleExpand(event.id)}
                      className="mt-1.5 flex items-center gap-1 text-[11.5px] text-muted-foreground hover:text-foreground"
                    >
                      <ChevronDown
                        className={cn("h-3 w-3 transition-transform", isExp && "rotate-180")}
                      />
                      {isExp ? "Hide detail" : "Show detail"}
                    </button>
                  ) : null}

                  {isExp && event.detail ? (
                    <pre className="mt-2 rounded-xl border border-hairline bg-surface-2 p-3 font-mono text-[11.5px] text-foreground/80 whitespace-pre-wrap">
                      {event.detail}
                    </pre>
                  ) : null}
                </div>
              </li>
            );
          })}

          {visible.length === 0 ? (
            <li className="flex flex-col items-center py-12 text-muted-foreground">
              <Search className="mb-3 h-8 w-8 opacity-30" />
              <p className="text-sm">No events match the current filters.</p>
            </li>
          ) : null}
        </ol>

        {hasMore ? (
          <button
            type="button"
            onClick={() => setShowCount((n) => n + 50)}
            className="mt-2 flex w-full items-center justify-center gap-2 rounded-xl border border-dashed border-hairline py-3 text-[12.5px] text-muted-foreground transition hover:border-primary/30 hover:text-foreground"
          >
            <ChevronDown className="h-4 w-4" />
            Load {Math.min(50, filtered.length - showCount)} more events
          </button>
        ) : null}
      </div>
    </div>
  );
}

function FilterChip({
  children,
  active,
  onClick
}: {
  children: React.ReactNode;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-full border px-2.5 py-0.5 text-[11.5px] font-medium transition-all select-none",
        active
          ? "border-primary/40 bg-primary/10 text-foreground shadow-[inset_0_0_0_1px_hsl(var(--primary)/0.2)]"
          : "border-hairline bg-surface-1 text-muted-foreground hover:border-primary/25 hover:text-foreground"
      )}
    >
      {children}
    </button>
  );
}
