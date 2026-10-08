import type { ReactNode } from "react";
import { cn } from "../../lib/utils";

/**
 * The action buttons in a title page's Ratings & IDs panel (#1058): two to a
 * row, so the panel is no taller than the poster beside it. A last button left
 * alone on its row takes the whole row. One rule for the film page and the show
 * page, so the two cannot drift apart.
 */
export function TitleActionGrid({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className={cn("mt-3 grid grid-cols-2 gap-1.5 [&>*:last-child:nth-child(odd)]:col-span-2 [&>button]:h-auto [&>button]:min-h-(--control-height) [&>button]:whitespace-normal [&>button]:py-1.5 [&>button]:leading-tight", className)}>
      {children}
    </div>
  );
}
