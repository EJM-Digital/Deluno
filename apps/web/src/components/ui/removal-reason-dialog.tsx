/**
 * The Queue's "why" behind removing a download (cleanup sheet J1/J2, #924).
 *
 * Replaces the old single yes/no confirm. This dialog IS the confirmation:
 * there is no second gate behind it, and each of the three plain-word
 * choices commits to something different as soon as it is clicked.
 */
import * as Dialog from "./dialog-primitives";
import { Loader2 } from "lucide-react";
import { cn } from "../../lib/utils";
import { Button } from "./button";

export type RemovalReason = "failed" | "unwanted" | "forget" | "reclaim";

interface RemovalReasonDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  busyReason: RemovalReason | null;
  onChoose: (reason: RemovalReason) => void;
}

const CHOICES: {
  reason: RemovalReason;
  label: string;
  description: string;
  variant: "destructive-solid" | "outline" | "secondary";
}[] = [
  {
    reason: "failed",
    label: "It's bad",
    description: "Remove it from the client, delete its files, blocklist this release everywhere it turns up, and search for another.",
    variant: "destructive-solid"
  },
  {
    reason: "unwanted",
    label: "I don't want it",
    description: "Remove it from the client, delete its files, and stop wanting this title. Nothing is blocklisted.",
    variant: "outline"
  },
  {
    reason: "forget",
    label: "Just stop tracking it",
    description: "Deluno forgets this download. The client keeps it and its files, untouched.",
    variant: "secondary"
  }
];

export function RemovalReasonDialog({ open, onOpenChange, title, busyReason, onChoose }: RemovalReasonDialogProps) {
  const busy = busyReason !== null;

  return (
    <Dialog.Root open={open} onOpenChange={(next) => { if (!busy) onOpenChange(next); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px] data-[state=open]:animate-fade-in-stable" />
        <Dialog.Content
          className={cn(
            "fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2",
            "overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl dark:border-white/8",
            "data-[state=open]:animate-fade-in-stable"
          )}
          aria-describedby="removal-reason-description"
        >
          <div className="p-8 sm:p-9">
            <Dialog.Title className="text-(length:--type-title-md) font-semibold tracking-[-0.02em] text-foreground">
              Why remove “{title}”?
            </Dialog.Title>
            <p id="removal-reason-description" className="mt-1 max-w-prose text-(length:--type-body) text-muted-foreground leading-relaxed">
              Every choice removes it from Transfers. What Deluno does next depends on why.
            </p>

            <div className="mt-6 flex flex-col gap-3">
              {CHOICES.map((choice) => (
                <Button
                  key={choice.reason}
                  type="button"
                  variant={choice.variant}
                  size="lg"
                  aria-label={`Remove “${title}”: ${choice.label}`}
                  disabled={busy}
                  onClick={() => onChoose(choice.reason)}
                  className="h-auto min-h-(--control-height-lg) w-full flex-col items-start gap-1 whitespace-normal px-5 py-3 text-left"
                >
                  <span className="flex w-full items-center gap-2 text-(length:--type-body) font-semibold">
                    {busyReason === choice.reason ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                    {choice.label}
                  </span>
                  <span className="w-full text-(length:--type-caption) font-normal opacity-80">
                    {choice.description}
                  </span>
                </Button>
              ))}
            </div>

            <div className="mt-4 flex justify-end">
              <Dialog.Close asChild>
                <Button type="button" variant="ghost" size="sm" disabled={busy}>
                  Cancel
                </Button>
              </Dialog.Close>
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
