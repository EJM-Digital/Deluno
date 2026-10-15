/**
 * The notifications bell (#907).
 *
 * It used to jump straight to Activity and swallow the click -- there was no
 * onClick at all, so the dot asked for attention and the button never
 * answered it. Now it opens a small panel with the most recent events, so a
 * failed job can be seen and dealt with from wherever in the app the bell was
 * clicked, without leaving the page behind.
 *
 * Since #1087 the bell is the one place for problems on every screen. Its badge
 * is the real-problem count (the two Dashboard badges added together, none at
 * zero), and its panel lists Needs you first, the same rows and fix buttons the
 * Dashboard's Needs you draws, then the recent events below.
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
import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { createPortal } from "react-dom";
import { useNavigate } from "react-router-dom";
import { Bell } from "lucide-react";
import { cn } from "../../lib/utils";
import { fetchPageItems, type ActivityEventItem } from "../../lib/api";
import { attentionTotal, type AttentionSnapshot } from "../../lib/use-attention";
import { useAction } from "../../routes/dashboard/downloads-kit";
import { AlsoWorthALook, bellRowElement } from "../../routes/dashboard/needs-you";
import { bellRows, type BellRow } from "../../lib/bell-rows";
import { RealtimeGroups, useSignalREvent } from "../../lib/use-signalr";
import { queryClient } from "../../lib/route-cache";
import { activityItemFromPush, prependOnce } from "../../lib/activity-live-patch";
import { useDisplayPreferences } from "../../lib/display-preferences";
import { activityLinkForEvent, activityLinkForStatus } from "../../lib/activity-history";
import { eventLabel, foldRoutineJobEvents, formatEventAge, mattersToTheBell } from "../../lib/activity-format";
import { Chip } from "../ui/chip";
import { TOPBAR_ICON, TOPBAR_ICON_BUTTON } from "../../layouts/topbar-tokens";

const LIMIT = 10;

/** The Recent list lives in the shared cache, so it can be kept current whether the panel is open or not (#1179). */
const RECENT_KEY = ["bell", "recent"] as const;

/**
 * How far back the bell reads before filtering (#916). Routine job noise
 * used to fill all ten slots on its own every half hour; reading a wider
 * page and then keeping only what matters means the ten shown are still ten
 * once the noise is filtered out, not "however many were left after it". A busy
 * library writes hundreds of search lines between two notable events, so 60 was
 * often all noise and the section read "Nothing has happened yet" beside a full
 * Activity page; 400 reaches back to the last real events, and with none the
 * section is not drawn (Needs you above and Show all activity cover the rest).
 */
const FETCH_PAGE_SIZE = 400;

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

/** Where clicking an event goes: its own entry in the Activity history (#1100), which links on to the title or the Queue. */
function eventHref(event: Pick<ActivityEventItem, "id" | "createdUtc">): string {
  return activityLinkForEvent(event);
}

/** Failed and errored jobs are the ones a person has to act on; everything else is just news. */
function needsAttention(severity: ActivityEventItem["severity"]): boolean {
  return severity === "error" || severity === "warning";
}

const NO_EVENTS: ActivityEventItem[] = [];

