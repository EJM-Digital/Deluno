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
import { FilterChips, MoreFilters } from "../ui/filter-chips";
import type { ActivityEventItem } from "../../lib/api";
import { eventLabel } from "../../lib/activity-format";

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
  /** The kind of event in plain words (#1086). `category` is only the group used for colour and filtering. */
  label?: string;
  severity: Severity;
  detail?: string;
  createdUtc: string;
}

function apiToTimeline(item: ActivityEventItem): TimelineEvent {
  return {
    id: item.id,
    message: item.message,
    category: normalizeCategory(item.category ?? "system"),
    label: eventLabel(item),
    severity: severityForCategory(item.category ?? "system"),
    detail: item.detailsJson ?? undefined,
    createdUtc: item.createdUtc
  };
}

/* ── Category config ─────────────────────────────────────────────── */
const CAT_CONFIG: Record<
  string,
  // Both classes spelled out. `text-${rail.replace("bg-", "")}` built the icon
  // colour at runtime, and Tailwind generates only the literals it can see — so
  // any of these whose `text-` twin was not written out somewhere else in the
  // app was purged and the icon rendered with no colour.
  { icon: React.ComponentType<{ className?: string }>; rail: string; icon_colour: string; label: string }
> = {
  download: { icon: Download, rail: "bg-info", icon_colour: "text-info", label: "Download" },
  import: { icon: FileInput, rail: "bg-success", icon_colour: "text-success", label: "Import" },
  search: { icon: Search, rail: "bg-primary", icon_colour: "text-primary", label: "Search" },
  error: { icon: XCircle, rail: "bg-destructive", icon_colour: "text-destructive", label: "Error" },
  notification: { icon: Info, rail: "bg-warning", icon_colour: "text-warning", label: "Notification" },
  system: { icon: Settings2, rail: "bg-muted-foreground", icon_colour: "text-muted-foreground", label: "System" }
};

const defaultCat = { icon: Info, rail: "bg-muted-foreground", icon_colour: "text-muted-foreground", label: "Event" };

const SEV_CONFIG: Record<Severity, { icon: React.ComponentType<{ className?: string }>; text: string; bg: string }> = {
  info: { icon: Info, text: "text-info", bg: "bg-info/10 border-info/20" },
  success: { icon: CheckCircle2, text: "text-success", bg: "bg-success/10 border-success/20" },
  warning: { icon: AlertTriangle, text: "text-warning", bg: "bg-warning/10 border-warning/20" },
  error: { icon: XCircle, text: "text-destructive", bg: "border-destructive/20" }
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

function normalizeCategory(category: string): Category {
  if (category.includes("download") || category.includes("dispatch")) return "download";
  if (category.includes("import") || category.includes("filesystem")) return "import";
  if (category.includes("search") || category.includes("release")) return "search";
  if (category.includes("processing") || category.includes("processor")) return "system";
  if (category.includes("failed") || category.includes("error") || category.includes("rejected") || category.includes("replacement")) return "error";
  if (category.includes("notification")) return "notification";
  return "system";
}

function severityForCategory(category: string): Severity {
  if (category.includes("failed") || category.includes("error") || category.includes("rejected") || category.includes("replacement")) return "error";
  if (category.includes("held") || category.includes("waiting") || category.includes("force") || category.includes("processing")) return "warning";
  if (category.includes("completed") || category.includes("dispatched") || category.includes("auto-queued")) return "success";
  return "info";
}

/* ── Component ───────────────────────────────────────────────────── */
const PAGE = 50;

/**
 * The timeline's filters and the events they leave, held by the screen that owns the card so the filters can sit in
 * the card's header (#1115) and the body shows only the events.
 */
export function useAuditTimeline(events: ActivityEventItem[], liveEvents: TimelineEvent[] = [], maxVisible = 200) {
  const [query, setQuery] = useState("");
  const [catFilter, setCatFilter] = useState<string>("all");
  const [sevFilter, setSevFilter] = useState<Severity | "all">("all");
  const [showCount, setShowCount] = useState(PAGE);

  const converted = useMemo(() => events.map(apiToTimeline), [events]);
  const all = useMemo(() => {
    // A live event carries the raw code as its category; group it the way the
    // fetched ones are, and name it from the plain-words label it arrived with.
    const live = liveEvents.map((e) => ({ ...e, label: e.label ?? eventLabel({ category: e.category }), category: normalizeCategory(e.category) }));
    const combined = [...live, ...converted];
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
          return e.message.toLowerCase().includes(q) || (e.label ?? e.category).toLowerCase().includes(q);
        }
        return true;
      })
      .slice(0, maxVisible);
  }, [all, catFilter, sevFilter, query, maxVisible]);

  const counts = useMemo(() => {
    const byCategory: Record<string, number> = {};
    for (const e of all) byCategory[e.category] = (byCategory[e.category] ?? 0) + 1;
    return byCategory;
  }, [all]);

  const liveIds = useMemo(() => new Set(liveEvents.map((e) => e.id)), [liveEvents]);

  return {
    query,
    setQuery: (next: string) => { setQuery(next); setShowCount(PAGE); },
    catFilter,
    setCatFilter: (next: string) => { setCatFilter(next); setShowCount(PAGE); },
    sevFilter,
    setSevFilter: (next: Severity | "all") => { setSevFilter(next); setShowCount(PAGE); },
    showCount,
    showMore: () => setShowCount((n) => n + PAGE),
    all,
    filtered,
    counts,
    liveIds,
    /** The card's count: how many events, and whether a filter is narrowing them. */
    countLabel: `${filtered.length} event${filtered.length !== 1 ? "s" : ""}${query || catFilter !== "all" || sevFilter !== "all" ? " matching" : ""}`
  };
}

