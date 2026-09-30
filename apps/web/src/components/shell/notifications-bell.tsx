/**
 * The notifications bell (#907).
 *
 * It used to jump straight to Activity and swallow the click -- there was no
 * onClick at all, so the dot asked for attention and the button never
 * answered it. Now it opens a small panel with the most recent events, so a
 * failed job can be seen and dealt with from wherever in the app the bell was
 * clicked, without leaving the page behind.
 *
 * The panel is seeded from `/api/activity`, the same endpoint Activity itself
 * reads, and stays live from the same `activity` realtime group while it is
 * open. The merge and formatting are borrowed from the Activity screen
 * (`lib/activity-format.ts`).
 *
 * It is portalled to `document.body` and positioned from the trigger's own
 * box, the way `MenuSelect` is: the topbar clips overflow for its own rounded
 * corners, and a panel left in the tree would be cut off the same way a
 * dropdown menu was.
 *
 * Contract: GET /api/activity?pageSize=…, realtime `ActivityEventAdded`.
 */
import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useNavigate } from "react-router-dom";
import { Bell } from "lucide-react";
import { cn } from "../../lib/utils";
import { fetchPageItems, type ActivityEventItem } from "../../lib/api";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { RealtimeGroups, useSignalREvent } from "../../lib/use-signalr";
import { useDisplayPreferences } from "../../lib/display-preferences";
import { eventLabel, foldRoutineJobEvents, formatEventAge, mattersToTheBell } from "../../lib/activity-format";
import { Chip } from "../ui/chip";
import { TOPBAR_ICON, TOPBAR_ICON_BUTTON } from "../../layouts/topbar-tokens";

const LIMIT = 10;

/**
 * How far back the bell reads before filtering (#916). Routine job noise
 * used to fill all ten slots on its own every half hour; reading a wider
 * page and then keeping only what matters means the ten shown are still ten
 * once the noise is filtered out, not "however many were left after it".
 */
const FETCH_PAGE_SIZE = 60;

/**
 * What the bell actually shows: folded to one row per job run, filtered to
 * what needs attention or changed the library or queue, newest first.
 */
function bellEvents(raw: ActivityEventItem[]): ActivityEventItem[] {
  return foldRoutineJobEvents(raw)
    .map((folded) => folded.event)
    .filter(mattersToTheBell)
    .slice(0, LIMIT);
}

/** Where clicking an event goes: the title, the Queue item, or (failing that) its own Activity entry. */
function eventHref(event: Pick<ActivityEventItem, "relatedEntityType" | "relatedEntityId">): string {
  if (event.relatedEntityType === "movie" && event.relatedEntityId) return `/movies/${event.relatedEntityId}`;
  if (event.relatedEntityType === "series" && event.relatedEntityId) return `/tv/${event.relatedEntityId}`;
  if (event.relatedEntityType === "download_dispatch") return "/queue";
  return "/activity";
}

/** Failed and errored jobs are the ones a person has to act on; everything else is just news. */
function needsAttention(severity: ActivityEventItem["severity"]): boolean {
  return severity === "error" || severity === "warning";
}

