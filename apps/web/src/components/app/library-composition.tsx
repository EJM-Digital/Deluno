/**
 * What the library is actually made of (#270).
 *
 * Four flat counters tell you the totals but not the shape: whether a library
 * is mostly movie or mostly television, and how much of it Deluno is still
 * chasing. A ring answers both at a glance, and each segment links to the list
 * behind it.
 *
 * The segments are the four rungs of the mark (DESIGN-001) — disjoint states of
 * the same catalogue, so the ring always sums to the whole. Every segment also
 * prints its own number, so the drawing is never the only way to read it.
 *
 * Both the words and the colours come from `TITLE_MARK_PRESENTATION`. They used
 * to be written out here: *On disk* in green, *Still missing* in **amber** and
 * *Upgradeable* in blue — three names for states the rest of Deluno already
 * names, one of them spelled differently from the mark it drew ("Upgradeable"
 * against "Upgradable"), and the amber one claiming a person was needed for
 * titles Deluno searches for on its own schedule (#302).
 */
import { Link } from "react-router-dom";
import { CountUp } from "../ui/count-up";
import { TITLE_MARK_PRESENTATION, type TitleMark } from "../../lib/status-tones";
import { cn } from "../../lib/utils";

interface Segment {
  label: string;
  value: number;
  href: string;
  /** The custom property, e.g. `--mark-quality-met`. Taken from the one table. */
  cssVar: string;
}

const SIZE = 120;
const RADIUS = 48;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

export function LibraryComposition({
  covered,
  missing,
  upgradable,
  upcoming = 0,
  movieCount,
  showCount,
  className
}: {
  covered: number;
  missing: number;
  upgradable: number;
  upcoming?: number;
  movieCount: number;
  showCount: number;
  className?: string;
}) {
  const segment = (mark: TitleMark, value: number, href: string): Segment => ({
    label: TITLE_MARK_PRESENTATION[mark].label,
    value: Math.max(0, value),
    href,
    cssVar: TITLE_MARK_PRESENTATION[mark].cssVar
  });

  const segments: Segment[] = [
    segment("covered", covered, "/movies?status=covered"),
    segment("upgrade", upgradable, "/search-cycles/upgrades"),
    segment("missing", missing, "/search-cycles/missing"),
    // Only when there is one. A zero segment draws nothing and still takes a
    // legend row, which is a fourth line saying "no".
    ...(upcoming > 0 ? [segment("upcoming", upcoming, "/movies?status=upcoming")] : [])
  ];

  const total = segments.reduce((sum, segment) => sum + segment.value, 0);

  let offset = 0;
  const arcs = segments.map((segment) => {
    const fraction = total === 0 ? 0 : segment.value / total;
    const arc = { ...segment, fraction, dash: fraction * CIRCUMFERENCE, offset };
    offset += fraction * CIRCUMFERENCE;
    return arc;
  });

  return (
    <section
      className={cn(
        "flex flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]",
        className
      )}
    >
      <header className="flex items-baseline justify-between gap-3 px-[var(--card-pad-x)] pt-3">
        <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
          Library
        </span>
        <span className="text-[length:var(--type-caption)] text-muted-foreground">
          {movieCount.toLocaleString()} movies · {showCount.toLocaleString()} shows
        </span>
      </header>

      <div className="flex flex-1 items-center gap-[var(--grid-gap)] px-[var(--card-pad-x)] py-3">
        <div className="relative shrink-0" style={{ width: SIZE, height: SIZE }}>
          <svg viewBox={`0 0 ${SIZE} ${SIZE}`} className="h-full w-full -rotate-90" aria-hidden>
            <circle
              cx={SIZE / 2}
              cy={SIZE / 2}
              r={RADIUS}
              fill="none"
              stroke="hsl(var(--surface-3))"
              strokeWidth="10"
            />
            {arcs.map((arc) => (
              <circle
                key={arc.label}
                cx={SIZE / 2}
                cy={SIZE / 2}
                r={RADIUS}
                fill="none"
                stroke={`hsl(var(${arc.cssVar}))`}
                strokeWidth="10"
                strokeDasharray={`${arc.dash} ${CIRCUMFERENCE - arc.dash}`}
                strokeDashoffset={-arc.offset}
                className="transition-[stroke-dasharray,stroke-dashoffset] duration-700"
              />
            ))}
          </svg>
          <div className="absolute inset-0 flex flex-col items-center justify-center">
            <span className="font-display text-[length:var(--type-title-sm)] font-semibold tabular-nums leading-none text-foreground">
              <CountUp value={total} />
            </span>
            <span className="mt-0.5 text-[length:var(--type-micro)] text-muted-foreground">
              {total === 1 ? "title" : "titles"}
            </span>
          </div>
        </div>

        <dl className="flex min-w-0 flex-1 flex-col justify-center gap-1">
          {arcs.map((arc) => (
            <Link
              key={arc.label}
              to={arc.href}
              className="group flex min-w-0 items-center gap-2 rounded-lg px-1.5 py-1 transition-colors hover:bg-primary/[0.06] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <span
                aria-hidden
                className="h-2 w-2 shrink-0 rounded-full"
                style={{ backgroundColor: `hsl(var(${arc.cssVar}))` }}
              />
              <dt className="min-w-0 flex-1 truncate text-[length:var(--type-caption)] text-muted-foreground">
                {arc.label}
              </dt>
              <dd className="shrink-0 text-[length:var(--type-body-sm)] font-semibold tabular-nums text-foreground">
                <CountUp value={arc.value} />
              </dd>
            </Link>
          ))}
        </dl>
      </div>
    </section>
  );
}