export type AuditTimelineState = ReturnType<typeof useAuditTimeline>;

const SEVERITY_OPTIONS = [
  { value: "all", label: "All severities" },
  { value: "info", label: "Info" },
  { value: "success", label: "Success" },
  { value: "warning", label: "Warning" },
  { value: "error", label: "Error" }
];

/** The category chips and the severity picker, for the card's header after its title (the Indexers card's pattern). */
export function AuditTimelineFilters({ timeline }: { timeline: AuditTimelineState }) {
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-2 lg:flex-nowrap">
      <FilterChips<string>
        label="Filter by kind of event"
        size="sm"
        value={timeline.catFilter}
        onValueChange={timeline.setCatFilter}
        options={[
          { value: "all", label: "All", count: timeline.all.length },
          ...ALL_CATEGORIES.map((cat) => ({ value: cat, label: CAT_CONFIG[cat]?.label ?? cat, count: timeline.counts[cat] ?? 0 }))
        ]}
      />
      <MoreFilters
        groups={[{ label: "Severity", ariaLabel: "Filter by severity", value: timeline.sevFilter, onValueChange: (next) => timeline.setSevFilter(next as Severity | "all"), options: SEVERITY_OPTIONS }]}
      />
    </div>
  );
}

export function AuditTimeline({ timeline }: { timeline: AuditTimelineState }) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const topRef = useRef<HTMLDivElement>(null);
  const { filtered, showCount, liveIds } = timeline;

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
    <div className="flex flex-col gap-(--grid-gap)">
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
            const isFirst = i === 0 && liveIds.has(event.id);

            return (
              <li
                key={event.id}
                className={cn(
                  "relative flex gap-(--grid-gap) pb-5",
                  isFirst && "animate-[reveal-item_0.3s_ease_both]"
                )}
              >
                {/* Rail dot */}
                <div className="relative z-10 flex h-10 w-10 shrink-0 items-center justify-center">
                  <div
                    className={cn(
                      "flex h-7 w-7 items-center justify-center rounded-full border border-hairline bg-card shadow-xs",
                    )}
                  >
                    <CatIcon className={cn("h-3.5 w-3.5", cat.icon_colour)} />
                  </div>
                </div>

                {/* Content */}
                <div className="min-w-0 flex-1 pt-1">
                  <div className="flex flex-wrap items-start justify-between gap-x-3 gap-y-0.5">
                    <p className="text-(length:--type-body-sm) font-medium leading-snug text-foreground">
                      {event.message}
                    </p>
                    <div className="flex shrink-0 items-center gap-2">
                      <span
                        className={cn(
                          "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-(length:--type-micro) font-semibold uppercase tracking-wider",
                          sev.bg, sev.text
                        )}
                      >
                        <SevIcon className="h-2.5 w-2.5" />
                        {event.severity}
                      </span>
                    </div>
                  </div>

                  <div className="mt-0.5 flex items-center gap-2 text-(length:--type-caption) text-muted-foreground">
                    <span className="rounded-full border border-hairline px-1.5 py-0.5">
                      {event.label ?? eventLabel({ category: event.category })}
                    </span>
                    <span title={absoluteTime(event.createdUtc)}>{relativeTime(event.createdUtc)}</span>
                    <span className="opacity-50">·</span>
                    <span className="text-(length:--type-micro) tabular-nums">{absoluteTime(event.createdUtc)}</span>
                  </div>

                  {event.detail ? (
                    <button
                      type="button"
                      onClick={() => toggleExpand(event.id)}
                      className="mt-1.5 flex items-center gap-1 text-(length:--type-caption) text-muted-foreground hover:text-foreground"
                    >
                      <ChevronDown
                        className={cn("h-3 w-3 transition-transform", isExp && "rotate-180")}
                      />
                      {isExp ? "Hide detail" : "Show detail"}
                    </button>
                  ) : null}

                  {isExp && event.detail ? (
                    <pre className="mt-2 rounded-xl border border-hairline bg-surface-2 p-3 font-mono text-(length:--type-caption) text-foreground/80 whitespace-pre-wrap">
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
            onClick={timeline.showMore}
            className="mt-2 flex w-full items-center justify-center gap-2 rounded-xl border border-dashed border-hairline py-3 text-(length:--type-caption) text-muted-foreground transition hover:border-primary/30 hover:text-foreground"
          >
            <ChevronDown className="h-4 w-4" />
            Load {Math.min(50, filtered.length - showCount)} more events
          </button>
        ) : null}
      </div>
    </div>
  );
}
