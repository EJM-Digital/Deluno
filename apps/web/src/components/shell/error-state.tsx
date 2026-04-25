import * as React from "react";
import { RefreshCw, ExternalLink } from "lucide-react";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { ErrorArt, OfflineArt } from "./illustrations";

interface ErrorStateProps {
  title?: React.ReactNode;
  message?: React.ReactNode;
  /** Show the "offline tower" illustration instead of the broken-film one. */
  variant?: "error" | "offline";
  /** Technical error code displayed as a subtle mono badge. */
  code?: string;
  onRetry?: () => void;
  onReport?: () => void;
  className?: string;
  size?: "default" | "compact";
}

export function ErrorState({
  title = "Something went wrong",
  message = "Deluno couldn't load that area. The server may be unreachable.",
  variant = "error",
  code,
  onRetry,
  onReport,
  className,
  size = "default"
}: ErrorStateProps) {
  return (
    <div
      role="alert"
      className={cn(
        "relative overflow-hidden rounded-2xl border border-destructive/20 bg-destructive/[0.03] dark:bg-destructive/[0.04]",
        size === "compact" ? "px-6 py-10" : "px-8 py-14",
        "flex flex-col items-center justify-center text-center",
        className
      )}
    >
      {/* Ambient destructive halo */}
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0"
        style={{
          background:
            "radial-gradient(ellipse 55% 45% at 50% 40%, hsl(var(--destructive) / 0.08), transparent 60%)"
        }}
      />

      <div className="relative mb-4">
        {variant === "offline" ? (
          <OfflineArt size={size === "compact" ? 112 : 150} />
        ) : (
          <ErrorArt size={size === "compact" ? 112 : 150} />
        )}
      </div>

      <div className="relative z-10 max-w-md space-y-1.5">
        <h3
          className={cn(
            "font-display font-bold tracking-tight text-foreground",
            size === "compact" ? "text-base" : "text-lg"
          )}
        >
          {title}
        </h3>
        <p className="text-balance text-[13px] leading-relaxed text-muted-foreground">
          {message}
        </p>
        {code ? (
          <div className="pt-1">
            <code className="font-mono-code inline-flex items-center gap-1.5 rounded-md border border-hairline bg-surface-1 px-2 py-0.5 text-[10.5px] text-muted-foreground">
              <span className="h-1 w-1 rounded-full bg-destructive" />
              {code}
            </code>
          </div>
        ) : null}
      </div>

      <div className="relative z-10 mt-5 flex flex-wrap items-center justify-center gap-2">
        {onRetry ? (
          <Button variant="default" size="sm" onClick={onRetry} className="gap-1.5">
            <RefreshCw className="h-3.5 w-3.5" />
            Try again
          </Button>
        ) : null}
        {onReport ? (
          <Button variant="ghost" size="sm" onClick={onReport} className="gap-1.5">
            Report issue
            <ExternalLink className="h-3 w-3" />
          </Button>
        ) : null}
      </div>
    </div>
  );
}
