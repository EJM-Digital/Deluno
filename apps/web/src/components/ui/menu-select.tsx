import * as React from "react";
import { createPortal } from "react-dom";
import { ChevronDown } from "lucide-react";
import { cn } from "../../lib/utils";

export interface MenuSelectOption {
  value: string;
  label: string;
  /**
   * A line under the label saying what it means.
   *
   * Optional, because most pick-one lists are self-explanatory and a second line
   * on every row would only add height. It exists for the ones that are not:
   * "Popularity" and "Bitrate" are orders nobody can guess at, and a label you
   * have to guess at is a control you avoid.
   */
  hint?: string;
}

/**
 * Pick one of a short list, in Deluno's own chrome.
 *
 * A native `<select>` cannot be this. The list it opens is drawn by the
 * operating system, so it arrives square, flush and highlighted in the system
 * blue no matter what the page around it looks like — beside a menu Deluno drew
 * itself, the two do not read as the same control. Colour tokens on `option`
 * get the shade right and still leave the shape and the highlight wrong.
 *
 * So this is a real one: a button and a list of buttons, styled once, used
 * everywhere a short pick-one belongs. It keeps what the native control gave
 * away for free — arrow keys, Home and End, Escape, and focus returning to the
 * trigger — and it is announced as what it is. A listbox, not a menu: this
 * chooses a value, and a menu is for commands.
 *
 * Use `Select` instead for a long or open-ended list, where the native control's
 * scrolling and type-ahead earn their keep.
 */
