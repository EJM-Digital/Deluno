import * as React from "react";
import { cn } from "../../lib/utils";

export type Severity = "ok" | "info" | "warn" | "danger" | "neutral";

export function AttentionDot({
  severity = "neutral",
  pulse = false,
  className
}: {
  severity?: Severity;
  pulse?: boolean;
  className?: string;
}) {
  const color =
    severity === "danger"
      ? "bg-state-danger"
      : severity === "warn"
        ? "bg-state-warn"
        : severity === "ok"
          ? "bg-state-ok"
          : severity === "info"
            ? "bg-state-info"
            : "bg-muted-foreground/50";

  return (
    <span
      aria-hidden="true"
      className={cn(
        "inline-block h-1.5 w-1.5 rounded-full",
        color,
        pulse && "animate-pulse",
        className
      )}
    />
  );
}

export function AttentionBadge({
  count,
  severity = "neutral",
  className
}: {
  count: number;
  severity?: Severity;
  className?: string;
}) {
  if (count <= 0) return null;
  const tone =
    severity === "danger"
      ? "bg-state-danger/15 text-state-danger ring-state-danger/30"
      : severity === "warn"
        ? "bg-state-warn/15 text-state-warn ring-state-warn/30"
        : severity === "ok"
          ? "bg-state-ok/15 text-state-ok ring-state-ok/30"
          : severity === "info"
            ? "bg-state-info/15 text-state-info ring-state-info/30"
            : "bg-surface-2 text-muted-foreground ring-hairline";
  return (
    <span
      className={cn(
        "tabular inline-flex min-w-[1.25rem] items-center justify-center rounded-full px-1.5 text-[10px] font-semibold leading-none ring-1 ring-inset",
        tone,
        className
      )}
    >
      {count > 99 ? "99+" : count}
    </span>
  );
}

export function StatusPill({
  severity = "neutral",
  children,
  className
}: {
  severity?: Severity;
  children: React.ReactNode;
  className?: string;
}) {
  const tone =
    severity === "danger"
      ? "border-state-danger/25 bg-state-danger/10 text-state-danger"
      : severity === "warn"
        ? "border-state-warn/25 bg-state-warn/10 text-state-warn"
        : severity === "ok"
          ? "border-state-ok/25 bg-state-ok/10 text-state-ok"
          : severity === "info"
            ? "border-state-info/25 bg-state-info/10 text-state-info"
            : "border-hairline bg-surface-2 text-muted-foreground";

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[11px] font-medium",
        tone,
        className
      )}
    >
      <AttentionDot severity={severity} />
      {children}
    </span>
  );
}
