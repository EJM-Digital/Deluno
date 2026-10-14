/**
 * Small shared pieces for the Dashboard's Downloads and System tabs (#1082).
 *
 * A panel is a ListCard that fills its grid cell and clips its own body (a list
 * puts its rows in a `FitList`, which shows whole rows only), so the tab never
 * scrolls the page. `useAction` runs one button's request, says
 * what happened in plain words and refreshes the stored answers behind the
 * screen. Nothing here reaches a client, indexer or disk while a screen waits:
 * every call is a person pressing a button.
 */
import { useCallback, useState, type ReactNode } from "react";
import { Loader2 } from "lucide-react";
import { Link } from "react-router-dom";
import { toast } from "../../components/shell/toaster";
import { Button } from "../../components/ui/button";
import { ListCard } from "../../components/ui/list-card";
import { readErrorText } from "../../lib/api/client";
import { queryClient } from "../../lib/route-cache";
import { authedFetch } from "../../lib/use-auth";
import { cn } from "../../lib/utils";

/** The header link every panel carries: where you act on what the panel shows. */
export function PanelLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Link
      to={to}
      className="whitespace-nowrap text-(length:--type-caption) font-medium text-primary hover:underline focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring"
    >
      {children} <span aria-hidden>{"→"}</span>
    </Link>
  );
}

export function Panel({
  title,
  count,
  to,
  linkLabel,
  className,
  footer,
  children
}: {
  title: string;
  count?: ReactNode;
  to: string;
  linkLabel: string;
  className?: string;
  footer?: ReactNode;
  children: ReactNode;
}) {
  return (
    <ListCard title={title} count={count} actions={<PanelLink to={to}>{linkLabel}</PanelLink>} className={cn("flex min-h-0 flex-col", className)}>
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">{children}</div>
      {footer ? <div className="border-t border-hairline px-(--card-pad-x) py-2 text-(length:--type-caption) text-muted-foreground">{footer}</div> : null}
    </ListCard>
  );
}

/** One plain sentence inside a panel that has nothing to list. */
export function PanelNote({ children }: { children: ReactNode }) {
  return <p className="px-(--card-pad-x) py-4 text-(length:--type-body-sm) text-muted-foreground">{children}</p>;
}

/** A label and a value on one line, for the small facts panels. */
export function FactRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3 px-(--card-pad-x) py-1.5 text-(length:--type-body-sm)">
      <span className="shrink-0 whitespace-nowrap text-muted-foreground">{label}</span>
      <span className="min-w-0 truncate max-md:whitespace-normal max-md:break-words text-right font-medium text-foreground">{children}</span>
    </div>
  );
}

export interface ActionState {
  busy: string | null;
  run: (key: string, task: () => Promise<string | void>) => Promise<void>;
}

/** Runs one action at a time, reports it in plain words and refreshes what is on screen. */
export function useAction(): ActionState {
  const [busy, setBusy] = useState<string | null>(null);
  const run = useCallback(async (key: string, task: () => Promise<string | void>) => {
    setBusy(key);
    try {
      const message = await task();
      if (message) toast.success(message);
      // Only the screens that are open are asked again; the summary is one of them.
      void queryClient.invalidateQueries();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That did not work. Nothing was changed.");
    } finally {
      setBusy(null);
    }
  }, []);
  return { busy, run };
}

/** POSTs (or another verb) and throws with the server's own words when it says no. */
export async function send(path: string, failure: string, init: { method?: string; body?: unknown } = {}): Promise<{ message?: string } | null> {
  const response = await authedFetch(path, {
    method: init.method ?? "POST",
    headers: init.body === undefined ? undefined : { "Content-Type": "application/json" },
    body: init.body === undefined ? undefined : JSON.stringify(init.body)
  });
  if (!response.ok) throw new Error(await readErrorText(response, failure));
  return (await response.json().catch(() => null)) as { message?: string } | null;
}

/**
 * One size for every action button on the Live board and in the bell's list (#1096):
 * the same height, the same width (wide enough for "Search again" and "Open
 * Transfers", never cut), the same outlined box. A quieter action (Give up) keeps the
 * box and only softens its text. Every card and row lines its buttons up in the same
 * columns, and because this is the one class everything shares, they cannot drift.
 */
