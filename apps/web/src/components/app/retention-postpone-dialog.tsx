import * as Dialog from "../ui/dialog-primitives";
import { Clock } from "lucide-react";
import { useEffect, useState } from "react";
import { formatDateTime, useDisplayPreferences } from "../../lib/display-preferences";
import { Button } from "../ui/button";
import { Field } from "../ui/field";
import { Input } from "../ui/input";

/** The most a removal day can be pushed back at once; the server allows the same. */
const MIN_DAYS = 1;
const MAX_DAYS = 3650;
const DEFAULT_DAYS = 14;

interface RetentionPostponeDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** When it is due now, so the new day can be shown before anything is changed. */
  removeAfterUtc: string | null;
  busy?: boolean;
  onConfirm: (days: number) => void;
}

/**
 * Postpone a Leaving soon title (#1010): push its day back by a number of days
 * without keeping it. It is not exempt, so on the new day it still goes if it
 * still matches. A title already past its day counts from today.
 */
export function RetentionPostponeDialog({ open, onOpenChange, title, removeAfterUtc, busy = false, onConfirm }: RetentionPostponeDialogProps) {
  const { preferences } = useDisplayPreferences();
  const [days, setDays] = useState(String(DEFAULT_DAYS));

  useEffect(() => {
    if (open) setDays(String(DEFAULT_DAYS));
  }, [open]);

  const count = Number(days);
  const valid = Number.isInteger(count) && count >= MIN_DAYS && count <= MAX_DAYS;
  const newDay = (() => {
    if (!valid || !removeAfterUtc) return null;
    const due = new Date(removeAfterUtc).getTime();
    const from = Number.isNaN(due) ? Date.now() : Math.max(due, Date.now());
    return new Date(from + count * 86_400_000).toISOString();
  })();

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl data-[state=open]:animate-fade-in"
          aria-describedby="retention-postpone-description"
        >
          <div className="border-b border-hairline px-6 py-5">
            <div className="flex items-start gap-3">
              <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary/10">
                <Clock className="h-4 w-4 text-primary" />
              </span>
              <div>
                <Dialog.Title className="font-semibold text-foreground">Postpone {title}?</Dialog.Title>
                <Dialog.Description id="retention-postpone-description" className="mt-1 text-(length:--type-body-sm) leading-relaxed text-muted-foreground">
                  Its day moves back. It is not kept, so on the new day the rule still acts on it if it still matches. One already past its day counts from today.
                </Dialog.Description>
              </div>
            </div>
          </div>

          <div className="grid gap-(--grid-gap) p-6">
            <Field
              label="Days to postpone"
              help={newDay ? `Its new day is ${formatDateTime(newDay, preferences)}.` : undefined}
              error={valid ? undefined : `Enter a whole number of days from ${MIN_DAYS} to ${MAX_DAYS}.`}
            >
              <Input type="number" min={MIN_DAYS} max={MAX_DAYS} value={days} onChange={(event) => setDays(event.target.value)} />
            </Field>
          </div>

          <div className="flex justify-end gap-2 border-t border-hairline bg-surface-1/60 px-6 py-4">
            <Dialog.Close asChild>
              <Button type="button" variant="ghost" disabled={busy}>Cancel</Button>
            </Dialog.Close>
            <Button type="button" onClick={() => onConfirm(count)} disabled={busy || !valid}>
              Postpone
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
