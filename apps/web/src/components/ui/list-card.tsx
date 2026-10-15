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
import { COLUMN_EASE, COLUMN_MOVE_MS, prefersReducedMotion, trackColumnDrag } from "../../lib/list-column-drag";
import { isColumnMovable, isColumnSortable, useListColumns, type ListColumnSpec, type ListColumnsState } from "../../lib/use-list-columns";
import { cn } from "../../lib/utils";
import { ColumnsAnnouncements, ColumnsButton, useSharedColumnsHint, type ColumnsControls } from "./columns-menu";
import { plainTitle } from "../../lib/plain-title";

/** A ListTable inside a ListCard hands its Columns controls up to the card, which draws the button last in its header. */
interface ListCardContextValue {
  register: (id: string, controls: ColumnsControls | null) => void;
}
const ListCardContext = React.createContext<ListCardContextValue | null>(null);

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
  const [tables, setTables] = React.useState<Record<string, ColumnsControls>>({});
  const register = React.useCallback((id: string, controls: ColumnsControls | null) => {
    setTables((current) => {
      if (!controls) {
        if (!(id in current)) return current;
        const rest = { ...current };
        delete rest[id];
        return rest;
      }
      return { ...current, [id]: controls };
    });
  }, []);
  const registered = Object.values(tables);
  const contextValue = React.useMemo(() => ({ register }), [register]);
  return (
    <section aria-labelledby={titleId} className={cn("overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]", className)}>
      <header className="flex min-h-(--list-header-height) flex-wrap items-center gap-x-3 gap-y-2 border-b border-hairline px-(--card-pad-x) py-2">
        <h2 id={titleId} className="text-(length:--type-card-title) font-semibold leading-none text-foreground">{typeof title === "string" && titleCase ? titleCaseLabel(title) : title}</h2>
        {count ? <span className="text-(length:--type-caption) text-muted-foreground">{count}</span> : null}
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
              className="h-(--control-height-sm) w-56 rounded-[8px] border border-hairline bg-surface-1 pl-8 pr-2 text-(length:--type-caption) text-foreground placeholder:text-muted-foreground focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring"
            />
            <span className="sr-only">{filter.placeholder ?? "Filter"}</span>
          </label>
        ) : null}
        {actions}
        {registered.length ? (
          <>
            <ColumnsButton onReset={() => registered.forEach((entry) => entry.reset())} />
            <ColumnsAnnouncements controls={registered} />
          </>
        ) : null}
      </header>
      <ListCardContext.Provider value={contextValue}>{children}</ListCardContext.Provider>
    </section>
  );
}

/* --------------------------------------------------------------- table */

const ARROW_UP = "M5 8.5v-6M2.5 5 5 2.5 7.5 5";
const ARROW_DOWN = "M5 1.5v6M2.5 5 5 7.5 7.5 5";

/** The thin line arrow beside the sorted column's name, the same as Weir's. */
function SortArrow({ direction }: { direction: "asc" | "desc" }) {
  return (
    <svg viewBox="0 0 10 10" width="10" height="10" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden className="shrink-0">
      <path d={direction === "asc" ? ARROW_UP : ARROW_DOWN} />
    </svg>
  );
}

/** A column heading wider than its column wraps to a second line before it is cut, and says all of itself as a tooltip (#1168). */
const HEADING_LABEL = "line-clamp-2 min-w-0 [overflow-wrap:anywhere] leading-[1.15]";

export interface ListColumn<T = never> extends Omit<ListColumnSpec<T>, "label" | "srOnly"> {
  label: React.ReactNode;
  /** CSS grid track. Defaults: first column `minmax(0,1.6fr)`, others `minmax(0,1fr)`. */
  width?: string;
  /** Right-align (numbers). */
  align?: "start" | "end";
  /** Keep visible on narrow screens. */
  mobile?: boolean;
  /**
   * The track on a phone. Each row is its own grid, so an "auto" track is as wide as that row's own chip or button
   * and the rows stop lining up; a fixed width keeps them in line where that matters (#1130).
   */
  mobileWidth?: string;
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
  /** A phone lays each row out as a wrapping line instead of a grid: see MOBILE_FLOW. */
  flow: boolean;
}

const ListTableContext = React.createContext<ListTableContextValue | null>(null);
/** The page's own column index of the cell being rendered; null outside a reordered row. */
const CellIndexContext = React.createContext<number | null>(null);

interface ListTableProps<T> {
  /** The page's columns. Give each an `id`, and a `sort` where sorting makes sense. */
  columns?: Array<ListColumn<T>>;
  /** Names this list in this browser: its column order and sort are remembered under it. */
  listId?: string;
  /** The page's rows, when the table should sort them: pass them here and render them from the function form of `children`. */
  items?: readonly T[];
  /** From `useListColumns`, for a page that needs the sort itself (a list that loads a page at a time sorts on the server). */
  list?: ListColumnsState<T>;
  /** Rows open something on click — reserve the trailing chevron track. */
  chevron?: boolean;
  children: React.ReactNode | ((items: T[]) => React.ReactNode);
  className?: string;
}

