/**
 * The dashboard's opening statement (#270).
 *
 * A single pane of glass should say how the whole system is, in one glance,
 * before it says anything else. This is that sentence: a state, and the four
 * counts that describe the library — rendered at a size you can read from
 * across the room.
 *
 * It used to carry a throughput strip as well — a live trace with its own
 * reading. That went (#289): six seconds of history answers no question anyone
 * has, it started empty on every page load, and while idle it said "Idle" about
 * a foot above a Download speed card saying the same thing (#276). Speed is now
 * one surface, below, carrying both directions and its own history. Every count
 * here is a link into the page that owns it, and all motion stops under
 * `prefers-reduced-motion`.
 */
import { Link } from "react-router-dom";
import { CountUp } from "../ui/count-up";
import { StatusLed, type LedTone } from "../ui/status-led";
import { cn } from "../../lib/utils";

interface HeroStat {
  label: string;
  value: number;
  help: string;
  href: string;
  tone?: "default" | "warn";
}

export function DashboardHero({
  headline,
  detail,
  tone,
  stats,
  action
}: {
  /** The one-line state of the system, already decided by the caller. */
  headline: string;
  detail: string;
  tone: LedTone;
  stats: HeroStat[];
  /**
   * The one thing to do next, when there is one. An empty library used to
   * offer "Add a movie" only from the Recently added card at the bottom of the
   * page, so hiding that card while empty left a first-run dashboard with
   * nothing to press (#270). The call to action belongs beside the sentence
   * telling you the library is empty, not three bands below it.
   */
  action?: { label: string; to: string };
}) {
  return (
    <section
      aria-label="Overview"
      className={cn(
        "relative isolate overflow-hidden rounded-2xl border bg-card shadow-card",
        tone === "danger"
          ? "border-destructive/30"
          : tone === "warn"
            ? "border-warning/30"
            : "border-hairline dark:border-white/[0.07]"
      )}
    >
      {/* Atmosphere: one wash keyed to the system state. */}
      <span
        aria-hidden
        className={cn(
          "pointer-events-none absolute -left-32 -top-40 -z-10 h-[28rem] w-[28rem] rounded-full blur-[110px]",
          tone === "danger" ? "bg-destructive/15" : tone === "warn" ? "bg-warning/15" : "bg-primary/10"
        )}
      />

      <div className="relative p-[var(--card-pad-x)]">
        <div className="min-w-0">
          <span className="flex items-center gap-2">
            <StatusLed tone={tone} size={9} pulse={tone === "ok"} />
            <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.14em] text-muted-foreground">
              System
            </span>
          </span>
          <h2 className="mt-1.5 font-display text-[length:var(--type-title-md)] font-semibold leading-tight tracking-tight text-foreground">
            {headline}
          </h2>
          <p className="mt-1 max-w-2xl text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">
            {detail}
          </p>
          {action ? (
            <Link
              to={action.to}
              className="mt-3 inline-flex h-[var(--control-height)] items-center gap-1.5 rounded-[10px] border border-hairline bg-surface-1 px-3 text-[length:var(--type-body-sm)] font-medium text-foreground transition-colors hover:border-primary/40 hover:bg-primary/[0.06] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {action.label}
            </Link>
          ) : null}
        </div>
      </div>

      <div className="relative grid grid-cols-2 gap-px border-t border-hairline bg-hairline/70 sm:grid-cols-4 dark:bg-white/[0.06]">
        {stats.map((stat) => (
          <Link
            key={stat.label}
            to={stat.href}
            className="group flex flex-col gap-0.5 bg-card px-[var(--card-pad-x)] py-3 transition-colors hover:bg-primary/[0.06] focus-visible:relative focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
          >
            <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.12em] text-muted-foreground">
              {stat.label}
            </span>
            <span
              className={cn(
                "font-display text-[length:var(--type-title-md)] font-semibold tabular-nums leading-none tracking-[-0.03em]",
                stat.tone === "warn" ? "text-warning" : "text-foreground"
              )}
            >
              <CountUp value={stat.value} />
            </span>
            <span className="truncate text-[length:var(--type-caption)] text-muted-foreground">{stat.help}</span>
          </Link>
        ))}
      </div>
    </section>
  );
}
