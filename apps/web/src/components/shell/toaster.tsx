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

import { Toaster as SonnerToaster, toast as sonnerToast } from "sonner";
import { useTheme } from "next-themes";
import { CheckCircle2, CircleAlert, Info, Loader2, XCircle } from "lucide-react";
import { cn } from "../../lib/utils";

export function Toaster() {
  const { resolvedTheme } = useTheme();
  return (
    <SonnerToaster
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
          title: "text-[13px] font-semibold leading-snug tracking-tight text-foreground",
          description: "mt-0.5 text-[12px] leading-snug text-muted-foreground",
          actionButton:
            "ml-auto inline-flex h-7 items-center gap-1 rounded-md bg-primary px-2.5 text-[11.5px] font-semibold text-primary-foreground shadow-sm transition hover:bg-primary/90",
          cancelButton:
            "inline-flex h-7 items-center rounded-md border border-hairline bg-card px-2.5 text-[11.5px] font-medium text-muted-foreground transition hover:text-foreground",
          closeButton:
            "absolute right-2 top-2 flex h-6 w-6 items-center justify-center rounded-md text-muted-foreground/70 transition hover:bg-muted/60 hover:text-foreground"
        }
      }}
    />
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
