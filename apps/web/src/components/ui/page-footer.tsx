/**
 * PageFooter — the drawer footer's counterpart for page-level forms
 * (Size rules, File handling, Automation…). Pinned to the bottom of the content
 * area so Save is always reachable, no matter which card you are editing.
 *
 * Same anatomy as DrawerFooter: status left, Discard + Save right.
 *
 * Positioned `fixed` rather than `sticky` on purpose: the app shell wraps every
 * page in an `overflow-x: hidden` container, which becomes the scroll container
 * for sticky and would stop the bar pinning. It offsets by the sidebar width on
 * large screens and sits above the mobile tab bar on small ones; a spacer keeps
 * the last card clear of it.
 */
import * as React from "react";
import { Check, CircleAlert, CircleDot, Loader2, type LucideIcon } from "lucide-react";
import { cn } from "../../lib/utils";
import { Button } from "./button";
import type { DrawerSaveState } from "./drawer";

interface FooterStatus {
  icon: LucideIcon;
  tone: string;
  label: string;
  spin?: boolean;
}

export function PageFooter({
  state,
  message,
  saveLabel,
  onDiscard,
  onSave,
  saveType = "submit",
  disabled,
  className
}: {
  state: DrawerSaveState;
  message?: string | null;
  saveLabel: string;
  onDiscard: () => void;
  onSave?: () => void;
  saveType?: "submit" | "button";
  disabled?: boolean;
  className?: string;
}) {
  const statuses: Record<DrawerSaveState, FooterStatus | null> = {
    // A tool-style surface has no dirty state but still has something to say.
    clean: message ? { icon: CircleDot, tone: "text-muted-foreground", label: message } : null,
    dirty: { icon: CircleDot, tone: "text-warning", label: "Unsaved changes" },
    saving: { icon: Loader2, tone: "text-muted-foreground", label: "Saving…", spin: true },
    saved: { icon: Check, tone: "text-success", label: message ?? "Saved just now" },
    error: { icon: CircleAlert, tone: "text-destructive", label: message ?? "Could not save" }
  };
  const status = statuses[state];
  const StatusIcon = status?.icon;
  const saving = state === "saving";
  const canSave = state === "dirty" || state === "error";

  return (
    <>
      <div aria-hidden className="h-[var(--drawer-footer-height)]" />
      <div
        className={cn(
          "fixed inset-x-0 bottom-[var(--mobile-tabbar-height)] z-30 flex min-h-[var(--drawer-footer-height)] items-center justify-between gap-[var(--grid-gap)]",
          "border-t border-hairline bg-card/95 px-[var(--content-pad-inline)] backdrop-blur supports-[backdrop-filter]:bg-card/85",
          "lg:bottom-0 lg:left-[var(--sidebar-width)]",
          className
        )}
      >
        <span role="status" aria-live="polite" className={cn("inline-flex min-w-0 items-center gap-1.5 text-[length:var(--type-caption)]", status?.tone)}>
          {status && StatusIcon ? (
            <>
              <StatusIcon className={cn("h-3 w-3 shrink-0", status.spin && "animate-spin")} strokeWidth={2.5} />
              <span className="truncate">{status.label}</span>
            </>
          ) : null}
        </span>
        <div className="flex shrink-0 items-center gap-2">
          <Button type="button" variant="outline" onClick={onDiscard} disabled={saving || state === "clean"}>
            Discard
          </Button>
          <Button type={saveType} onClick={saveType === "button" ? onSave : undefined} disabled={disabled || !canSave || saving}>
            {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
            {saveLabel}
          </Button>
        </div>
      </div>
    </>
  );
}
