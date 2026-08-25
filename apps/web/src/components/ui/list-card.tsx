/**
 * ListCard — the one container for a collection page.
 *
 *   ┌ header (48px): title · count · [filter] · [actions] ┐
 *   ├ column labels (36px, uppercase 11px)                 ┤
 *   ├ row (56px): Name+sub │ d1 │ d2 │ d3 │ Status │ On │ › ┤
 *   └ … or one empty state inside the card                 ┘
 *
 * Rows are the same height everywhere; column widths come from `ListTable`'s
 * `columns` so every page lines up. Clicking a row opens its Drawer.
 * On narrow screens only the first cell and cells marked `mobile` remain.
 */
import * as React from "react";
import { ChevronRight, Search } from "lucide-react";
import { titleCaseLabel } from "../../lib/title-case";
import { cn } from "../../lib/utils";

/* ---------------------------------------------------------------- card */

interface ListCardProps {
  title: React.ReactNode;
  /** e.g. "5 plans · 4 enabled" */
  count?: React.ReactNode;
  filter?: { value: string; onChange: (value: string) => void; placeholder?: string };
  actions?: React.ReactNode;
  className?: string;
  children: React.ReactNode;
}

export function ListCard({ title, count, filter, actions, className, children }: ListCardProps) {
  const filterId = React.useId();
  return (
    <section className={cn("overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]", className)}>
      <header className="flex min-h-[var(--list-header-height)] items-center gap-3 border-b border-hairline px-[var(--card-pad-x)]">
        <h2 className="text-[length:var(--type-card-title)] font-semibold leading-none text-foreground">{typeof title === "string" ? titleCaseLabel(title) : title}</h2>
        {count ? <span className="text-[length:var(--type-caption)] text-muted-foreground">{count}</span> : null}
        <span className="flex-1" />
        {filter ? (
          <label htmlFor={filterId} className="relative hidden sm:block">
            <Search aria-hidden className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <input
              id={filterId}
              type="search"
              value={filter.value}
              onChange={(event) => filter.onChange(event.target.value)}
              placeholder={filter.placeholder ?? "Filter"}
              className="h-[var(--control-height-sm)] w-56 rounded-[8px] border border-hairline bg-surface-1 pl-8 pr-2 text-[length:var(--type-caption)] text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
            <span className="sr-only">{filter.placeholder ?? "Filter"}</span>
          </label>
        ) : null}
        {actions}
      </header>
      {children}
    </section>
  );
}

/* --------------------------------------------------------------- table */

export interface ListColumn {
  label: React.ReactNode;
  /** CSS grid track. Defaults: first column `minmax(0,1.6fr)`, others `minmax(0,1fr)`. */
  width?: string;
  /** Right-align (numbers). */
  align?: "start" | "end";
  /** Keep visible on narrow screens. */
  mobile?: boolean;
  /** Visually hidden header text (e.g. for the switch column when obvious). */
  srOnly?: boolean;
}

/** Fixed tracks so Status / On / › land in the same place on every page. */
export const LIST_TRACK = {
  status: "150px",
  toggle: "56px",
  chevron: "40px"
} as const;

const ListTableContext = React.createContext<{ template: string; mobileTemplate: string; chevron: boolean; columns: ListColumn[] } | null>(null);

interface ListTableProps {
  columns: ListColumn[];
  /** Rows open something on click — reserve the trailing chevron track. */
  chevron?: boolean;
  children: React.ReactNode;
  className?: string;
}

export function ListTable({ columns, chevron = true, children, className }: ListTableProps) {
  const template = React.useMemo(() => {
    const tracks = columns.map((column, index) => column.width ?? (index === 0 ? "minmax(0,1.6fr)" : "minmax(0,1fr)"));
    if (chevron) tracks.push(LIST_TRACK.chevron);
    return tracks.join(" ");
  }, [columns, chevron]);
  const mobileTemplate = React.useMemo(() => {
    const tracks = columns.map((column, index) => (index === 0 ? "minmax(0,1fr)" : column.mobile ? "auto" : null)).filter(Boolean) as string[];
    if (chevron) tracks.push(LIST_TRACK.chevron);
    return tracks.join(" ");
  }, [columns, chevron]);

  return (
    <ListTableContext.Provider value={{ template, mobileTemplate, chevron, columns }}>
      <div role="table" className={className}>
        <div
          role="row"
          style={{ "--list-cols": template, "--list-cols-mobile": mobileTemplate } as React.CSSProperties}
          className="grid h-[var(--list-thead-height)] items-center gap-[var(--grid-gap)] border-b border-hairline bg-surface-2/40 px-[var(--card-pad-x)] [grid-template-columns:var(--list-cols-mobile)] md:[grid-template-columns:var(--list-cols)]"
        >
          {columns.map((column, index) => (
            <span
              key={index}
              role="columnheader"
              className={cn(
                "truncate text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground",
                column.align === "end" && "text-right",
                index !== 0 && !column.mobile && "hidden md:block",
                column.srOnly && "sr-only"
              )}
            >
              {column.label}
            </span>
          ))}
          {chevron ? <span aria-hidden /> : null}
        </div>
        <div role="rowgroup">{children}</div>
      </div>
    </ListTableContext.Provider>
  );
}

