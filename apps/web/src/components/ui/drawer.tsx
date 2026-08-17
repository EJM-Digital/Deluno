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
import * as Dialog from "@radix-ui/react-dialog";
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
      <header className="flex min-h-[var(--drawer-header-height)] shrink-0 items-center justify-between gap-[var(--grid-gap)] border-b border-hairline px-6">
        <div className="min-w-0">
          <Dialog.Title className="truncate text-[length:var(--type-title-sm)] font-semibold leading-tight tracking-[-0.01em]">
            {title}
          </Dialog.Title>
          <Dialog.Description className={cn("mt-0.5 truncate text-[length:var(--type-caption)] text-muted-foreground", !description && "sr-only")}>
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
        <footer className="flex min-h-[var(--drawer-footer-height)] shrink-0 items-center justify-between gap-[var(--grid-gap)] border-t border-hairline bg-surface-2/40 px-6">
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
            "focus:outline-none",
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
            <form className={bodyClassName} onSubmit={onSubmit} noValidate>
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
  /** Uppercase 11px label. Omit for a trailing danger section. */
  title?: React.ReactNode;
  /** Muted text right of the title (e.g. "12 rules selected"). */
  aside?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={cn("grid gap-[var(--grid-gap)] border-b border-hairline py-5 last:border-b-0", className)}>
      {title ? (
        <h3 className="flex items-baseline gap-2 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
          {title}
          {aside ? <span className="font-normal normal-case tracking-normal">{aside}</span> : null}
        </h3>
      ) : null}
      {children}
    </section>
  );
}

/* ------------------------------------------------------------- danger */

export function DrawerDanger({
  title,
  description,
  action
}: {
  title: React.ReactNode;
  description?: React.ReactNode;
  action: React.ReactNode;
}) {
  return (
    <div className="flex min-h-[52px] items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-destructive/30 px-[var(--field-pad-x)] py-2">
      <div className="min-w-0">
        <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">{title}</p>
        {description ? <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">{description}</p> : null}
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
  saveLabel,
  onCancel,
  onSave,
  saveType = "submit",
  disabled,
  saveEnabled
}: {
  state: DrawerSaveState;
  message?: string | null;
  saveLabel: string;
  onCancel: () => void;
  onSave?: () => void;
  saveType?: "submit" | "button";
  disabled?: boolean;
  /** Tool-style drawers (previews, tests) can keep the primary action enabled regardless of dirty state. */
  saveEnabled?: boolean;
}) {
  const statuses: Record<DrawerSaveState, FooterStatus | null> = {
    clean: null,
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
      <span role="status" aria-live="polite" className={cn("inline-flex min-w-0 items-center gap-1.5 text-[length:var(--type-caption)]", status?.tone)}>
        {status && StatusIcon ? (
          <>
            <StatusIcon className={cn("h-3 w-3 shrink-0", status.spin && "animate-spin")} strokeWidth={2.5} />
            <span className="truncate">{status.label}</span>
          </>
        ) : null}
      </span>
      <div className="flex shrink-0 items-center gap-2">
        <Button type="button" variant="outline" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type={saveType} onClick={saveType === "button" ? onSave : undefined} disabled={disabled || !canSave || saving}>
          {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
          {saveLabel}
        </Button>
      </div>
    </>
  );
}
