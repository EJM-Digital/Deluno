import * as React from "react";
import { cn } from "../../lib/utils";
import {
  EmptyLibraryArt,
  NoResultsArt,
  OfflineArt
} from "./illustrations";

export type EmptyStateVariant = "library" | "search" | "offline" | "custom";

interface EmptyStateProps {
  /** Selects a built-in illustration. Use `custom` to pass your own via `icon`. */
  variant?: EmptyStateVariant;
  /** Legacy lucide-style icon fallback (used when variant is `custom` and no art given). */
  icon?: React.ComponentType<{ className?: string }>;
  /** Override the illustration with any node (e.g. a branded SVG). */
  art?: React.ReactNode;
  title: React.ReactNode;
  description?: React.ReactNode;
  /** Primary "recovery" action. */
  action?: React.ReactNode;
  /** Secondary, less prominent action (usually a link). */
  secondaryAction?: React.ReactNode;
  learnMore?: React.ReactNode;
  className?: string;
  /** Smaller inline variant for panel contexts. @default "default" */
  size?: "default" | "compact" | "sm";
}

export function EmptyState({
  variant = "library",
  icon: Icon,
  art,
  title,
  description,
  action,
  secondaryAction,
  learnMore,
  size = "default",
  className
}: EmptyStateProps) {
  const isSmall = size === "compact" || size === "sm";
  const illoSize = size === "sm" ? 88 : size === "compact" ? 112 : 150;
  const illustration =
    art ?? (
      variant === "library" ? (
        <EmptyLibraryArt size={illoSize} />
      ) : variant === "search" ? (
        <NoResultsArt size={illoSize} />
      ) : variant === "offline" ? (
        <OfflineArt size={illoSize} />
      ) : Icon ? (
        <div className="flex h-14 w-14 items-center justify-center rounded-2xl bg-gradient-to-br from-primary/15 to-[hsl(var(--primary-2))/0.12] text-primary ring-1 ring-primary/15">
          <Icon className="h-6 w-6" />
        </div>
      ) : null
    );

  return (
    <div
      className={cn(
        "relative overflow-hidden rounded-2xl border border-dashed border-hairline/80 bg-card/40 dark:bg-white/[0.015]",
        size === "sm" ? "px-4 py-6" : isSmall ? "px-6 py-10" : "px-8 py-14",
        "flex flex-col items-center justify-center text-center",
        className
      )}
    >
      {/* Soft radial accent wash */}
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0 opacity-60"
        style={{
          background:
            "radial-gradient(ellipse 60% 50% at 50% 35%, hsl(var(--primary) / 0.06), transparent 60%)"
        }}
      />

      {illustration ? (
        <div className="relative mb-4">{illustration}</div>
      ) : null}

      <div className="relative z-10 max-w-md space-y-1.5">
        <h3
          className={cn(
            "font-display font-bold tracking-tight text-foreground",
            size === "sm" ? "text-sm" : size === "compact" ? "text-base" : "text-lg"
          )}
        >
          {title}
        </h3>
        {description ? (
          <p className="text-balance text-[13px] leading-relaxed text-muted-foreground">
            {description}
          </p>
        ) : null}
      </div>

      {action || secondaryAction ? (
        <div className="relative z-10 mt-5 flex flex-wrap items-center justify-center gap-2">
          {action}
          {secondaryAction}
        </div>
      ) : null}

      {learnMore ? (
        <div className="relative z-10 mt-3 text-[11.5px] text-muted-foreground/80">
          {learnMore}
        </div>
      ) : null}
    </div>
  );
}
