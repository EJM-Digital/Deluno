import * as Dialog from "../ui/dialog-primitives";
import { ShieldCheck } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "../ui/button";
import { Field } from "../ui/field";
import { SegmentedControl } from "../ui/segmented-control";
import { Textarea } from "../ui/textarea";

export interface KeepChoice {
  scope: "rule" | "all";
  note: string;
}

interface RetentionKeepDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  ruleName: string;
  busy?: boolean;
  onConfirm: (choice: KeepChoice) => void;
}

/**
 * Keep a Leaving soon title (#494). James: the choice is made each time - from
 * the rule that caught it, or from every rule - with an optional note that
 * shows wherever the title is shown as kept.
 */
export function RetentionKeepDialog({ open, onOpenChange, title, ruleName, busy = false, onConfirm }: RetentionKeepDialogProps) {
  const [scope, setScope] = useState<KeepChoice["scope"]>("rule");
  const [note, setNote] = useState("");

  useEffect(() => {
    if (!open) return;
    setScope("rule");
    setNote("");
  }, [open]);

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl data-[state=open]:animate-fade-in"
          aria-describedby="retention-keep-description"
        >
          <div className="border-b border-hairline px-6 py-5">
            <div className="flex items-start gap-3">
              <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary/10">
                <ShieldCheck className="h-4 w-4 text-primary" />
              </span>
              <div>
                <Dialog.Title className="font-semibold text-foreground">Keep {title}?</Dialog.Title>
                <Dialog.Description id="retention-keep-description" className="mt-1 text-(length:--type-body-sm) leading-relaxed text-muted-foreground">
                  It leaves Leaving soon now and stays until you undo the keep.
                </Dialog.Description>
              </div>
            </div>
          </div>

          <div className="grid gap-(--grid-gap) p-6">
            <Field label="Keep it from" help={scope === "rule" ? `It stays off that rule's list, even while the rule still matches it. Another rule can still mark it later.` : "No rule will mark it while it is kept."}>
              <SegmentedControl
                value={scope}
                onValueChange={setScope}
                options={[
                  { value: "rule", label: `“${ruleName}”` },
                  { value: "all", label: "Every rule" }
                ]}
              />
            </Field>
            <Field label="Note" optional help="Shown with the keep, so the reason is there later.">
              <Textarea value={note} onChange={(event) => setNote(event.target.value)} placeholder="Kids' favourite" maxLength={300} />
            </Field>
          </div>

          <div className="flex justify-end gap-2 border-t border-hairline bg-surface-1/60 px-6 py-4">
            <Dialog.Close asChild>
              <Button type="button" variant="ghost" disabled={busy}>Cancel</Button>
            </Dialog.Close>
            <Button type="button" onClick={() => onConfirm({ scope, note })} disabled={busy}>
              Keep
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
