import { Sparkles } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../../lib/utils";

export interface PageHeroStat {
  label: string;
  value: string;
  tone?: "primary" | "success" | "warn" | "danger" | "neutral";
}

interface PageHeroProps {
  /** Small eyebrow label above the title */
  eyebrow?: string;
  eyebrowIcon?: ReactNode;
  /** Large editorial title, supports ReactNode for gradient highlights */
  title: ReactNode;
  /** Short descriptive subtitle (one-liner) */
  subtitle?: ReactNode;
  /** Optional action buttons row */
  actions?: ReactNode;
  /** Right-column stats grid (up to 4 items recommended) */
  stats?: PageHeroStat[];
  /** Optional poster backdrop array (8 posters stitched as mosaic) */
  posters?: string[];
  /** Custom className for container */
  className?: string;
  /** Height variant */
  size?: "sm" | "md";
}

/**
 * Editorial page hero: poster-mosaic backdrop + operational typography + stats.
 * Used at the top of every major page in the app shell.
 */
export function PageHero({
  eyebrow,
  eyebrowIcon,
  title,
  subtitle,
  actions,
  stats,
  posters,
  className,
  size = "md"
}: PageHeroProps) {
  return (
    <section
      className={cn(
        "relative overflow-hidden rounded-[24px] border border-hairline bg-card shadow-lg dark:border-white/[0.06]",
        className
      )}
    >
      {/* Poster mosaic backdrop (if provided) */}
      {posters && posters.length > 0 ? (
        <div aria-hidden className="absolute inset-0">
          <div
            className="absolute inset-0 grid gap-px opacity-[0.35] dark:opacity-[0.5]"
            style={{ gridTemplateColumns: `repeat(${Math.min(posters.length, 8)}, 1fr)` }}
          >
            {posters.slice(0, 8).map((p, i) => (
              <div
                key={i}
                className="h-full"
                style={{
                  backgroundImage: `url(${p})`,
                  backgroundSize: "cover",
                  backgroundPosition: "center"
                }}
              />
            ))}
          </div>
          <div className="absolute inset-0 backdrop-blur-[60px] backdrop-saturate-150" />
        </div>
      ) : null}

      {/* Gradient wash — always present */}
      <div
        aria-hidden
        className="absolute inset-0 bg-gradient-to-br from-background/75 via-background/88 to-background/96 dark:from-black/65 dark:via-black/82 dark:to-black/95"
      />
      <div
        aria-hidden
        className="absolute inset-0 bg-[radial-gradient(ellipse_70%_55%_at_15%_10%,hsl(var(--primary)/0.18),transparent_70%)] dark:bg-[radial-gradient(ellipse_70%_55%_at_15%_10%,hsl(var(--primary)/0.22),transparent_72%)]"
      />

      <div
        className={cn(
          "relative grid gap-[var(--grid-gap)]",
          stats && stats.length > 0
            ? "hero-layout-with-stats"
            : "",
          size === "sm" ? "p-[var(--tile-pad)] md:p-[calc(var(--tile-pad)*1.15)]" : "p-[calc(var(--tile-pad)*1.1)] md:p-[calc(var(--tile-pad)*1.35)] lg:p-[calc(var(--tile-pad)*1.5)]"
        )}
      >
        <div className="flex flex-col justify-between gap-5">
          <div>
            {eyebrow ? (
              <div className="inline-flex items-center gap-2 rounded-full border border-primary/25 bg-primary/10 px-2.5 py-1 backdrop-blur-sm dark:border-primary/20">
                {eyebrowIcon ?? <Sparkles className="h-3 w-3 text-primary" />}
                <span className="text-[11px] font-semibold tracking-wide text-primary">
                  {eyebrow}
                </span>
              </div>
            ) : null}

            <h1
              className={cn(
                "mt-4 text-balance font-display font-bold leading-[0.98] tracking-display text-foreground",
                size === "sm"
                  ? "text-[32px] sm:text-[40px] md:text-[44px]"
                  : "text-[36px] sm:text-[48px] md:text-[56px] lg:text-[64px]"
              )}
            >
              {title}
            </h1>

            {subtitle ? (
              <p
                className={cn(
                  "mt-3 max-w-[min(68rem,100%)] text-balance leading-relaxed text-muted-foreground",
                  size === "sm" ? "text-[14px]" : "text-[15px]"
                )}
              >
                {subtitle}
              </p>
            ) : null}
          </div>

          {actions ? <div className="flex min-w-0 flex-wrap items-center gap-2">{actions}</div> : null}
        </div>

        {stats && stats.length > 0 ? (
          <div className="relative flex flex-col justify-center rounded-2xl border border-hairline bg-card/60 p-[calc(var(--tile-pad)*0.8)] backdrop-blur-md dark:border-white/[0.08] dark:bg-white/[0.03]">
            <div className="hero-stat-grid">
              {stats.map((stat, i) => (
                <HeroStatTile key={i} {...stat} />
              ))}
            </div>
          </div>
        ) : null}
      </div>
    </section>
  );
}

function HeroStatTile({ label, value, tone = "neutral" }: PageHeroStat) {
  const valueClass = {
    primary: "text-primary",
    success: "text-success",
    warn: "text-warning",
    danger: "text-destructive",
    neutral: "text-foreground"
  }[tone];

  return (
    <div className="min-w-0 rounded-xl border border-hairline bg-muted/30 px-[calc(var(--tile-pad)*0.55)] py-[calc(var(--tile-pad)*0.42)] dark:border-white/[0.05] dark:bg-white/[0.02]">
      <p className="density-nowrap text-[length:var(--type-micro)] font-bold uppercase tracking-[0.16em] text-muted-foreground/70">
        {label}
      </p>
      <p
        className={cn(
          "density-nowrap mt-0.5 tabular font-display text-[length:var(--type-title-md)] font-bold leading-tight tracking-display",
          valueClass
        )}
      >
        {value}
      </p>
    </div>
  );
}

/**
 * Glass tile — elevated surface with gradient top-hairline. Base for all content panels.
 */
export function GlassTile({
  className,
  children,
  ...rest
}: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        "relative overflow-hidden rounded-2xl border border-hairline bg-card shadow-card",
        "before:pointer-events-none before:absolute before:inset-x-0 before:top-0 before:h-px",
        "before:bg-gradient-to-r before:from-transparent before:via-hairline before:to-transparent",
        "dark:border-white/[0.06]",
        className
      )}
      {...rest}
    >
      {children}
    </div>
  );
}

/**
 * A small stat chip — used for top-of-section summaries.
 */
export function StatChip({
  label,
  value,
  tone = "neutral",
  icon
}: {
  label: string;
  value: string | number;
  tone?: "primary" | "success" | "warn" | "danger" | "neutral";
  icon?: ReactNode;
}) {
  const toneClass = {
    primary: "bg-primary/10 text-primary border-primary/20",
    success: "bg-success/10 text-success border-success/20",
    warn: "bg-warning/10 text-warning border-warning/20",
    danger: "bg-destructive/10 text-destructive border-destructive/20",
    neutral: "bg-muted/40 text-foreground border-hairline dark:bg-white/[0.04]"
  }[tone];

  return (
    <div
      className={cn(
        "inline-flex items-center gap-2 rounded-full border px-3 py-1",
        toneClass
      )}
    >
      {icon}
      <span className="density-nowrap text-[length:var(--type-micro)] font-semibold uppercase tracking-wide opacity-75">
        {label}
      </span>
      <span className="density-nowrap tabular text-[length:var(--type-caption)] font-bold">{value}</span>
    </div>
  );
}