export function NotificationsBell({ attention }: { attention: AttentionSnapshot }) {
  const navigate = useNavigate();
  const { preferences } = useDisplayPreferences();
  const listId = useId();
  const headingId = useId();
  const action = useAction();
  const total = attentionTotal(attention);
  // Two sections: what needs you about your media, and what needs you about Deluno itself (#1098).
  const rows = useMemo(() => bellRows(attention.summary), [attention.summary]);
  const drawSection = (label: string, list: BellRow[]) =>
    list.length === 0 ? null : (
      <section aria-label={label} key={label}>
        <p className="px-4 pb-0.5 pt-2 text-(length:--type-micro) font-semibold uppercase tracking-[0.12em] text-muted-foreground">{label}</p>
        <ul>{list.map((row) => bellRowElement(row, action))}</ul>
      </section>
    );
  const nothingNeedsYou = rows.media.length === 0 && rows.deluno.length === 0;
  const { leftovers, suggestions } = attention.summary;

  const [open, setOpen] = useState(false);
  // Read the first time the panel opens; after that the pushes below keep it current, open or closed.
  const recent = useQuery(
    {
      queryKey: RECENT_KEY,
      queryFn: async () => bellEvents(await fetchPageItems<ActivityEventItem>(`/api/activity?pageSize=${FETCH_PAGE_SIZE}`)),
      enabled: open,
      staleTime: Infinity
    },
    queryClient
  );
  const events = recent.data ?? NO_EVENTS;
  const loaded = recent.status !== "pending";
  const [anchor, setAnchor] = useState<{ top: number; right: number; width: number } | null>(null);

  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const firstItemRef = useRef<HTMLButtonElement>(null);

  function close(returnFocus: boolean) {
    setOpen(false);
    if (returnFocus) triggerRef.current?.focus();
  }

  // A new event goes into the cached list the moment it is pushed, whether the panel is open or not. A list that was
  // never read is left alone: opening the panel reads it, and it is complete then.
  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    // A routine job's own lifecycle noise never made it worth waking up for; it is filtered out here the same as it
    // is on the first read, rather than arriving live and then never being folded away like its siblings.
    if (!mattersToTheBell(event)) return;
    queryClient.setQueryData<ActivityEventItem[]>(RECENT_KEY, (current) => (current ? prependOnce(current, activityItemFromPush(event), LIMIT) : current));
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
      // Wide enough for a title, its reason and the row's buttons side by side
      // (James, 1 Oct: "i think this can be wider"); a phone gets its width less
      // the margins.
      const width = Math.min(608, window.innerWidth - margin * 2);
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
        aria-label={total > 0 ? `Notifications — ${total} ${total === 1 ? "thing needs" : "things need"} a look` : "Notifications"}
      >
        <Bell className={TOPBAR_ICON} strokeWidth={1.75} />
        {total > 0 ? (
          <span className="absolute -right-0.5 -top-0.5 inline-flex h-[16px] min-w-[16px] items-center justify-center rounded-full bg-warning px-1 text-(length:--type-micro) font-bold tabular-nums leading-none text-background shadow-[0_0_0_1.5px_hsl(var(--background))]">
            {total > 99 ? "99+" : total}
          </span>
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
          className="z-80 overflow-hidden rounded-xl border border-hairline/80 bg-popover shadow-lg dark:border-white/[0.07]"
        >
          <p id={headingId} className="border-b border-hairline px-4 py-3 text-(length:--type-caption) font-semibold uppercase tracking-[0.14em] text-muted-foreground">
            Notifications
          </p>

          <section aria-label="Needs you" className="border-b border-hairline">
            <p className="flex items-center gap-2 px-4 pb-1 pt-3 text-(length:--type-micro) font-semibold uppercase tracking-[0.12em] text-muted-foreground">
              Needs you
              {total > 0 ? <span className="tabular-nums text-warning">{total}</span> : null}
            </p>
            {nothingNeedsYou ? (
              <p className="px-4 pb-3 text-(length:--type-body-sm) text-muted-foreground">Nothing needs you right now.</p>
            ) : (
              // A link in a row goes to the screen that clears it, so the panel closes behind it.
              <div
                className="max-h-[360px] overflow-y-auto"
                onClick={(event) => {
                  if ((event.target as HTMLElement).closest("a")) close(false);
                }}
              >
                {drawSection("Your media", rows.media)}
                {drawSection("Deluno", rows.deluno)}
              </div>
            )}
            {rows.updates.length > 0 ? (
              // Ready, not a problem: listed on their own and never part of the number above.
              <div
                className="max-h-[200px] overflow-y-auto border-t border-hairline"
                onClick={(event) => {
                  if ((event.target as HTMLElement).closest("a")) close(false);
                }}
              >
                {drawSection("Updates ready", rows.updates)}
              </div>
            ) : null}
            {leftovers + suggestions > 0 ? (
              <div
                className="border-t border-hairline px-4 py-2 text-(length:--type-caption) text-muted-foreground"
                onClick={(event) => {
                  if ((event.target as HTMLElement).closest("a")) close(false);
                }}
              >
                <AlsoWorthALook attention={{ alsoWorthALook: { leftovers, suggestions } }} />
              </div>
            ) : null}
          </section>

          {loaded && events.length === 0 ? null : <p className="px-4 pb-1 pt-3 text-(length:--type-micro) font-semibold uppercase tracking-[0.12em] text-muted-foreground">Recent</p>}
          {!loaded ? (
            <p className="px-4 py-4 text-center text-(length:--type-body-sm) text-muted-foreground">Loading…</p>
          ) : events.length === 0 ? null : (
            <div className="max-h-[240px] overflow-y-auto">
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
                    <span title={event.message} className="min-w-0 flex-1 truncate text-(length:--type-body-sm) text-foreground">
                      {event.message}
                    </span>
                    {needsAttention(event.severity) ? (
                      <Chip tone={event.severity === "error" ? "bad" : "warn"} className="shrink-0">
                        {event.severity === "error" ? "Failed" : "Attention"}
                      </Chip>
                    ) : null}
                  </span>
                  <span className="block truncate text-(length:--type-micro) text-muted-foreground">
                    {eventLabel(event)} · {formatEventAge(event.createdUtc, preferences)}
                  </span>
                </button>
              ))}
            </div>
          )}

          <button
            type="button"
            onClick={() => {
              navigate(activityLinkForStatus());
              close(false);
            }}
            className="block w-full border-t border-hairline px-4 py-3 text-center text-(length:--type-body-sm) font-semibold text-primary transition hover:bg-muted/45"
          >
            Show all activity
          </button>
        </div>,
        document.body
      ) : null}
    </div>
  );
}
