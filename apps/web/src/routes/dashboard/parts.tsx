/**
 * The few building blocks every Dashboard tab shares (#1082): a panel that
 * keeps to its own height, a list that shows only the whole rows that fit, and
 * a label/value line.
 * Nothing here holds data; each tab says what is true and this only lays it out.
 */
import { cloneElement, useCallback, useLayoutEffect, useRef, useState, type ReactElement, type ReactNode } from "react";
import { ArrowRight } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "../../components/ui/button";
import { ListCard } from "../../components/ui/list-card";
import { cn } from "../../lib/utils";

/**
 * A ListCard that never grows past the space its grid cell gives it, so a busy
 * panel cannot push the page off the screen. The body clips, and never cuts a
 * row in half because a list panel puts its rows in a `FitList`, which shows
 * the whole rows that fit and then "and N more" with a link to the rest.
 * `to` puts the one "open the screen where you act on this" link in the header.
 */
export function Panel({
  title,
  count,
  to,
  toLabel,
  className,
  bodyClassName,
  children
}: {
  title: string;
  count?: ReactNode;
  to?: string;
  toLabel?: string;
  className?: string;
  bodyClassName?: string;
  children: ReactNode;
}) {
  return (
    <ListCard
      title={title}
      count={count}
      className={cn("flex min-h-0 flex-col", className)}
      actions={
        to ? (
          <Button asChild variant="ghost" size="sm" className="-mr-2 text-primary">
            <Link to={to} aria-label={`${toLabel ?? "Open"}: ${title}`}>
              {toLabel ?? "Open"}
              <ArrowRight aria-hidden className="h-3.5 w-3.5" />
            </Link>
          </Button>
        ) : undefined
      }
    >
      <div className={cn("flex min-h-0 flex-1 flex-col overflow-hidden", bodyClassName)}>{children}</div>
    </ListCard>
  );
}

/** One label on the left, its value on the right; a link when it has somewhere to go. */
export function Line({
  label,
  value,
  to,
  tone,
  hint,
  ...rest
}: {
  label: ReactNode;
  value?: ReactNode;
  to?: string;
  tone?: "warn" | "bad" | "ok";
  hint?: ReactNode;
  /** `data-*` marks a row a FitList measures. */
  "data-fit-row"?: string;
}) {
  const body = (
    <>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[length:var(--type-body-sm)] text-foreground">{label}</span>
        {hint ? <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{hint}</span> : null}
      </span>
      {value !== undefined ? (
        <span
          className={cn(
            "shrink-0 text-right text-[length:var(--type-body-sm)] font-medium tabular-nums",
            tone === "warn" ? "text-warning" : tone === "bad" ? "text-destructive" : tone === "ok" ? "text-success" : "text-foreground"
          )}
        >
          {value}
        </span>
      ) : null}
    </>
  );
  const className = "flex min-h-8 items-center gap-3 border-b border-hairline px-[var(--card-pad-x)] py-1.5 last:border-b-0";
  return to ? (
    <Link to={to} {...rest} className={cn(className, "transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring")}>
      {body}
    </Link>
  ) : (
    <div {...rest} className={className}>{body}</div>
  );
}

/** A sentence that says something true where a list has nothing to list. */
export function Plain({ children }: { children: ReactNode }) {
  return <p className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-body-sm)] text-muted-foreground">{children}</p>;
}

/** A small caption above a group of lines. */
export function Group({ children, ...rest }: { children: ReactNode; "data-fit-row"?: string }) {
  return (
    <p {...rest} className="px-[var(--card-pad-x)] pb-1 pt-2.5 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
      {children}
    </p>
  );
}

/** A thin proportion bar, for counts against a whole. */
export function Bar({ fraction, tone = "primary" }: { fraction: number; tone?: "primary" | "success" | "warning" | "danger" }) {
  const fill = { primary: "bg-primary", success: "bg-success", warning: "bg-warning", danger: "bg-destructive" }[tone];
  return (
    <span aria-hidden className="block h-1.5 w-full overflow-hidden rounded-full bg-surface-3">
      <span className={cn("block h-full rounded-full", fill)} style={{ width: `${Math.min(100, Math.max(0, fraction * 100))}%` }} />
    </span>
  );
}

/** Height kept for the "and N more" line when not every row fits. */
const MORE_LINE_PX = 36;

/**
 * Says how many of a list's rows fit whole. `bottoms` are each row's bottom edge
 * measured from the top of the box that holds them, `boxHeight` that box's height.
 * A box with no height (nothing laid out yet, or a test with no layout) shows every row.
 *
 * A row marked as a heading is never left as the last thing shown, because a
 * caption with nothing under it is the same cut-off row in other clothes.
 *
 * `moreBeyond` says the list has rows that were never handed to it (the server
 * sent only the newest), so the "and N more" line is shown even when every row
 * it has fits, and its height is kept.
 */
