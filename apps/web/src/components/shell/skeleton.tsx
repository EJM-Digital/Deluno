import * as React from "react";
import { cn } from "../../lib/utils";

export function Skeleton({
  className,
  ...props
}: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        "relative overflow-hidden rounded-md bg-surface-2",
        "before:absolute before:inset-0 before:-translate-x-full before:bg-gradient-to-r before:from-transparent before:via-white/5 before:to-transparent before:animate-shimmer",
        className
      )}
      {...props}
    />
  );
}

export function RowSkeleton({ count = 4 }: { count?: number }) {
  return (
    <div className="space-y-2">
      {Array.from({ length: count }).map((_, i) => (
        <div
          key={i}
          className="flex items-center gap-3 rounded-xl border border-hairline bg-card p-3"
        >
          <Skeleton className="h-8 w-8 rounded-full" />
          <div className="flex-1 space-y-2">
            <Skeleton className="h-3 w-1/3" />
            <Skeleton className="h-3 w-2/3" />
          </div>
          <Skeleton className="h-6 w-16 rounded-full" />
        </div>
      ))}
    </div>
  );
}

export function CardSkeleton() {
  return (
    <div className="space-y-3 rounded-2xl border border-hairline bg-card p-4">
      <Skeleton className="h-4 w-1/3" />
      <Skeleton className="h-8 w-1/2" />
      <Skeleton className="h-3 w-2/3" />
    </div>
  );
}

export function PosterSkeleton() {
  return (
    <div className="space-y-2">
      <Skeleton className="aspect-[2/3] w-full rounded-xl" />
      <Skeleton className="h-3 w-2/3" />
      <Skeleton className="h-2.5 w-1/3" />
    </div>
  );
}

/**
 * Full-route skeleton — used as `HydrateFallback` / suspense fallback
 * on every lazy-loaded page. Mimics the standard page shape: hero,
 * KPI row, primary content tile, right rail.
 */
export function RouteSkeleton() {
  return (
    <div className="space-y-[var(--page-gap)]" aria-busy="true" aria-live="polite">
      <div className="space-y-3">
        <Skeleton className="h-3 w-32" />
        <Skeleton className="h-9 w-2/3 max-w-xl" />
        <Skeleton className="h-3 w-1/2 max-w-md" />
      </div>

      <div className="fluid-kpi-grid">
        {Array.from({ length: 4 }).map((_, i) => (
          <CardSkeleton key={`route-card-${i}`} />
        ))}
      </div>

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.45fr)_minmax(min(100%,var(--hero-stat-panel-min)),0.85fr)] 2xl:grid-cols-[minmax(0,1.65fr)_minmax(min(100%,calc(var(--hero-stat-panel-min)+40px)),0.7fr)]">
        <div className="space-y-3 rounded-2xl border border-hairline bg-card p-5 dark:border-white/[0.06]">
          <Skeleton className="h-4 w-1/3" />
          <Skeleton className="h-3 w-1/2" />
          <div className="mt-3 space-y-2">
            <RowSkeleton count={5} />
          </div>
        </div>
        <div className="space-y-3">
          <CardSkeleton />
          <CardSkeleton />
        </div>
      </div>
    </div>
  );
}

/** Poster-grid skeleton for the library view. */
export function LibraryGridSkeleton({ count = 18 }: { count?: number }) {
  return (
    <div
      className="dashboard-poster-grid"
      aria-busy="true"
      aria-live="polite"
    >
      {Array.from({ length: count }).map((_, i) => (
        <PosterSkeleton key={`poster-sk-${i}`} />
      ))}
    </div>
  );
}
