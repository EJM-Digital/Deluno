import * as Dialog from "./dialog-primitives";
import { AlertTriangle } from "lucide-react";
import * as React from "react";
import { cn } from "../../lib/utils";
import { Button } from "./button";

interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: React.ReactNode;
  confirmLabel?: string;
  confirmVariant?: "destructive" | "default";
  presentation?: "default" | "decision";
  secondaryLabel?: string;
  secondaryVariant?: "outline" | "destructive";
  busy?: boolean;
  onConfirm: () => void;
  onSecondary?: () => void;
  /** Extra content between the description and the buttons — a checkbox, for instance. */
  extraContent?: React.ReactNode;
}

export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel = "Confirm",
  confirmVariant = "destructive",
  presentation = "decision",
  secondaryLabel,
  secondaryVariant = "outline",
  busy = false,
  onConfirm,
  onSecondary,
  extraContent,
}: ConfirmDialogProps) {
  const isDecision = presentation === "decision";

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay
          className={cn(
            "fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px]",
            isDecision ? "data-[state=open]:animate-fade-in-stable" : "data-[state=open]:animate-fade-in"
          )}
        />
        <Dialog.Content
          className={cn(
            "fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] -translate-x-1/2 -translate-y-1/2",
            isDecision ? "max-w-xl" : "max-w-sm",
            "overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl",
            "dark:border-white/8",
            isDecision ? "data-[state=open]:animate-fade-in-stable" : "data-[state=open]:animate-fade-in"
          )}
          aria-describedby="confirm-description"
        >
          <div className={cn("p-6", isDecision && "p-8 sm:p-9")}>
            <div className={cn("mb-4 flex items-start gap-(--grid-gap)", isDecision && "mb-8")}>
              {confirmVariant === "destructive" && (
                <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-destructive/10">
                  <AlertTriangle className="h-4 w-4 text-destructive" />
                </span>
              )}
              <div>
                {/* The visible heading is the dialog's accessible name. A separate
                    sr-only Dialog.Title made screen readers announce it twice. */}
                <Dialog.Title
                  className={cn(
                    "font-semibold text-foreground",
                    isDecision && "text-(length:--type-title-md) tracking-[-0.02em]"
                  )}
                >
                  {title}
                </Dialog.Title>
                <p
                  id="confirm-description"
                  className={cn(
                    "mt-1 text-sm text-muted-foreground leading-relaxed",
                    isDecision && "max-w-prose text-(length:--type-body)"
                  )}
                >
                  {description}
                </p>
              </div>
            </div>

            {extraContent ? <div className="mb-4">{extraContent}</div> : null}

            <div
              className={cn(
                isDecision ? "flex flex-col gap-3" : "flex flex-col-reverse gap-2 sm:flex-row sm:justify-end"
              )}
            >
              <Dialog.Close asChild>
                <Button
                  variant="secondary"
                  size={isDecision ? "lg" : "sm"}
                  className={cn(
                    "w-full",
                    !isDecision && "sm:w-auto",
                    isDecision && "order-3 h-auto min-h-(--control-height-lg) justify-center whitespace-normal px-5 py-3 text-center"
                  )}
                  disabled={busy}
                >
                  Cancel
                </Button>
              </Dialog.Close>
              {secondaryLabel && onSecondary ? (
                <Button
                  size={isDecision ? "lg" : "sm"}
                  variant={secondaryVariant}
                  className={cn(
                    "w-full",
                    !isDecision && "sm:w-auto",
                    isDecision && "order-2 h-auto min-h-(--control-height-lg) justify-center whitespace-normal px-5 py-3 text-center"
                  )}
                  disabled={busy}
                  onClick={onSecondary}
                >
                  {secondaryLabel}
                </Button>
              ) : null}
              <Button
                size={isDecision ? "lg" : "sm"}
                className={cn(
                  "w-full",
                  !isDecision && "sm:w-auto",
                  isDecision && "order-1 h-auto min-h-(--control-height-lg) justify-center whitespace-normal px-5 py-3 text-center"
                )}
                disabled={busy}
                onClick={onConfirm}
                variant={confirmVariant === "destructive" ? "destructive-solid" : "default"}
              >
                {confirmLabel}
              </Button>
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