export function MenuSelect({
  value,
  options,
  onChange,
  label,
  align = "start",
  menuWidth,
  leading,
  className,
  triggerClassName
}: {
  value: string;
  options: MenuSelectOption[];
  onChange: (value: string) => void;
  /** Names the control for assistive technology. */
  label: string;
  align?: "start" | "end";
  /** Defaults to matching the trigger, which is what a picker usually wants. */
  menuWidth?: string;
  /** Rendered before the label — a status dot, an icon. */
  leading?: React.ReactNode;
  className?: string;
  triggerClassName?: string;
}) {
  const listId = React.useId();
  const [open, setOpen] = React.useState(false);
  const [anchor, setAnchor] = React.useState<{ top: number; left: number; right: number; width: number } | null>(null);
  const rootRef = React.useRef<HTMLDivElement>(null);
  const triggerRef = React.useRef<HTMLButtonElement>(null);
  const menuRef = React.useRef<HTMLDivElement>(null);
  const itemRefs = React.useRef<Array<HTMLButtonElement | null>>([]);

  const selectedIndex = Math.max(0, options.findIndex((option) => option.value === value));
  const selected = options[selectedIndex];

  React.useEffect(() => {
    if (!open) return;

    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      // The menu is portalled out of this subtree, so "inside" means either.
      if (rootRef.current?.contains(target) || menuRef.current?.contains(target)) return;
      setOpen(false);
    }

    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open]);

  /*
    The menu is rendered into `document.body` rather than beside its trigger.

    A dropdown that stays in the tree is at the mercy of whatever is above it:
    the library toolbar clips its children with `overflow-hidden` to keep its
    rounded corners, so the second option was simply cut off — present in the
    DOM, invisible on screen. The header has a stacking context of its own for
    the same kind of reason. Neither is wrong; a menu just does not belong
    inside them. Portalled and positioned from the trigger's own box, it can
    open anywhere.
  */
  React.useLayoutEffect(() => {
    if (!open) return;

    function measure() {
      const rect = triggerRef.current?.getBoundingClientRect();
      if (!rect) return;
      setAnchor({ top: rect.bottom + 8, left: rect.left, right: window.innerWidth - rect.right, width: rect.width });
    }

    measure();
    window.addEventListener("resize", measure);
    // Capture, so it follows a trigger inside any scrolling panel, not just the page.
    window.addEventListener("scroll", measure, true);
    return () => {
      window.removeEventListener("resize", measure);
      window.removeEventListener("scroll", measure, true);
    };
  }, [open]);

  // Opening lands on the current choice, the way a native select does, so the
  // first arrow key moves from where you are rather than from the top.
  //
  // Waits on `anchor` as well as `open`: the list is portalled and cannot render
  // until the trigger has been measured, so on the first pass there is nothing
  // to focus yet. Without that, focus stayed on the trigger and the arrow keys
  // did nothing — the one thing the native control had given away for free.
  React.useEffect(() => {
    if (open && anchor) itemRefs.current[selectedIndex]?.focus();
  }, [open, anchor, selectedIndex]);

  function close(returnFocus: boolean) {
    setOpen(false);
    if (returnFocus) triggerRef.current?.focus();
  }

  function moveFocus(from: number, delta: number) {
    const next = (from + delta + options.length) % options.length;
    itemRefs.current[next]?.focus();
  }

  function onOptionKeyDown(event: React.KeyboardEvent, index: number) {
    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        moveFocus(index, 1);
        break;
      case "ArrowUp":
        event.preventDefault();
        moveFocus(index, -1);
        break;
      case "Home":
        event.preventDefault();
        itemRefs.current[0]?.focus();
        break;
      case "End":
        event.preventDefault();
        itemRefs.current[options.length - 1]?.focus();
        break;
      case "Escape":
        event.preventDefault();
        close(true);
        break;
      case "Tab":
        setOpen(false);
        break;
      default:
        break;
    }
  }

  return (
    <div ref={rootRef} className={cn("relative", className)}>
      <button
        ref={triggerRef}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-controls={listId}
        aria-expanded={open}
        aria-label={label}
        // Exposed so a caller can style the open state without owning the state.
        data-open={open}
        onClick={() => setOpen((current) => !current)}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            setOpen(true);
          }
        }}
        className={cn(
          "flex w-full min-w-0 items-center gap-2 rounded-xl text-left transition [&>span:nth-last-child(2)]:mr-auto",
          triggerClassName
        )}
      >
        {leading}
        <span className="truncate">{selected?.label ?? ""}</span>
        <ChevronDown
          aria-hidden
          className={cn("h-4 w-4 shrink-0 text-muted-foreground/80 transition", open && "rotate-180")}
          strokeWidth={1.75}
        />
      </button>

      {open && anchor ? createPortal(
        <div
          ref={menuRef}
          id={listId}
          role="listbox"
          aria-label={label}
          style={{
            position: "fixed",
            top: anchor.top,
            ...(align === "end" ? { right: anchor.right } : { left: anchor.left }),
            minWidth: anchor.width
          }}
          className={cn(
            "z-[80] overflow-hidden rounded-xl border border-hairline/80 bg-popover p-1.5 shadow-lg dark:border-white/[0.07]",
            menuWidth
          )}
        >
          {options.map((option, index) => {
            const isSelected = option.value === value;
            return (
              <button
                key={option.value}
                ref={(element) => { itemRefs.current[index] = element; }}
                type="button"
                role="option"
                aria-selected={isSelected}
                onClick={() => {
                  onChange(option.value);
                  close(true);
                }}
                onKeyDown={(event) => onOptionKeyDown(event, index)}
                className={cn(
                  "flex min-h-[var(--control-height-sm)] w-full items-center justify-between gap-3 rounded-lg px-3 text-left",
                  "text-[length:var(--shell-nav-size)] font-semibold transition",
                  isSelected
                    ? "bg-primary/12 text-foreground ring-1 ring-inset ring-primary/20"
                    : "text-muted-foreground hover:bg-muted/45 hover:text-foreground"
                )}
              >
                <span className="min-w-0">
                  <span className="block truncate">{option.label}</span>
                  {option.hint ? (
                    <span className="block truncate text-[length:var(--type-micro)] font-normal text-muted-foreground">
                      {option.hint}
                    </span>
                  ) : null}
                </span>
                {isSelected ? (
                  <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-primary shadow-[0_0_10px_hsl(var(--primary)/0.75)]" />
                ) : null}
              </button>
            );
          })}
        </div>,
        document.body
      ) : null}
    </div>
  );
}
