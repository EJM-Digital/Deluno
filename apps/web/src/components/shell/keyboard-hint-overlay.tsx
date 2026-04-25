import * as React from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { X } from "lucide-react";
import { Kbd } from "../ui/kbd";
import { cn } from "../../lib/utils";

export interface KeyboardShortcut {
  keys: string[];
  label: string;
  group?: string;
}

interface KeyboardHintOverlayProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  shortcuts: KeyboardShortcut[];
}

export function KeyboardHintOverlay({
  open,
  onOpenChange,
  shortcuts
}: KeyboardHintOverlayProps) {
  const groups = React.useMemo(() => {
    const map = new Map<string, KeyboardShortcut[]>();
    for (const s of shortcuts) {
      const g = s.group ?? "General";
      if (!map.has(g)) map.set(g, []);
      map.get(g)!.push(s);
    }
    return Array.from(map.entries());
  }, [shortcuts]);

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay
          className={cn(
            "fixed inset-0 z-[70] bg-background/70 backdrop-blur-sm animate-fade-in"
          )}
        />
        <Dialog.Content
          className={cn(
            "fixed left-1/2 top-1/2 z-[71] w-[min(92vw,640px)] -translate-x-1/2 -translate-y-1/2",
            "rounded-2xl border border-hairline bg-card shadow-lg animate-slide-up"
          )}
        >
          <div className="flex items-center justify-between border-b border-hairline px-5 py-3">
            <Dialog.Title className="font-display text-base font-semibold text-foreground">
              Keyboard shortcuts
            </Dialog.Title>
            <Dialog.Description className="sr-only">
              A searchable reference of Deluno keyboard shortcuts grouped by area.
            </Dialog.Description>
            <Dialog.Close className="rounded-md p-1 text-muted-foreground transition hover:bg-secondary hover:text-foreground">
              <X className="h-4 w-4" />
              <span className="sr-only">Close</span>
            </Dialog.Close>
          </div>
          <div className="max-h-[70vh] overflow-y-auto px-5 py-4">
            <div className="grid gap-6 sm:grid-cols-2">
              {groups.map(([group, list]) => (
                <div key={group} className="space-y-2">
                  <p className="text-[11px] uppercase tracking-[0.18em] text-muted-foreground">
                    {group}
                  </p>
                  <ul className="space-y-1.5">
                    {list.map((item) => (
                      <li
                        key={`${group}-${item.label}`}
                        className="flex items-center justify-between gap-3 rounded-md px-1.5 py-1 hover:bg-surface-2"
                      >
                        <span className="text-sm text-foreground">
                          {item.label}
                        </span>
                        <span className="flex items-center gap-1">
                          {item.keys.map((k, i) => (
                            <React.Fragment key={`${item.label}-${k}-${i}`}>
                              <Kbd>{k}</Kbd>
                              {i < item.keys.length - 1 ? (
                                <span className="text-xs text-muted-foreground">
                                  then
                                </span>
                              ) : null}
                            </React.Fragment>
                          ))}
                        </span>
                      </li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
