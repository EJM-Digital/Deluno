import { ArrowUpRight } from "lucide-react";
import { Link } from "react-router-dom";
import type { ReactNode } from "react";
import type { LibraryItem } from "../../lib/api";
import { cn } from "../../lib/utils";

export interface LibraryImpactLink {
  label: string;
  value: ReactNode;
  detail?: ReactNode;
  href: string;
  tone?: "ok" | "info" | "warn" | "muted";
}

const toneClasses: Record<NonNullable<LibraryImpactLink["tone"]>, string> = {
  ok: "border-success/30 bg-success/[0.05]",
  info: "border-info/30 bg-info/[0.05]",
  warn: "border-warning/35 bg-warning/[0.05]",
  muted: "border-hairline bg-surface-1/35"
};

/** Small linked library chips for settings that affect one or more libraries. */
export function LibraryImpactLinks({
  libraries,
  emptyLabel = "Not used by any library yet",
  className
}: {
  libraries: LibraryItem[];
  emptyLabel?: ReactNode;
  className?: string;
}) {
  if (!libraries.length) return <span className={cn("text-muted-foreground", className)}>{emptyLabel}</span>;

  return (
    <span className={cn("flex min-w-0 flex-wrap gap-1.5", className)}>
      {libraries.slice(0, 4).map((library) => (
        <Link
          key={library.id}
          to={`/settings/libraries?libraryId=${encodeURIComponent(library.id)}`}
          onClick={(event) => event.stopPropagation()}
          className="inline-flex min-w-0 max-w-full items-center gap-1 rounded-full border border-hairline bg-surface-1 px-2 py-1 text-[length:var(--type-caption)] font-medium text-foreground transition-colors hover:border-primary/40 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <span className="max-w-36 truncate">{library.name}</span>
          <span className="shrink-0 text-muted-foreground">{library.mediaType === "tv" ? "TV" : "Movies"}</span>
        </Link>
      ))}
      {libraries.length > 4 ? <span className="self-center text-[length:var(--type-caption)] text-muted-foreground">+{libraries.length - 4} more</span> : null}
    </span>
  );
}

/** A consistent reverse-impact map used inside a library drawer. */
export function LibraryImpactPanel({
  title = "Where this library is configured",
  description = "These settings change how Deluno searches for, routes, processes, or imports this library.",
  items,
  className
}: {
  title?: ReactNode;
  description?: ReactNode;
  items: LibraryImpactLink[];
  className?: string;
}) {
  return (
    <section className={cn("grid gap-3 border-b border-hairline py-4", className)} aria-labelledby="library-impact-title">
      <div className="grid gap-1">
        <h3 id="library-impact-title" className="text-[length:var(--type-body-sm)] font-semibold text-foreground">{title}</h3>
        <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{description}</p>
      </div>
      <div className="grid gap-2 sm:grid-cols-2">
        {items.map((item) => (
          <Link
            key={item.label}
            to={item.href}
            className={cn(
              "group grid min-w-0 gap-1 rounded-[10px] border px-3 py-2.5 transition-colors hover:border-primary/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              toneClasses[item.tone ?? "muted"]
            )}
          >
            <span className="flex min-w-0 items-center justify-between gap-2">
              <span className="truncate text-[length:var(--type-caption)] font-semibold uppercase tracking-[0.08em] text-muted-foreground">{item.label}</span>
              <ArrowUpRight aria-hidden className="h-3.5 w-3.5 shrink-0 text-muted-foreground transition-colors group-hover:text-primary" />
            </span>
            <span className="truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{item.value}</span>
            {item.detail !== undefined ? <span className="truncate text-[length:var(--type-caption)] text-muted-foreground">{item.detail}</span> : null}
          </Link>
        ))}
      </div>
    </section>
  );
}
