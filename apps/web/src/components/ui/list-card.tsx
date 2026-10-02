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
import { ChevronDown, ChevronRight, ChevronUp, GripVertical, RotateCcw, Search } from "lucide-react";
import { titleCaseLabel } from "../../lib/title-case";
import { COLUMN_EASE, COLUMN_MOVE_MS, prefersReducedMotion, trackColumnDrag } from "../../lib/list-column-drag";
import { isColumnMovable, isColumnSortable, useListColumns, type ListColumnSpec, type ListColumnsState } from "../../lib/use-list-columns";
import { cn } from "../../lib/utils";

/* ---------------------------------------------------------------- card */

interface ListCardProps {
  title: React.ReactNode;
  /** Secondary text after the title. A card whose title would repeat the page or tab leads with its count in `title` instead. */
  count?: React.ReactNode;
  /** Set false when `title` is a count or state phrase ("4 indexers · 3 of 4 healthy", "Up to date") that must keep its own casing. */
  titleCase?: boolean;
  filter?: { value: string; onChange: (value: string) => void; placeholder?: string };
  actions?: React.ReactNode;
  className?: string;
  children: React.ReactNode;
}

export function ListCard({ title, count, titleCase = true, filter, actions, className, children }: ListCardProps) {
  const filterId = React.useId();
  const titleId = React.useId();
  return (
    <section aria-labelledby={titleId} className={cn("overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]", className)}>
      <header className="flex min-h-[var(--list-header-height)] items-center gap-3 border-b border-hairline px-[var(--card-pad-x)]">
        <h2 id={titleId} className="text-[length:var(--type-card-title)] font-semibold leading-none text-foreground">{typeof title === "string" && titleCase ? titleCaseLabel(title) : title}</h2>
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

export interface ListColumn<T = never> extends Omit<ListColumnSpec<T>, "label" | "srOnly"> {
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
  /**
   * A trailing button column. Rows are laid out one grid at a time, so a
   * track sized "auto" is as wide as that row's own button: rows without one
   * collapsed it and their other columns sat further right than the row that
   * had it (System → System tasks, "Check library files"). A fixed track keeps
   * every row's columns in line, with room for a short label at 4K scaling.
   */
  action: "11.5rem",
  toggle: "56px",
  chevron: "40px"
} as const;

interface ListTableContextValue {
  template: string;
  mobileTemplate: string;
  chevron: boolean;
  /** Index each column had in the page's own list, in display order. */
  visual: number[];
  reordered: boolean;
}

const ListTableContext = React.createContext<ListTableContextValue | null>(null);
/** The page's own column index of the cell being rendered; null outside a reordered row. */
const CellIndexContext = React.createContext<number | null>(null);

interface ListTableProps<T> {
  /** The page's columns. Give each an `id`, and a `sort` where sorting makes sense. */
  columns?: Array<ListColumn<T>>;
  /** Names this list in this browser: its column order and sort are remembered under it. */
  listId?: string;
  /** From `useListColumns`, when the page hands its rows over to be sorted. */
  list?: ListColumnsState<T>;
  /** Rows open something on click — reserve the trailing chevron track. */
  chevron?: boolean;
  children: React.ReactNode;
  className?: string;
}

export function ListTable<T = never>({ columns, listId, list, chevron = true, children, className }: ListTableProps<T>) {
  const own = useListColumns<T>(list ? undefined : listId, (columns ?? []) as ListColumnSpec<T>[]);
  const state = list ?? own;
  const enabled = Boolean(state.listId);
  const headerRowRef = React.useRef<HTMLDivElement>(null);
  const bodyRef = React.useRef<HTMLDivElement>(null);
  const flipRef = React.useRef<Record<string, number> | null>(null);
  const [announcement, setAnnouncement] = React.useState("");

  const visual = state.columns.map((entry) => entry.originalIndex);
  const displayOrder = state.columns.map((entry) => entry.id);
  const orderKey = displayOrder.join("|");

  const template = state.columns
    .map(({ column, originalIndex }) => (column as ListColumn<T>).width ?? (originalIndex === 0 ? "minmax(0,1.6fr)" : "minmax(0,1fr)"))
    .concat(chevron ? [LIST_TRACK.chevron] : [])
    .join(" ");
  const mobileTemplate = state.columns
    .map(({ column, originalIndex }) => (originalIndex === 0 ? "minmax(0,1fr)" : (column as ListColumn<T>).mobile ? "auto" : null) as string | null)
    .filter((track): track is string => Boolean(track))
    .concat(chevron ? [LIST_TRACK.chevron] : [])
    .join(" ");
  const reordered = visual.some((value, index) => value !== index);

  const movableById = React.useCallback(
    (id: string) => enabled && Boolean(state.columns.find((entry) => entry.id === id && isColumnMovable(entry.column))),
    [enabled, state.columns]
  );

  const moveByKeyboard = (id: string, delta: -1 | 1) => {
    const movable = state.movableOrder;
    const from = movable.indexOf(id);
    const to = from + delta;
    if (from < 0 || to < 0 || to >= movable.length) return;
    const headerRow = headerRowRef.current;
    if (headerRow) {
      const snapshot: Record<string, number> = {};
      headerRow.querySelectorAll<HTMLElement>('[role="columnheader"][data-col-id]').forEach((header) => {
        snapshot[header.dataset.colId as string] = header.getBoundingClientRect().left;
      });
      flipRef.current = snapshot;
    }
    const next = [...movable];
    next.splice(to, 0, next.splice(from, 1)[0]);
    state.setMovableOrder(next);
    const label = state.columns.find((entry) => entry.id === id)?.column.label;
    setAnnouncement(`${typeof label === "string" ? label : "Column"} moved to position ${to + 1} of ${movable.length}`);
  };

  // Keyboard moves slide the swapped columns into place (FLIP): the new order is already laid out,
  // so each header and its cells start at their old spot and ease to the new one.
  React.useLayoutEffect(() => {
    const snapshot = flipRef.current;
    flipRef.current = null;
    const headerRow = headerRowRef.current;
    const body = bodyRef.current;
    if (!snapshot || !headerRow || !body || prefersReducedMotion()) return;
    const rows = Array.from(body.querySelectorAll<HTMLElement>('[role="row"]'));
    const moving: HTMLElement[] = [];
    headerRow.querySelectorAll<HTMLElement>('[role="columnheader"][data-col-id]').forEach((header) => {
      const id = header.dataset.colId as string;
      const dx = (snapshot[id] ?? 0) - header.getBoundingClientRect().left;
      if (Math.abs(dx) < 1) return;
      const position = displayOrder.indexOf(id);
      const elements = [header, ...rows.map((row) => row.children[position] as HTMLElement | undefined).filter((cell): cell is HTMLElement => Boolean(cell))];
      for (const element of elements) {
        element.style.transition = "none";
        element.style.transform = `translate3d(${dx}px,0,0)`;
      }
      moving.push(...elements);
    });
    if (moving.length === 0) return;
    void headerRow.offsetWidth;
    for (const element of moving) {
      element.style.transition = `transform ${COLUMN_MOVE_MS}ms ${COLUMN_EASE}`;
      element.style.transform = "";
    }
    const timer = setTimeout(() => {
      for (const element of moving) element.style.transition = "";
    }, COLUMN_MOVE_MS + 30);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [orderKey]);

  const startDrag = (event: React.PointerEvent, id: string) => {
    const headerRow = headerRowRef.current;
    const body = bodyRef.current;
    if (!headerRow || !body || !movableById(id)) return;
    trackColumnDrag(event.nativeEvent, id, {
      headerRow,
      body,
      order: displayOrder,
      isMovable: movableById,
      commit: (order) => state.setMovableOrder(order.filter(movableById))
    });
  };

  return (
    <>
      <div role="table" className={className}>
        <div
          ref={headerRowRef}
          role="row"
          style={{ "--list-cols": template, "--list-cols-mobile": mobileTemplate } as React.CSSProperties}
          className="relative grid h-[var(--list-thead-height)] items-center gap-[var(--grid-gap)] border-b border-hairline bg-surface-2/40 px-[var(--card-pad-x)] [grid-template-columns:var(--list-cols-mobile)] md:[grid-template-columns:var(--list-cols)]"
        >
          {state.columns.map(({ column, id, originalIndex }) => {
            const col = column as ListColumn<T>;
            const movable = movableById(id);
            const sortable = enabled && isColumnSortable(col);
            const active = state.sort?.id === id ? state.sort : null;
            return (
              <div
                key={id}
                role="columnheader"
                data-col-id={id}
                aria-sort={sortable ? (active ? (active.direction === "asc" ? "ascending" : "descending") : "none") : undefined}
                tabIndex={movable && !sortable ? 0 : undefined}
                aria-keyshortcuts={movable ? "Alt+ArrowLeft Alt+ArrowRight" : undefined}
                title={movable ? "Drag to move this column. Alt with the left or right arrow also moves it." : undefined}
                onPointerDown={movable ? (event) => startDrag(event, id) : undefined}
                onKeyDown={
                  movable
                    ? (event) => {
                        if (event.altKey && (event.key === "ArrowLeft" || event.key === "ArrowRight")) {
                          event.preventDefault();
                          moveByKeyboard(id, event.key === "ArrowLeft" ? -1 : 1);
                        }
                      }
                    : undefined
                }
                className={cn(
                  "group relative min-w-0 truncate rounded-sm text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  movable && "cursor-grab touch-pan-y select-none",
                  col.align === "end" && "text-right",
                  originalIndex !== 0 && !col.mobile && "hidden md:block",
                  col.srOnly && "sr-only"
                )}
              >
                {movable ? (
                  <GripVertical aria-hidden className="pointer-events-none absolute -left-2.5 top-1/2 h-3 w-3 -translate-y-1/2 opacity-0 transition-opacity duration-150 group-hover:opacity-50" />
                ) : null}
                {sortable ? (
                  <button
                    type="button"
                    onClick={() => state.toggleSort(id)}
                    className={cn("inline-flex max-w-full cursor-[inherit] items-center gap-1 truncate uppercase tracking-[0.1em] hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring", active && "text-foreground")}
                  >
                    <span className="truncate">{col.label}</span>
                    {active ? active.direction === "asc" ? <ChevronUp aria-hidden className="h-3 w-3 shrink-0" /> : <ChevronDown aria-hidden className="h-3 w-3 shrink-0" /> : null}
                  </button>
                ) : (
                  col.label
                )}
              </div>
            );
          })}
          {chevron ? <span aria-hidden /> : null}
          {enabled && state.changed ? (
            <button
              type="button"
              aria-label="Reset columns"
              title="Reset columns"
              onClick={state.reset}
              className="absolute right-1.5 top-1/2 grid h-6 w-6 -translate-y-1/2 place-items-center rounded-md text-muted-foreground transition-colors hover:bg-surface-2 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <RotateCcw aria-hidden className="h-3.5 w-3.5" />
            </button>
          ) : null}
        </div>
        <ListTableContext.Provider value={{ template, mobileTemplate, chevron, visual, reordered }}>
          <div ref={bodyRef} role="rowgroup">{children}</div>
        </ListTableContext.Provider>
      </div>
      <span className="sr-only" role="status" aria-live="polite">{announcement}</span>
    </>
  );
}

/* ----------------------------------------------------------------- row */

interface ListRowProps extends Omit<React.HTMLAttributes<HTMLDivElement>, "onClick"> {
  onClick?: () => void;
  selected?: boolean;
  children: React.ReactNode;
}

/** Cells in the order the person chose. Only reordered when the cell count matches the columns; otherwise left as the page wrote them. */
function orderedCells(children: React.ReactNode, visual: number[]): React.ReactNode {
  const flat: React.ReactNode[] = [];
  const push = (nodes: React.ReactNode) => {
    React.Children.toArray(nodes).forEach((node) => {
      if (React.isValidElement(node) && node.type === React.Fragment) push((node.props as { children?: React.ReactNode }).children);
      else flat.push(node);
    });
  };
  push(children);
  if (flat.length !== visual.length) return children;
  return visual.map((originalIndex) => (
    <CellIndexContext.Provider key={originalIndex} value={originalIndex}>
      {flat[originalIndex]}
    </CellIndexContext.Provider>
  ));
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
      {table.reordered ? orderedCells(children, table.visual) : children}
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
  /** Set the primary line in the code face — for paths, IDs and keys. The secondary line stays in the UI face; prose is never monospace. */
  mono?: boolean;
  numeric?: boolean;
  className?: string;
  children?: React.ReactNode;
}

export function ListCell({ primary, secondary, mobile = false, align = "start", mono, numeric, className, children }: ListCellProps) {
  const originalIndex = React.useContext(CellIndexContext);
  return (
    <div
      role="cell"
      className={cn(
        "min-w-0 text-[length:var(--list-row-text)] leading-tight",
        // The page's first column always shows on a phone, wherever the person has moved it to.
        !mobile && (originalIndex === null ? "hidden md:block first:block" : originalIndex === 0 ? "" : "hidden md:block"),
        align === "end" && "text-right",
        numeric && "tabular-nums",
        className
      )}
    >
      {children ?? (
        <>
          {primary !== undefined ? (
            <span className={cn("block truncate text-foreground", mono && "font-mono text-[length:var(--type-caption)]")}>{primary}</span>
          ) : null}
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
      <span className="block truncate text-[length:var(--list-row-text)] font-semibold text-foreground">{name}</span>
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
