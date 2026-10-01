/**
 * The card every part of Live sits in (#1098): a title, a quiet line of what it
 * holds that gives way (truncates) before anything else when the card is narrow, a
 * link to where it is managed, and a body that takes the rest of the height. It looks
 * like the app's list cards, but its header never wraps onto a second line.
 */
import { ArrowRight } from "lucide-react";
import { useId, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { Button } from "../../../components/ui/button";
import { cn } from "../../../lib/utils";

export function LiveCard({ title, count, to, toLabel, iconOnly = false, bare = false, className, bodyClassName, children }: {
  title: string;
  count?: ReactNode;
  to?: string;
  toLabel?: string;
  /** The link is an arrow alone (its name is still said), so a long count line keeps the room. */
  iconOnly?: boolean;
  /** No rule under the header, as in the tile cards of the right column. */
  bare?: boolean;
  className?: string;
  bodyClassName?: string;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <section aria-labelledby={id} className={cn("flex min-h-0 min-w-0 flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]", className)}>
      <header className={cn("flex min-h-[var(--list-header-height)] shrink-0 items-center gap-3 px-[var(--card-pad-x)]", !bare && "border-b border-hairline")}>
        <h2 id={id} className="shrink-0 text-[length:var(--type-card-title)] font-semibold leading-none text-foreground">{title}</h2>
        <span title={typeof count === "string" ? count : undefined} className="min-w-0 flex-1 truncate text-[length:var(--type-caption)] text-muted-foreground">{count}</span>
        {to ? (
          <Button asChild variant="ghost" size="sm" className="-mr-2 shrink-0 text-primary">
            <Link to={to} aria-label={`${toLabel ?? "Open"}: ${title}`}>
              {iconOnly ? null : toLabel ?? "Open"}
              <ArrowRight aria-hidden className="h-3.5 w-3.5" />
            </Link>
          </Button>
        ) : null}
      </header>
      <div className={cn("flex min-h-0 flex-1 flex-col overflow-hidden", bodyClassName)}>{children}</div>
    </section>
  );
}
