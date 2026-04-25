import { Command } from "cmdk";
import * as Dialog from "@radix-ui/react-dialog";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { cn } from "../../lib/utils";
import {
  buildActionCommands,
  navigationCommands,
  settingsCommands,
  type CommandItem
} from "../../lib/command-registry";
import { Kbd } from "../ui/kbd";

const RECENTS_KEY = "deluno-cmd-recents";
const MAX_RECENTS = 5;

function readRecents(): string[] {
  try {
    const raw = localStorage.getItem(RECENTS_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed) ? parsed.filter((x) => typeof x === "string") : [];
  } catch {
    return [];
  }
}

function pushRecent(path: string) {
  const prev = readRecents().filter((p) => p !== path);
  prev.unshift(path);
  localStorage.setItem(RECENTS_KEY, JSON.stringify(prev.slice(0, MAX_RECENTS)));
}

interface CommandPaletteProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  theme?: string;
  onToggleTheme?: () => void;
}

export function CommandPalette({
  open,
  onOpenChange,
  theme,
  onToggleTheme
}: CommandPaletteProps) {
  const navigate = useNavigate();
  const [search, setSearch] = useState("");

  useEffect(() => {
    if (!open) setSearch("");
  }, [open]);

  const actionCommands = useMemo(
    () => buildActionCommands({ onToggleTheme, theme }),
    [onToggleTheme, theme]
  );

  const run = useCallback(
    (item: CommandItem) => {
      if (item.to) {
        navigate(item.to);
        pushRecent(item.to);
      }
      item.perform?.();
      onOpenChange(false);
    },
    [navigate, onOpenChange]
  );

  const recents = useMemo(() => readRecents(), [open]);

  const recentItems = useMemo(() => {
    const labels: Record<string, string> = {};
    for (const c of navigationCommands) {
      if (c.to) labels[c.to] = c.label;
    }
    return recents
      .map((path) => ({ path, label: labels[path] ?? path }))
      .filter((r) => r.label);
  }, [recents]);

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-[85] bg-background/60 backdrop-blur-sm animate-fade-in" />
        <Dialog.Content
          aria-describedby={undefined}
          className={cn(
            "fixed left-1/2 top-[min(12vh,120px)] z-[86] w-[min(92vw,560px)] -translate-x-1/2",
            "rounded-2xl border border-hairline bg-popover/95 p-0 shadow-lg backdrop-blur animate-slide-down"
          )}
        >
          <Dialog.Title className="sr-only">Command palette</Dialog.Title>
          <Command
            className="flex max-h-[min(70vh,520px)] flex-col overflow-hidden rounded-2xl"
            label="Command palette"
            shouldFilter
            loop
          >
            <div className="border-b border-hairline px-3 py-2">
              <Command.Input
                value={search}
                onValueChange={setSearch}
                placeholder="Jump to area, run action…"
                className="w-full border-0 bg-transparent py-2 text-sm text-foreground outline-none placeholder:text-muted-foreground"
              />
            </div>
            <Command.List className="overflow-y-auto p-2">
              <Command.Empty className="px-3 py-6 text-center text-sm text-muted-foreground">
                No matches.
              </Command.Empty>

              {recentItems.length > 0 ? (
                <Command.Group
                  heading="Recents"
                  className="mb-2 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-[0.14em] [&_[cmdk-group-heading]]:text-muted-foreground"
                >
                  {recentItems.map((r) => (
                    <Command.Item
                      key={r.path}
                      value={`recent ${r.label} ${r.path}`}
                      onSelect={() => {
                        navigate(r.path);
                        pushRecent(r.path);
                        onOpenChange(false);
                      }}
                      className="flex cursor-pointer items-center justify-between gap-3 rounded-lg px-2 py-2 text-sm text-foreground aria-selected:bg-surface-2"
                    >
                      <span>{r.label}</span>
                      <span className="font-mono text-xs text-muted-foreground">{r.path}</span>
                    </Command.Item>
                  ))}
                </Command.Group>
              ) : null}

              <Command.Group
                heading="Navigation"
                className="mb-2 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-[0.14em] [&_[cmdk-group-heading]]:text-muted-foreground"
              >
                {navigationCommands.map((item) => {
                  const Icon = item.icon;
                  return (
                    <Command.Item
                      key={item.id}
                      value={`${item.label} ${item.keywords?.join(" ") ?? ""}`}
                      onSelect={() => run(item)}
                      className="flex cursor-pointer items-center gap-3 rounded-lg px-2 py-2 text-sm text-foreground aria-selected:bg-surface-2"
                    >
                      {Icon ? <Icon className="h-4 w-4 shrink-0 text-muted-foreground" /> : null}
                      <span className="flex-1">{item.label}</span>
                      {item.shortcut ? (
                        <span className="flex items-center gap-0.5">
                          {item.shortcut.map((k) => (
                            <Kbd key={k}>{k}</Kbd>
                          ))}
                        </span>
                      ) : null}
                    </Command.Item>
                  );
                })}
              </Command.Group>

              <Command.Group
                heading="Settings"
                className="mb-2 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-[0.14em] [&_[cmdk-group-heading]]:text-muted-foreground"
              >
                {settingsCommands.map((item) => {
                  const Icon = item.icon;
                  return (
                    <Command.Item
                      key={item.id}
                      value={`${item.label} ${item.keywords?.join(" ") ?? ""}`}
                      onSelect={() => run(item)}
                      className="flex cursor-pointer items-center gap-3 rounded-lg px-2 py-2 text-sm text-foreground aria-selected:bg-surface-2"
                    >
                      {Icon ? <Icon className="h-4 w-4 shrink-0 text-muted-foreground" /> : null}
                      <span className="flex-1">{item.label}</span>
                    </Command.Item>
                  );
                })}
              </Command.Group>

              {actionCommands.length > 0 ? (
                <Command.Group
                  heading="Preferences"
                  className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-[0.14em] [&_[cmdk-group-heading]]:text-muted-foreground"
                >
                  {actionCommands.map((item) => {
                    const Icon = item.icon;
                    return (
                      <Command.Item
                        key={item.id}
                        value={`${item.label} ${item.keywords?.join(" ") ?? ""}`}
                        onSelect={() => run(item)}
                        className="flex cursor-pointer items-center gap-3 rounded-lg px-2 py-2 text-sm text-foreground aria-selected:bg-surface-2"
                      >
                        {Icon ? <Icon className="h-4 w-4 shrink-0 text-muted-foreground" /> : null}
                        <span>{item.label}</span>
                      </Command.Item>
                    );
                  })}
                </Command.Group>
              ) : null}
            </Command.List>
          </Command>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
