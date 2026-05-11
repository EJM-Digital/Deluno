import * as Dialog from "@radix-ui/react-dialog";
import { AlertTriangle } from "lucide-react";
import * as React from "react";
import { cn } from "../../lib/utils";
import { Button } from "./button";

interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  confirmLabel?: string;
  confirmVariant?: "destructive" | "default";
  busy?: boolean;
  onConfirm: () => void;
}

export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel = "Confirm",
  confirmVariant = "destructive",
  busy = false,
  onConfirm,
}: ConfirmDialogProps) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className={cn(
            "fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2",
            "overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl",
            "dark:border-white/[0.08]",
            "data-[state=open]:animate-fade-in"
          )}
          aria-describedby="confirm-description"
        >
          <Dialog.Title className="sr-only">{title}</Dialog.Title>
          <div className="p-6">
            <div className="mb-4 flex items-start gap-4">
              {confirmVariant === "destructive" && (
                <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-red-500/10">
                  <AlertTriangle className="h-4 w-4 text-red-500" />
                </span>
              )}
              <div>
                <p className="font-semibold text-foreground">{title}</p>
                <p id="confirm-description" className="mt-1 text-sm text-muted-foreground leading-relaxed">
                  {description}
                </p>
              </div>
            </div>

            <div className="flex justify-end gap-2">
              <Dialog.Close asChild>
                <Button variant="secondary" size="sm" disabled={busy}>
                  Cancel
                </Button>
              </Dialog.Close>
              <Button
                size="sm"
                disabled={busy}
                onClick={onConfirm}
                className={
                  confirmVariant === "destructive"
                    ? "bg-red-600 text-white hover:brightness-110 shadow-none from-red-600 to-red-700"
                    : undefined
                }
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