export function ListTable<T = never>({ columns, listId, items, list, chevron = true, children, className }: ListTableProps<T>) {
  const own = useListColumns<T>(list ? undefined : listId, (columns ?? []) as ListColumnSpec<T>[], items);
  const state = list ?? own;
  const enabled = Boolean(state.listId);
  const headerRowRef = React.useRef<HTMLDivElement>(null);
  const bodyRef = React.useRef<HTMLDivElement>(null);
  const flipRef = React.useRef<Record<string, number> | null>(null);
  const [announcement, setAnnouncement] = React.useState("");
  const card = React.useContext(ListCardContext);
  const hintId = React.useId();
  const describedBy = useSharedColumnsHint(enabled && state.columns.some((entry) => isColumnSortable(entry.column)));
  const hasControls = enabled && state.columns.some((entry) => isColumnMovable(entry.column));
  const sortableTable = enabled && state.columns.some((entry) => isColumnSortable(entry.column));
  const controls: ColumnsControls = { hintId, sortable: sortableTable, announcement, reset: state.reset };
  const register = card?.register;
  const controlsKey = `${hasControls}|${sortableTable}|${announcement}`;
  React.useEffect(() => {
    if (!register || !hasControls) return;
    register(hintId, controls);
    return () => register(hintId, null);
    // `controls` is new every render; what matters is its key and the stable reset.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [register, hintId, controlsKey, state.reset]);

  const visual = state.columns.map((entry) => entry.originalIndex);
  const displayOrder = state.columns.map((entry) => entry.id);
  const orderKey = displayOrder.join("|");

  const template = state.columns
    .map(({ column, originalIndex }) => (column as ListColumn<T>).width ?? (originalIndex === 0 ? "minmax(0,1.6fr)" : "minmax(0,1fr)"))
    .concat(chevron ? [LIST_TRACK.chevron] : [])
    .join(" ");
  const mobileTemplate = state.columns
    .map(({ column, originalIndex }) => (originalIndex === 0 ? "minmax(0,1fr)" : (column as ListColumn<T>).mobile ? ((column as ListColumn<T>).mobileWidth ?? "auto") : null) as string | null)
    .filter((track): track is string => Boolean(track))
    .concat(chevron ? [LIST_TRACK.chevron] : [])
    .join(" ");
  const reordered = visual.some((value, index) => value !== index);
  // A column that asks for a fixed phone track keeps the grid so its rows line up; every other table flows (C3 to C9, #1168).
  const flow = !state.columns.some(({ column }) => Boolean((column as ListColumn<T>).mobileWidth));

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
    const rows = Array.from(body.querySelectorAll<HTMLElement>('[role="row"]')).filter((row) => row.querySelector(":scope > [role=cell]"));
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
          className="relative grid h-(--list-thead-height) items-center gap-(--grid-gap) border-b border-hairline bg-surface-2/40 px-(--card-pad-x) grid-cols-(--list-cols-mobile) md:grid-cols-(--list-cols)"
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
                aria-sort={active ? (active.direction === "asc" ? "ascending" : "descending") : undefined}
                tabIndex={movable && !sortable ? 0 : undefined}
                aria-keyshortcuts={movable ? "Alt+ArrowLeft Alt+ArrowRight" : undefined}
                aria-describedby={movable && !sortable ? describedBy : undefined}
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
                  "relative min-w-0 rounded-xs text-(length:--type-micro) font-semibold uppercase tracking-widest text-muted-foreground focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary",
                  movable && "cursor-grab touch-pan-y select-none",
                  active && "text-foreground",
                  col.align === "end" && "text-right",
                  originalIndex !== 0 && !col.mobile && "hidden md:block",
                  col.srOnly && "sr-only"
                )}
              >
                {sortable ? (
                  <button
                    type="button"
                    onClick={() => state.toggleSort(id)}
                    aria-describedby={movable ? describedBy : undefined}
                    className="inline-flex max-w-full cursor-[inherit] items-center gap-1 border-0 bg-transparent p-0 uppercase tracking-widest text-inherit hover:text-foreground focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
                  >
                    <span title={typeof col.label === "string" ? col.label : undefined} className={HEADING_LABEL}>{col.label}</span>
                    {active ? <SortArrow direction={active.direction} /> : null}
                  </button>
                ) : (
                  <span title={typeof col.label === "string" ? col.label : undefined} className={cn("block", HEADING_LABEL)}>{col.label}</span>
                )}
              </div>
            );
          })}
          {chevron ? <span aria-hidden /> : null}
          {hasControls && !card ? <ColumnsButton onReset={state.reset} className="absolute right-1.5 top-1/2 -translate-y-1/2" /> : null}
        </div>
        <ListTableContext.Provider value={{ template, mobileTemplate, chevron, visual, reordered, flow }}>
          <div ref={bodyRef} role="rowgroup">{typeof children === "function" ? children(state.items) : children}</div>
        </ListTableContext.Provider>
      </div>
      {hasControls && !card ? <ColumnsAnnouncements controls={[controls]} /> : null}
    </>
  );
}

/* ----------------------------------------------------------------- row */