export function rowsThatFit(
  bottoms: number[],
  boxHeight: number,
  moreLine = MORE_LINE_PX,
  isHeading: (index: number) => boolean = () => false,
  moreBeyond = false
): number {
  if (boxHeight <= 0 || bottoms.length === 0) return bottoms.length;
  if (!moreBeyond && bottoms[bottoms.length - 1] <= boxHeight) return bottoms.length;
  let count = 0;
  while (count < bottoms.length && bottoms[count] <= boxHeight - moreLine) count += 1;
  while (count > 0 && isHeading(count - 1)) count -= 1;
  return count;
}

/**
 * The one list every Dashboard panel uses (#1082): it shows only WHOLE rows that
 * fit the space it is given, then a link "and N more" to where the rest are.
 * A row is never cut in half and nothing is hidden without saying so.
 *
 * `rows` are keyed elements (an `li`, a `ListRow`): each is marked so its height
 * can be measured. `wrap` puts the shown rows inside a table or list, after
 * which anything above the rows (a table header) counts against the space.
 * `extra` is how many more exist that were never given to the list (the server
 * sent only the newest), so "and N more" counts them too.
 */
export function FitList({
  rows,
  wrap = (shown) => <ul>{shown}</ul>,
  moreTo,
  moreLabel = (hidden) => `and ${hidden.toLocaleString("en-AU")} more`,
  extra = 0,
  isHeading,
  fill = false
}: {
  rows: ReactElement[];
  wrap?: (shown: ReactElement[]) => ReactNode;
  /** Where the rest are; a function is given the rows that were left out. */
  moreTo: string | ((hidden: ReactElement[]) => string);
  moreLabel?: (hidden: number) => string;
  extra?: number;
  isHeading?: (index: number) => boolean;
  /** Shares the room left over after the whole rows between them, so the list fills its panel. Rows that stretch must lay their content out to fit. */
  fill?: boolean;
}) {
  const boxRef = useRef<HTMLDivElement>(null);
  const [fit, setFit] = useState<number | null>(null);
  const signature = rows.map((row) => String(row.key)).join("|");

  const measure = useCallback(() => {
    const box = boxRef.current;
    if (!box) return;
    const boxTop = box.getBoundingClientRect().top;
    const marked = Array.from(box.querySelectorAll<HTMLElement>("[data-fit-row]"));
    // A row that could not be measured is never hidden: show everything rather than guess.
    if (marked.length !== rows.length) {
      setFit(rows.length);
      return;
    }
    const bottoms = marked.map((element) => element.getBoundingClientRect().bottom - boxTop);
    setFit(rowsThatFit(bottoms, box.getBoundingClientRect().height, MORE_LINE_PX, isHeading, extra > 0));
  }, [rows.length, isHeading, extra]);

  // Whenever the rows change, show them all again and measure before anything is painted.
  useLayoutEffect(() => setFit(null), [signature, extra]);
  useLayoutEffect(() => {
    if (fit === null) measure();
  });
  // The panel growing or shrinking (the window, a tab switch) is measured again.
  useLayoutEffect(() => {
    const box = boxRef.current;
    if (!box || typeof ResizeObserver === "undefined") return undefined;
    let last = box.getBoundingClientRect().height;
    const observer = new ResizeObserver(() => {
      const height = box.getBoundingClientRect().height;
      if (Math.abs(height - last) < 1) return;
      last = height;
      setFit(null);
    });
    observer.observe(box);
    return () => observer.disconnect();
  }, []);

  const shownCount = fit === null ? rows.length : Math.min(fit, rows.length);
  const shown = rows.slice(0, shownCount).map((row) => cloneElement(row, { "data-fit-row": "" } as Record<string, string>));
  const hidden = rows.slice(shownCount);
  const hiddenCount = hidden.length + extra;
  const to = hiddenCount === 0 ? "" : typeof moreTo === "function" ? moreTo(hidden) : moreTo;

  return (
    <div ref={boxRef} className="relative flex min-h-0 flex-1 flex-col overflow-hidden">
      <div className={cn(fit === null && "invisible", fit !== null && fill && "flex min-h-0 flex-1 flex-col [&>ul]:flex [&>ul]:min-h-0 [&>ul]:flex-1 [&>ul]:flex-col [&>ul>li]:min-h-0 [&>ul>li]:flex-1")}>{shown.length > 0 ? wrap(shown) : null}</div>
      {hiddenCount > 0 && fit !== null ? (
        <Link
          to={to}
          className="flex h-9 shrink-0 items-center gap-1 border-t border-hairline px-[var(--card-pad-x)] text-[length:var(--type-caption)] font-medium text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
        >
          {moreLabel(hiddenCount)}
          <ArrowRight aria-hidden className="h-3 w-3" />
        </Link>
      ) : null}
    </div>
  );
}
