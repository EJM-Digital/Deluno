/**
 * The card every part of Live sits in (#1098): a title, a quiet line of what it
 * holds that gives way (truncates) before anything else when the card is narrow, a
 * link to where it is managed, and a body that takes the rest of the height. It is the
 * approved mockup's card (14px corners, its header below), and its header never wraps.
 */
import { useId, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";

export function LiveCard({ title, count, controls, description, aside, to, toLabel, iconOnly = false, className, bodyClassName, children }: {
  title: string;
  count?: ReactNode;
  /** Controls that take the count's place between the title and the link, such as the shelf's chips (Weir's `controls`): they scroll sideways rather than wrap, and the header stays 44px. */
  controls?: ReactNode;
  /** What the card adds up to, for a screen reader only: the card's accessible description (the count's words when `controls` stand in its place). */
  description?: string;
  /** A small extra link at the right, before the card's own link (Activity's "N more", as Weir's). */
  aside?: ReactNode;
  to?: string;
  toLabel?: string;
  /** The link is an arrow alone (its name is still said), so a long count line keeps the room. */
  iconOnly?: boolean;
  className?: string;
  bodyClassName?: string;
  children: ReactNode;
}) {
  const id = useId();
  const descriptionId = `${id}-description`;
  return (
    <section aria-labelledby={id} aria-describedby={description ? descriptionId : undefined} className={cn("flex min-h-0 min-w-0 flex-col overflow-hidden rounded-[14px] border border-hairline bg-card shadow-card dark:border-white/[0.07]", className)}>
      {/* The mockup's card header, rule for rule: 14/18/10 padding, baseline-aligned, a 15px semibold title on a 15px line, 12px between title and count, the count and link on Weir's 1.52 lines (Weir's header, 2 Oct 2026),
          the link 13px at the right with its arrow. No rule under it. */}
      <header className="flex min-h-[44px] shrink-0 items-baseline gap-[12px] whitespace-nowrap px-[18px] pb-[10px] pt-[14px]">
        <h2 id={id} className="m-0 shrink-0 text-[length:var(--type-card-title)] font-semibold leading-[15px] text-foreground">{title}</h2>
        {controls ? (
          <div className="flex min-w-0 flex-1 self-center -m-2">{controls}</div>
        ) : (
          <span title={typeof count === "string" ? count : undefined} className="min-w-0 flex-1 truncate text-[length:var(--type-caption)] leading-[1.52] text-muted-foreground">{count}</span>
        )}
        {aside}
        {to ? (
          <Link to={to} aria-label={`${toLabel ?? "Open"}: ${title}`} className={cn("ml-auto shrink-0 rounded-sm text-[length:var(--button-font-size-sm)] font-medium leading-[17px] text-primary hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[hsl(var(--ring))]", iconOnly && "self-center")}>
            {iconOnly ? null : `${toLabel ?? "Open"} `}
            <span aria-hidden>→</span>
          </Link>
        ) : null}
      </header>
      {description ? <p id={descriptionId} className="sr-only">{description}</p> : null}
      <div className={cn("flex min-h-0 flex-1 flex-col overflow-hidden", bodyClassName)}>{children}</div>
    </section>
  );
}