interface ListRowProps extends Omit<React.HTMLAttributes<HTMLDivElement>, "onClick"> {
  onClick?: () => void;
  selected?: boolean;
  children: React.ReactNode;
}

/**
 * On a phone a row is a wrapping line, not a grid. In a grid the name column (1fr) and the columns beside it (auto) were
 * sized against each other, and one long value (a path, a description, a button) took all the width and left the name a
 * strip one word or one letter wide. Now the name asks for 9rem and the rest follow it onto the same line only while
 * they fit; a long value goes underneath, full width, and wraps there. The chevron stays beside the name.
 */
const MOBILE_FLOW = [
  "max-md:flex max-md:flex-wrap max-md:gap-y-1 max-md:py-2",
  "max-md:[&>*]:order-2 max-md:[&>*]:min-w-0 max-md:[&>*]:max-w-full",
  "max-md:[&>:first-child]:order-0 max-md:[&>:first-child]:grow max-md:[&>:first-child]:basis-36"
].join(" ");

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
        "grid min-h-(--list-row-height) items-center gap-(--grid-gap) border-b border-hairline px-(--card-pad-x) last:border-b-0",
        "grid-cols-(--list-cols-mobile) md:grid-cols-(--list-cols)",
        table.flow && MOBILE_FLOW,
        interactive && "cursor-pointer transition-colors hover:bg-surface-2/60 focus-visible:outline-hidden focus-visible:bg-surface-2/60 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
        selected && "bg-primary/8 hover:bg-primary/10",
        className
      )}
      {...props}
    >
      {table.reordered ? orderedCells(children, table.visual) : children}
      {table.chevron ? (
        <span aria-hidden className={cn("flex justify-end text-muted-foreground", table.flow && "max-md:order-1! max-md:w-10 max-md:shrink-0")}>
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

/**
 * Cells cut a long value short with an ellipsis, which is right for a phone or
 * a laptop where the full value is one tap away in the row's drawer. On a wide
 * screen there is room to say it all, so the value wraps instead (#1162). A phone has no hover to read a tooltip with,
 * so there it wraps too (#1168).
 */
const WIDE_WRAP = "max-md:overflow-visible max-md:whitespace-normal max-md:break-words min-[1920px]:overflow-visible min-[1920px]:whitespace-normal min-[1920px]:break-words";

/**
 * A muted second line may take two lines before it is cut, and carries its full text as a tooltip. At 1920 it wraps
 * (WIDE_WRAP) and so must not be clamped: a two-line box whose text runs to three printed over the next row.
 */
const SECONDARY_CLAMP = "line-clamp-2 [overflow-wrap:anywhere] max-md:line-clamp-none min-[1920px]:line-clamp-none";

export function ListCell({ primary, secondary, mobile = false, align = "start", mono, numeric, className, children }: ListCellProps) {
  const originalIndex = React.useContext(CellIndexContext);
  return (
    <div
      role="cell"
      className={cn(
        "min-w-0 text-(length:--list-row-text) leading-tight",
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
            <span title={plainTitle(primary)} className={cn("block truncate text-foreground", WIDE_WRAP, mono && "font-mono text-(length:--type-caption)")}>{primary}</span>
          ) : null}
          {secondary !== undefined ? (
            <span title={plainTitle(secondary)} className={cn("mt-0.5 text-(length:--type-caption) text-muted-foreground", SECONDARY_CLAMP, WIDE_WRAP)}>{secondary}</span>
          ) : null}
        </>
      )}
    </div>
  );
}

/** First-column cell: bold name + muted subline, always visible. */
export function ListNameCell({ name, sub, className, nameTitle }: { name: React.ReactNode; sub?: React.ReactNode; className?: string; /** The name's full text when it holds a component that cannot be read as text (a version beside it). */ nameTitle?: string }) {
  return (
    <div role="cell" className={cn("min-w-0 leading-tight", className)}>
      <span title={nameTitle ?? plainTitle(name)} className={cn("block truncate text-(length:--list-row-text) font-semibold text-foreground", WIDE_WRAP)}>{name}</span>
      {sub !== undefined ? <span title={plainTitle(sub)} className={cn("mt-0.5 text-(length:--type-caption) text-muted-foreground", SECONDARY_CLAMP, WIDE_WRAP)}>{sub}</span> : null}
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
  // Inside a table the message is the table's one row with one cell, so a screen reader reads a table that says
  // "nothing here" instead of a row group with a stray block in it.
  const insideTable = Boolean(React.useContext(ListTableContext));
  const message = (
    <div
      role={insideTable ? "cell" : undefined}
      className="flex min-h-[112px] flex-col items-center justify-center gap-1.5 px-(--card-pad-x) py-(--card-pad-y) text-center"
    >
      <p className="text-(length:--type-body-sm) font-semibold text-foreground">{title}</p>
      {description ? <p className="max-w-[52ch] text-(length:--type-caption) text-muted-foreground">{description}</p> : null}
      {actions ? <div className="mt-1.5 flex flex-wrap items-center justify-center gap-2">{actions}</div> : null}
    </div>
  );
  return insideTable ? <div role="row">{message}</div> : message;
}
