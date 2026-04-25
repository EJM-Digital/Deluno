/**
 * Small pill that communicates async state beside a form/panel:
 *   · idle    → nothing (renders null)
 *   · syncing → animated spinner + "Syncing…"
 *   · saved   → check + "Saved just now" (auto-fades)
 *   · error   → alert + message
 *
 * Pair with `useSaveStatus()` which exposes `markSyncing`, `markSaved`,
 * `markError` — then wire those to your mutation handlers.
 */

import { useEffect, useRef, useState } from "react";
import { Check, CircleAlert, Loader2 } from "lucide-react";
import { cn } from "../../lib/utils";

export type SaveStatusState = "idle" | "syncing" | "saved" | "error";

export function useSaveStatus(resetAfterMs = 2200) {
  const [state, setState] = useState<SaveStatusState>("idle");
  const [message, setMessage] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    return () => {
      if (timer.current !== null) window.clearTimeout(timer.current);
    };
  }, []);

  function clear() {
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
      timer.current = null;
    }
  }

  return {
    state,
    message,
    markSyncing(msg?: string) {
      clear();
      setMessage(msg ?? null);
      setState("syncing");
    },
    markSaved(msg?: string) {
      clear();
      setMessage(msg ?? "Saved");
      setState("saved");
      timer.current = window.setTimeout(() => setState("idle"), resetAfterMs);
    },
    markError(msg: string) {
      clear();
      setMessage(msg);
      setState("error");
    },
    reset() {
      clear();
      setMessage(null);
      setState("idle");
    }
  };
}

interface SaveStatusProps {
  state: SaveStatusState;
  message?: string | null;
  className?: string;
}

export function SaveStatus({ state, message, className }: SaveStatusProps) {
  if (state === "idle") return null;

  const config = {
    syncing: {
      icon: Loader2,
      spin: true,
      tone: "text-muted-foreground",
      label: message ?? "Syncing…"
    },
    saved: {
      icon: Check,
      spin: false,
      tone: "text-success",
      label: message ?? "Saved just now"
    },
    error: {
      icon: CircleAlert,
      spin: false,
      tone: "text-destructive",
      label: message ?? "Could not save"
    }
  }[state];

  const Icon = config.icon;

  return (
    <span
      role="status"
      aria-live="polite"
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border border-hairline bg-card/70 px-2.5 py-1 text-[11px] font-medium",
        "dark:border-white/[0.06] dark:bg-white/[0.03]",
        config.tone,
        className
      )}
    >
      <Icon className={cn("h-3 w-3", config.spin && "animate-spin")} strokeWidth={2.25} />
      {config.label}
    </span>
  );
}
