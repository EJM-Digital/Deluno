import * as Dialog from "./dialog-primitives";
import { X } from "lucide-react";
import * as React from "react";
import { cn } from "../../lib/utils";

export const Sheet = Dialog.Root;
export const SheetTrigger = Dialog.Trigger;
export const SheetClose = Dialog.Close;

export function SheetContent({
  className,
  children,
  side = "right",
  description,
  ...props
}: Dialog.DialogContentProps & {
  side?: "left" | "right" | "bottom";
  /** What the panel is for, read out to screen readers. Defaults to its label. */
  description?: string;
}) {
  const isBottom = side === "bottom";
  const accessibleLabel =
    typeof props["aria-label"] === "string" && props["aria-label"].trim().length > 0
      ? props["aria-label"]
      : "Panel";
  const accessibleDescription = description ?? (accessibleLabel === "Panel" ? "Details and actions." : `Details and actions for ${accessibleLabel}.`);

  return (
    <Dialog.Portal>
      <Dialog.Overlay
        className={cn(
          "fixed inset-0 z-50",
          isBottom ? "bg-black/50 backdrop-blur-[2px]" : "bg-background/75 backdrop-blur-[6px]"
        )}
      />
      <Dialog.Content
        className={cn(
          isBottom
            ? cn(
                "fixed inset-x-0 mx-auto z-50 flex w-[calc(100%-1.25rem)] max-w-md flex-col",
                "bottom-[max(0.5rem,env(safe-area-inset-bottom))] max-h-[min(88dvh,640px)]",
                "overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl",
                "dark:border-white/8 dark:bg-[hsl(226_22%_9%/0.98)]",
                "data-[state=open]:animate-mobile-nav-sheet-in data-[state=closed]:animate-mobile-nav-sheet-out"
              )
            : cn(
                "fixed top-0 z-50 h-full w-full max-w-md border-hairline bg-card shadow-lg animate-fade-in",
                side === "right" ? "right-0 border-l" : "left-0 border-r"
              ),
          className
        )}
        {...props}
      >
        <Dialog.Title className="sr-only">{accessibleLabel}</Dialog.Title>
        <Dialog.Description className="sr-only">
          {accessibleDescription}
        </Dialog.Description>
        {children}
        <Dialog.Close
          className={cn(
            "absolute rounded-md p-2 text-muted-foreground transition hover:bg-secondary hover:text-foreground",
            "focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-primary/40",
            isBottom ? "right-2 top-2.5 z-10" : "right-4 top-4"
          )}
        >
          <X className="h-4 w-4" />
          <span className="sr-only">Close</span>
        </Dialog.Close>
      </Dialog.Content>
    </Dialog.Portal>
  );
}