export const UNIFORM_BUTTON = "h-7 w-30 shrink-0 justify-center whitespace-nowrap px-1.5";

/** The one size of button on a card of the Live board: every card puts two side by side in the same place, so they must all be the same box. */
export const CARD_BUTTON = "h-[calc(var(--type-caption)*2)] w-21 shrink-0 justify-center px-1 text-(length:--type-caption)";

/** A small button with its own busy spinner and a unique accessible name. */
export function ActionButton({
  label,
  name,
  busyKey,
  state,
  onClick,
  variant = "outline",
  uniform = false,
  card = false,
  hint
}: {
  label: string;
  /** Names the button for assistive tech: "Retry Sintel (2010)", never just "Retry". */
  name: string;
  busyKey: string;
  state: ActionState;
  onClick: () => void;
  variant?: "outline" | "ghost" | "destructive";
  /** The one size every button on the Live board and in the bell shares: see UNIFORM_BUTTON. A "ghost" action keeps the box and softens its text. */
  uniform?: boolean;
  /** The smaller box every card on the Live board shares (CARD_BUTTON). */
  card?: boolean;
  /** The hover note: what the button does, and what it will not touch. */
  hint?: string;
}) {
  const working = state.busy === busyKey;
  return (
    <Button
      type="button"
      variant={variant}
      size="sm"
      aria-label={name}
      title={hint}
      disabled={state.busy !== null}
      onClick={onClick}
      className={card ? cn(CARD_BUTTON, variant === "ghost" && "text-muted-foreground hover:text-foreground") : uniform ? cn(UNIFORM_BUTTON, variant === "ghost" && "text-muted-foreground hover:text-foreground") : undefined}
    >
      {working ? <Loader2 aria-hidden className="h-3.5 w-3.5 animate-spin" /> : null}
      {label}
    </Button>
  );
}

/* ------------------------------------------------------------ wording */

export function plural(count: number, one: string, many = `${one}s`): string {
  return `${count.toLocaleString("en-AU")} ${count === 1 ? one : many}`;
}

/** "3.9 MB/s": always a reading, and 0 reads "0 MB/s". */
export function formatSpeed(mbps: number | null | undefined): string {
  const value = Number.isFinite(mbps) && mbps ? Math.max(0, mbps as number) : 0;
  return `${value >= 10 || value === 0 ? Math.round(value) : value.toFixed(1)} MB/s`;
}

/** "2 min ago", "3 h ago", "yesterday". Empty for a missing or unreadable time. */
export function ago(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "";
  const then = Date.parse(iso);
  if (!Number.isFinite(then)) return "";
  const seconds = Math.round((now - then) / 1000);
  if (seconds < 0) return "just now";
  if (seconds < 45) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  return days === 1 ? "yesterday" : `${days} days ago`;
}

/** "in 14 s", "in 3 min", "in 2 h". A time already past reads "due now". */
export function until(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "";
  const then = Date.parse(iso);
  if (!Number.isFinite(then)) return "";
  const seconds = Math.round((then - now) / 1000);
  if (seconds <= 0) return "due now";
  if (seconds < 90) return `in ${seconds} s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `in ${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `in ${hours} h`;
  return `in ${Math.round(hours / 24)} days`;
}

/** Time left on a download: "9 min", "1 h 21 min", or a dash when the client does not say. */
export function formatEta(seconds: number | null | undefined): string {
  if (!seconds || !Number.isFinite(seconds) || seconds <= 0) return "–";
  const minutes = Math.round(seconds / 60);
  if (minutes < 1) return "under 1 min";
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours < 24) return rest ? `${hours} h ${rest} min` : `${hours} h`;
  return `${Math.round(hours / 24)} days`;
}

/** An unfamiliar code becomes a plain phrase: "rate-limited" reads "Rate limited". Never shown raw. */
export function plainWords(code: string | null | undefined, fallback: string): string {
  if (!code) return fallback;
  const words = code.replace(/[._:-]+/g, " ").replace(/([a-z])([A-Z])/g, "$1 $2").trim().toLowerCase();
  if (!words) return fallback;
  return words.charAt(0).toUpperCase() + words.slice(1);
}