export function NotificationsBell({ attention }: { attention: AttentionSnapshot }) {
  const navigate = useNavigate();
  const { preferences } = useDisplayPreferences();
  const listId = useId();
  const headingId = useId();

  const [open, setOpen] = useState(false);
  const [events, setEvents] = useState<ActivityEventItem[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [anchor, setAnchor] = useState<{ top: number; right: number; width: number } | null>(null);

  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const firstItemRef = useRef<HTMLButtonElement>(null);

  function close(returnFocus: boolean) {
    setOpen(false);
    if (returnFocus) triggerRef.current?.focus();
  }

  // Seeded once, the first time the panel is opened -- the bell sits in the
  // shell on every screen, and most of them never open it, so there is no
  // reason to hold its own list warm from the moment the app loads.
  useEffect(() => {
    if (!open || loaded) return;
    let cancelled = false;
    fetchPageItems<ActivityEventItem>(`/api/activity?pageSize=${FETCH_PAGE_SIZE}`)
      .then((items) => {
        if (cancelled) return;
        setEvents(bellEvents(items));
        setLoaded(true);
      })
      .catch(() => {
        if (!cancelled) setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, [open, loaded]);

  // Live while open, from the same feed Activity and the dashboard ticker
  // read. A freshly arrived envelope does not carry where the event links to
  // (see `use-signalr.tsx`'s `ActivityEventAddedEvent`), so a brand-new row
  // falls back to its own Activity entry until the panel is reopened and
  // refetches the fuller shape -- the same trade the ticker already makes.
  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    if (!open) return;
    // A routine job's own lifecycle noise never made it worth waking up for;
    // it is filtered out here the same as it is on the seeded fetch, rather
    // than arriving live and then never being folded away like its seeded
    // siblings.
    if (!mattersToTheBell(event)) return;
    setEvents((current) => {
      if (current.some((item) => item.id === event.id)) return current;
      return [
        {
          id: event.id,
          message: event.message,
          category: event.category,
          label: event.label,
          severity: event.severity,
          detailsJson: null,
          relatedJobId: null,
          relatedEntityType: null,
          relatedEntityId: null,
          createdUtc: event.createdUtc
        },
        ...current
      ].slice(0, LIMIT);
    });
  });

  useEffect(() => {
    if (!open) return;

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        event.preventDefault();
        close(true);
      }
    }

    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      // The panel is portalled out of this subtree, so "inside" means either.
      if (rootRef.current?.contains(target) || panelRef.current?.contains(target)) return;
      setOpen(false);
    }

    document.addEventListener("keydown", onKeyDown);
    document.addEventListener("pointerdown", onPointerDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      document.removeEventListener("pointerdown", onPointerDown);
    };
  }, [open]);

  // Positioned from the trigger's own box rather than left in the topbar's
  // flow -- see the file header on why a portal is required here at all.
  useLayoutEffect(() => {
    if (!open) return;

    function measure() {
      const rect = triggerRef.current?.getBoundingClientRect();
      if (!rect) return;
      const margin = 16;
      const width = Math.min(384, window.innerWidth - margin * 2);
      let right = Math.max(margin, window.innerWidth - rect.right);
      // The bell sits near the right edge of a phone's narrow header, so a
      // panel anchored purely off the trigger's right edge ran off the left
      // of the screen instead. Pull it back in rather than let it clip.
      if (window.innerWidth - right - width < margin) right = window.innerWidth - width - margin;
      setAnchor({ top: rect.bottom + 8, right, width });
    }

    measure();
    window.addEventListener("resize", measure);
    window.addEventListener("scroll", measure, true);
    return () => {
      window.removeEventListener("resize", measure);
      window.removeEventListener("scroll", measure, true);
    };
  }, [open]);

  // Lands on the first row so arrow-free keyboard use (Tab) starts inside the
  // panel rather than needing an extra press to leave the trigger behind.
  useEffect(() => {
    if (open && anchor) (firstItemRef.current ?? panelRef.current)?.focus();
  }, [open, anchor]);

  return (
    <div ref={rootRef} className="relative">
      <button
        ref={triggerRef}
        type="button"
        className={cn(TOPBAR_ICON_BUTTON, "relative")}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        onClick={() => setOpen((current) => !current)}
        aria-label={
          attention.failedJobs > 0
            ? `Notifications — ${attention.failedJobs} failed job${attention.failedJobs === 1 ? "" : "s"}`
            : attention.healthProblems > 0
              ? `Notifications — ${attention.healthProblems} health check${attention.healthProblems === 1 ? "" : "s"} need attention`
              : "Notifications"
        }
      >
        <Bell className={TOPBAR_ICON} strokeWidth={1.75} />
        {attention.failedJobs > 0 || attention.healthProblems > 0 ? (
          <span className="absolute right-1.5 top-1.5 h-[5px] w-[5px] rounded-full bg-destructive shadow-[0_0_0_1.5px_hsl(var(--background)),0_0_6px_hsl(var(--destructive)/0.7)]" />
        ) : null}
      </button>

      {open && anchor ? createPortal(
        <div
          ref={panelRef}
          id={listId}
          role="dialog"
          aria-labelledby={headingId}
          tabIndex={-1}
          style={{ position: "fixed", top: anchor.top, right: anchor.right, width: anchor.width }}
          className="z-[80] overflow-hidden rounded-xl border border-hairline/80 bg-popover shadow-lg dark:border-white/[0.07]"
        >
          <p id={headingId} className="border-b border-hairline px-4 py-3 text-[length:var(--type-caption)] font-semibold uppercase tracking-[0.14em] text-muted-foreground">
            Notifications
          </p>

          {events.length === 0 ? (
            <p className="px-4 py-6 text-center text-[length:var(--type-body-sm)] text-muted-foreground">
              {loaded ? "Nothing has happened yet." : "Loading…"}
            </p>
          ) : (
            <div className="max-h-[360px] overflow-y-auto">
              {events.map((event, index) => (
                <button
                  key={event.id}
                  ref={index === 0 ? firstItemRef : undefined}
                  type="button"
                  onClick={() => {
                    navigate(eventHref(event));
                    close(false);
                  }}
                  className="flex w-full min-w-0 flex-col items-start gap-0.5 border-b border-hairline px-4 py-2.5 text-left transition last:border-b-0 hover:bg-muted/45"
                >
                  <span className="flex w-full min-w-0 items-center gap-2">
                    <span className="min-w-0 flex-1 truncate text-[length:var(--type-body-sm)] text-foreground">
                      {event.message}
                    </span>
                    {needsAttention(event.severity) ? (
                      <Chip tone={event.severity === "error" ? "bad" : "warn"} className="shrink-0">
                        {event.severity === "error" ? "Failed" : "Attention"}
                      </Chip>
                    ) : null}
                  </span>
                  <span className="block truncate text-[length:var(--type-micro)] text-muted-foreground">
                    {eventLabel(event)} · {formatEventAge(event.createdUtc, preferences)}
                  </span>
                </button>
              ))}
            </div>
          )}

          <button
            type="button"
            onClick={() => {
              navigate("/activity");
              close(false);
            }}
            className="block w-full border-t border-hairline px-4 py-3 text-center text-[length:var(--type-body-sm)] font-semibold text-primary transition hover:bg-muted/45"
          >
            Show all activity
          </button>
        </div>,
        document.body
      ) : null}
    </div>
  );
}
