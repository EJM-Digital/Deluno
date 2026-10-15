/**
 * Drawer — the one editor surface.
 *
 *   header (72px): title · subtitle · close
 *   body: DrawerSection × n, in a fixed order per object type:
 *         Basics · domain section(s) · Fine-tune · Used by / Health · DrawerDanger
 *   footer (64px): status · Cancel · Save
 *
 * Opens from the right at --drawer-width; full-screen on narrow viewports.
 * Closing while dirty is the caller's decision (see useUnsavedChanges).
 */
import * as Dialog from "./dialog-primitives";
import { plainTitle } from "../../lib/plain-title";
import { X, Check, CircleAlert, Loader2, CircleDot, type LucideIcon } from "lucide-react";
import * as React from "react";
import { cn } from "../../lib/utils";
import { Button } from "./button";

interface DrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: React.ReactNode;
  description?: React.ReactNode;
  children: React.ReactNode;
  footer?: React.ReactNode;
  /** Wrap body in a form so Enter submits and the footer's Save can be type=submit. */
  onSubmit?: (event: React.FormEvent<HTMLFormElement>) => void;
  className?: string;
}

const bodyClassName = "flex h-full min-h-0 flex-col";

export function Drawer({ open, onOpenChange, title, description, children, footer, onSubmit, className }: DrawerProps) {
  const body = (
    <>
      <header className="flex min-h-(--drawer-header-height) shrink-0 items-center justify-between gap-(--grid-gap) border-b border-hairline px-6">
        <div className="min-w-0">
          <Dialog.Title title={plainTitle(title)} className="line-clamp-2 max-md:line-clamp-none break-words [overflow-wrap:anywhere] text-(length:--type-title-sm) font-semibold leading-tight tracking-[-0.01em]">
            {title}
          </Dialog.Title>
          <Dialog.Description className={cn("mt-0.5 truncate text-(length:--type-caption) text-muted-foreground max-md:whitespace-normal max-md:break-words", !description && "sr-only")}>
            {description ?? "Edit the details and save."}
          </Dialog.Description>
        </div>
        <Dialog.Close asChild>
          <Button type="button" variant="outline" size="icon" aria-label="Close" className="h-8 w-8 rounded-[8px]">
            <X className="h-3.5 w-3.5" />
          </Button>
        </Dialog.Close>
      </header>

      <div className="min-h-0 flex-1 overflow-y-auto px-6">{children}</div>

      {footer ? (
        <footer
          className={cn(
            "flex min-h-(--drawer-footer-height) shrink-0 flex-col items-stretch gap-2 border-t border-hairline bg-surface-2/40 px-6 py-3",
            "sm:h-(--drawer-footer-height) sm:flex-row sm:items-center sm:justify-between sm:gap-(--grid-gap) sm:py-0"
          )}
        >
          {footer}
        </footer>
      ) : null}
    </>
  );

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-[hsl(222_44%_3%/0.45)] backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className={cn(
            "fixed inset-y-0 right-0 z-50 flex w-full flex-col border-l border-hairline bg-card text-foreground shadow-lg",
            "sm:w-[min(var(--drawer-width),100vw)]",
            "data-[state=open]:animate-drawer-in data-[state=closed]:animate-drawer-out",
            "focus:outline-hidden",
            className
          )}
          onOpenAutoFocus={(event) => {
            // Focus the first field rather than the close button.
            const first = (event.currentTarget as HTMLElement).querySelector<HTMLElement>(
              "input:not([type=hidden]), select, textarea, [role=radiogroup] [tabindex='0'], button[role=switch]"
            );
            if (first) {
              event.preventDefault();
              first.focus();
            }
          }}
        >
          {onSubmit ? (
            // The drawer is portaled to <body>, but React bubbles synthetic events
            // up the component tree, not the DOM tree — so on a page that wraps its
            // own <form> around the list, saving here would submit that form too.
            <form
              className={bodyClassName}
              onSubmit={(event) => {
                event.stopPropagation();
                onSubmit(event);
              }}
              noValidate
            >
              {body}
            </form>
          ) : (
            <div className={bodyClassName}>{body}</div>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

/* ------------------------------------------------------------ section */

export function DrawerSection({
  title,
  aside,
  children,
  className
}: {
  /** Quiet eyebrow heading. Omit for a trailing danger section. */
  title?: React.ReactNode;
  /** Muted text right of the title (e.g. "12 rules selected"). */
  aside?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={cn("grid min-w-0 gap-(--grid-gap) border-b border-hairline py-5 last:border-b-0", className)}>
      {title ? (
        <h3 className="flex min-w-0 flex-wrap items-baseline gap-x-(--grid-gap) gap-y-1 text-(length:--type-caption) font-semibold uppercase leading-tight tracking-widest text-muted-foreground">
          <span className="min-w-0">{title}</span>
          {aside ? <span className="max-w-full font-normal normal-case leading-tight tracking-normal">{aside}</span> : null}
        </h3>
      ) : null}
      {children}
    </section>
  );
}

/* -------------------------------------------------------------- facts */

/**
 * Read-only label/value lines inside a DrawerSection — what a drawer shows when
 * it is explaining rather than editing (a provider record, the file on disk, the
 * inputs behind a decision). `Field` is for controls; this is its counterpart.
 */
export function DrawerFacts({ items }: { items: Array<{ label: React.ReactNode; value: React.ReactNode; mono?: boolean }> }) {
  return (
    <dl className="grid gap-1.5">
      {items.map((item, index) => (
        <div key={index} className="flex min-w-0 items-baseline justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0">
          <dt className="shrink-0 text-(length:--type-caption) text-muted-foreground">{item.label}</dt>
          <dd
            className={cn(
              "min-w-0 truncate text-right text-(length:--type-body-sm) text-foreground",
              item.mono && "font-mono text-(length:--type-caption)"
            )}
            title={typeof item.value === "string" ? item.value : undefined}
          >
            {item.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/* ------------------------------------------------------------- danger */

export function DrawerDanger({
  title,
  description,
  action,
  unavailableReason
}: {
  title: React.ReactNode;
  description?: React.ReactNode;
  action: React.ReactNode;
  /** Why the action cannot be used right now; said beside it so a dimmed button is never silent (#1248). */
  unavailableReason?: string | null;
}) {
  return (
    <div className="flex min-h-[52px] items-center justify-between gap-(--grid-gap) rounded-[10px] border border-destructive/30 px-(--field-pad-x) py-2">
      <div className="min-w-0">
        <p className="text-(length:--type-body-sm) font-medium text-foreground">{title}</p>
        {description ? <p className="mt-0.5 text-(length:--type-caption) text-muted-foreground">{description}</p> : null}
        {unavailableReason ? <p role="status" className="mt-0.5 text-(length:--type-caption) text-muted-foreground">{unavailableReason}</p> : null}
      </div>
      {action}
    </div>
  );
}

/* ------------------------------------------------------------- footer */

export type DrawerSaveState = "clean" | "dirty" | "saving" | "saved" | "error";

interface FooterStatus {
  icon: LucideIcon;
  tone: string;
  label: string;
  spin?: boolean;
}

/**
 * Standard footer: status on the left, Cancel + Save on the right.
 * `saveLabel` says what happens ("Save plan"), never just "Save".
 */
export function DrawerFooter({
  state,
  message,
  note,
  saveLabel,
  onCancel,
  onSave,
  saveType = "submit",
  disabled,
  saveEnabled,
  readOnly = false,
  extraAction
}: {
  state: DrawerSaveState;
  /**
   * What the last save said ("Routing saved", or why it failed). Shown only with the saved and error states, so a
   * message left over from an earlier drawer never appears on a freshly opened one (#1151).
   */
  message?: string | null;
  /** A standing note for a drawer that only explains something; shown in the clean state. */
  note?: string | null;
  saveLabel: string;
  onCancel: () => void;
  onSave?: () => void;
  saveType?: "submit" | "button";
  disabled?: boolean;
  /** Tool-style drawers (previews, tests) can keep the primary action enabled regardless of dirty state. */
  saveEnabled?: boolean;
  /**
   * This drawer explains rather than edits: one "Close" button, no Cancel.
   * Cancel and a disabled Save mean the same thing on a read-only surface.
   */
  readOnly?: boolean;
  /** One more action beside Cancel and Save, such as Test (#626). */
  extraAction?: React.ReactNode;
}) {
  const statuses: Record<DrawerSaveState, FooterStatus | null> = {
    // A tool-style surface has no dirty state but still has something to say.
    clean: note ? { icon: CircleDot, tone: "text-muted-foreground", label: note } : null,
    dirty: { icon: CircleDot, tone: "text-warning", label: "Unsaved changes" },
    saving: { icon: Loader2, tone: "text-muted-foreground", label: "Saving…", spin: true },
    saved: { icon: Check, tone: "text-success", label: message ?? "Saved just now" },
    error: { icon: CircleAlert, tone: "text-destructive", label: message ?? "Could not save" }
  };
  const status = statuses[state];
  const saving = state === "saving";
  const canSave = saveEnabled ?? (state === "dirty" || state === "error");
  const StatusIcon = status?.icon;

  return (
    <>
      <span
        role="status"
        aria-live="polite"
        className={cn("flex min-w-0 items-center gap-1.5 text-(length:--type-caption) sm:inline-flex", status?.tone)}
      >
        {status && StatusIcon ? (
          <>
            <StatusIcon className={cn("h-3 w-3 shrink-0", status.spin && "animate-spin")} strokeWidth={2.5} />
            {/* Wraps here rather than truncating: the extra footer action on
                narrow viewports left no room to shrink this into and still
                read, so the whole status stacks above the buttons instead
                of disappearing to nothing (#925). */}
            <span className="min-w-0 sm:truncate">{status.label}</span>
          </>
        ) : null}
      </span>
      <div className="flex flex-wrap shrink-0 items-center justify-end gap-2">
        {readOnly ? (
          <>
            {extraAction}
            <Button type="button" onClick={onCancel}>
              {saveLabel}
            </Button>
          </>
        ) : (
          <>
            {extraAction}
            <Button type="button" variant="outline" onClick={onCancel} disabled={saving}>
              Cancel
            </Button>
            <Button type={saveType} onClick={saveType === "button" ? onSave : undefined} disabled={disabled || !canSave || saving}>
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              {saveLabel}
            </Button>
          </>
        )}
      </div>
    </>
  );
}
