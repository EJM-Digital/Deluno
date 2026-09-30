/**
 * The few building blocks every Dashboard tab shares (#1082): a panel that
 * keeps to its own height and scrolls inside itself, and a label/value line.
 * Nothing here holds data; each tab says what is true and this only lays it out.
 */
import type { ReactNode } from "react";
import { ArrowRight } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "../../components/ui/button";
import { ListCard } from "../../components/ui/list-card";
import { cn } from "../../lib/utils";

/**
 * A ListCard that never grows past the space its grid cell gives it: the body
 * scrolls inside the card, so a busy panel cannot push the page off the screen.
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
      <div className={cn("min-h-0 flex-1 overflow-y-auto", bodyClassName)}>{children}</div>
    </ListCard>
  );
}

/** One label on the left, its value on the right; a link when it has somewhere to go. */
export function Line({
  label,
  value,
  to,
  tone,
  hint
}: {
  label: ReactNode;
  value?: ReactNode;
  to?: string;
  tone?: "warn" | "bad" | "ok";
  hint?: ReactNode;
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
    <Link to={to} className={cn(className, "transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring")}>
      {body}
    </Link>
  ) : (
    <div className={className}>{body}</div>
  );
}

/** A sentence that says something true where a list has nothing to list. */
export function Plain({ children }: { children: ReactNode }) {
  return <p className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-body-sm)] text-muted-foreground">{children}</p>;
}

/** A small caption above a group of lines. */
export function Group({ children }: { children: ReactNode }) {
  return (
    <p className="px-[var(--card-pad-x)] pb-1 pt-2.5 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
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