/* ----------------------------------------------------------------- row */

interface ListRowProps extends Omit<React.HTMLAttributes<HTMLDivElement>, "onClick"> {
  onClick?: () => void;
  selected?: boolean;
  children: React.ReactNode;
}

export function ListRow({ onClick, selected = false, className, children, ...props }: ListRowProps) {
  const table = React.useContext(ListTableContext);
  if (!table) throw new Error("ListRow must be rendered inside ListTable");
  const interactive = Boolean(onClick);

  return (
    <div
      role="row"
      tabIndex={interactive ? 0 : undefined}
      aria-selected={interactive ? selected : undefined}
      onClick={onClick}
      onKeyDown={(event) => {
        if (!interactive) return;
        if (event.key === "Enter" || event.key === " ") {
          if (event.target !== event.currentTarget) return;
          event.preventDefault();
          onClick?.();
        }
      }}
      style={{ "--list-cols": table.template, "--list-cols-mobile": table.mobileTemplate } as React.CSSProperties}
      className={cn(
        "grid min-h-[var(--list-row-height)] items-center gap-[var(--grid-gap)] border-b border-hairline px-[var(--card-pad-x)] last:border-b-0",
        "[grid-template-columns:var(--list-cols-mobile)] md:[grid-template-columns:var(--list-cols)]",
        interactive && "cursor-pointer transition-colors hover:bg-surface-2/60 focus-visible:outline-none focus-visible:bg-surface-2/60 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
        selected && "bg-primary/[0.08] hover:bg-primary/[0.1]",
        className
      )}
      {...props}
    >
      {children}
      {table.chevron ? (
        <span aria-hidden className="flex justify-end text-muted-foreground/50">
          {interactive ? <ChevronRight className="h-4 w-4" /> : null}
        </span>
      ) : null}
    </div>
  );
}

/* ---------------------------------------------------------------- cell */

interface ListCellProps {
  /** Main line. Bold in the first column. */
  primary?: React.ReactNode;
  /** Muted second line, truncated. */
  secondary?: React.ReactNode;
  /** Keep on narrow screens (first cell always is). */
  mobile?: boolean;
  align?: "start" | "end";
  mono?: boolean;
  numeric?: boolean;
  className?: string;
  children?: React.ReactNode;
}

export function ListCell({ primary, secondary, mobile = false, align = "start", mono, numeric, className, children }: ListCellProps) {
  return (
    <div
      role="cell"
      className={cn(
        "min-w-0 text-[length:var(--type-body-sm)] leading-tight",
        !mobile && "hidden md:block first:block",
        align === "end" && "text-right",
        numeric && "tabular-nums",
        mono && "font-mono text-[length:var(--type-caption)]",
        className
      )}
    >
      {children ?? (
        <>
          {primary !== undefined ? <span className="block truncate text-foreground">{primary}</span> : null}
          {secondary !== undefined ? (
            <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground">{secondary}</span>
          ) : null}
        </>
      )}
    </div>
  );
}

/** First-column cell: bold name + muted subline, always visible. */
export function ListNameCell({ name, sub, className }: { name: React.ReactNode; sub?: React.ReactNode; className?: string }) {
  return (
    <div role="cell" className={cn("min-w-0 leading-tight", className)}>
      <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{name}</span>
      {sub !== undefined ? <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground">{sub}</span> : null}
    </div>
  );
}

/* --------------------------------------------------------------- empty */

export function ListEmpty({
  title,
  description,
  actions
}: {
  title: React.ReactNode;
  description?: React.ReactNode;
  actions?: React.ReactNode;
}) {
  return (
    <div className="flex min-h-[112px] flex-col items-center justify-center gap-1.5 px-[var(--card-pad-x)] py-[var(--card-pad-y)] text-center">
      <p className="text-[length:var(--type-body-sm)] font-semibold text-foreground">{title}</p>
      {description ? <p className="max-w-[52ch] text-[length:var(--type-caption)] text-muted-foreground">{description}</p> : null}
      {actions ? <div className="mt-1.5 flex flex-wrap items-center justify-center gap-2">{actions}</div> : null}
    </div>
  );
}
