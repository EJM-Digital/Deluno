/**
 * Deluno toast surface.
 *
 * Thin wrapper over sonner — locked to our accent/surface tokens so
 * toasts blend into the rest of the shell instead of looking like a
 * third-party drop-in. Exports `toast` for app-wide imports and a
 * `<Toaster />` for mounting at the app root.
 *
 * Every toast renders in an aria-live region (sonner handles that)
 * so screen readers announce them automatically.
 */

import { Toaster as SonnerToaster, toast as sonnerToast, useSonner } from "sonner";
import { useTheme } from "next-themes";
import { CheckCircle2, CircleAlert, Info, Loader2, X, XCircle } from "lucide-react";
import { cn } from "../../lib/utils";

/**
 * The height reserved below the toast stack for the clear-all control, so the
 * two can never overlap however many toasts are showing or however far the
 * stack expands on hover.
 */
const CLEAR_ALL_LANE_PX = 44;

/**
 * One control that clears the whole stack.
 *
 * Every toast already has its own close button, which is fine for one and
 * tedious for six: a burst of per-item results - a bulk action, a sync that
 * fails item by item - buries the corner of the screen, and the only ways out
 * are to click them off one at a time or wait them out. This appears once
 * there is more than one toast to clear and gets out of the way again when
 * there isn't.
 */
function DismissAllToasts() {
  const { toasts } = useSonner();
  if (toasts.length < 2) return null;

  return (
    <button
      type="button"
      onClick={() => sonnerToast.dismiss()}
      className="fixed bottom-3 right-4 z-[9999] inline-flex items-center gap-1.5 rounded-full border border-hairline bg-card/95 px-3 py-1 text-[length:var(--type-caption)] font-medium text-muted-foreground shadow-lg backdrop-blur transition hover:text-foreground dark:border-white/[0.06]"
    >
      <X className="h-3.5 w-3.5" aria-hidden="true" />
      Clear all {toasts.length}
    </button>
  );
}

export function Toaster() {
  const { resolvedTheme } = useTheme();
  return (
    <>
    <DismissAllToasts />
    <SonnerToaster
      offset={{ bottom: CLEAR_ALL_LANE_PX }}
      position="bottom-right"
      theme={resolvedTheme === "dark" ? "dark" : "light"}
      closeButton
      duration={4500}
      visibleToasts={4}
      icons={{
        success: <CheckCircle2 className="h-4 w-4 text-success" strokeWidth={2.25} />,
        info: <Info className="h-4 w-4 text-info" strokeWidth={2.25} />,
        warning: <CircleAlert className="h-4 w-4 text-warning" strokeWidth={2.25} />,
        error: <XCircle className="h-4 w-4 text-destructive" strokeWidth={2.25} />,
        loading: <Loader2 className="h-4 w-4 animate-spin text-primary" strokeWidth={2.25} />
      }}
      toastOptions={{
        className: cn(
          "rounded-xl border border-hairline bg-card text-foreground shadow-lg",
          "dark:border-white/[0.06]"
        ),
        classNames: {
          toast:
            "group flex w-full items-start gap-3 rounded-xl border border-hairline bg-card/95 p-3 pr-8 text-sm text-foreground shadow-lg backdrop-blur dark:border-white/[0.06] dark:bg-card/90",
          title: "text-[length:var(--type-body-sm)] font-semibold leading-snug tracking-tight text-foreground",
          description: "mt-0.5 text-[length:var(--type-caption)] leading-snug text-muted-foreground",
          actionButton:
            "ml-auto inline-flex h-7 items-center gap-1 rounded-md bg-primary px-2.5 text-[length:var(--type-caption)] font-semibold text-primary-foreground shadow-sm transition hover:bg-primary/90",
          cancelButton:
          "inline-flex h-7 items-center rounded-md border border-hairline bg-card px-2.5 text-[length:var(--type-caption)] font-medium text-muted-foreground transition hover:text-foreground",
          closeButton:
            "absolute right-2 top-2 flex h-6 w-6 items-center justify-center rounded-md text-muted-foreground/70 transition hover:bg-muted/60 hover:text-foreground"
        }
      }}
    />
    </>
  );
}

/**
 * Re-export of sonner's toast with a slightly friendlier shape. Use:
 *   toast.success("Saved")
 *   toast.error("Could not reach indexer", { description: "…" })
 *   const id = toast.loading("Testing provider…")
 *   toast.success("Provider ready", { id })
 */
export const toast = sonnerToast;
