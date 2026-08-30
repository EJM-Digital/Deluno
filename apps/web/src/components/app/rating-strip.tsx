import type { MetadataRatingItem } from "../../lib/api";

interface RatingStripProps {
  ratings?: MetadataRatingItem[] | null;
  fallbackRating?: number | null;
}

/**
 * A colour per source, because the point of showing four scores is telling them
 * apart at a glance (#319). Two of them had a tone and the other two shared the
 * default, so Rotten Tomatoes and Metacritic — the two most likely to disagree
 * with the rest — were the two that looked identical.
 *
 * The associations are the ones each site uses itself, so the card reads the way
 * the source does: TMDb blue, IMDb amber, Rotten Tomatoes red, Metacritic
 * yellow-green.
 */
const SOURCE_TONES: Record<string, string> = {
  tmdb: "border-sky-400/30 bg-gradient-to-br from-sky-500/15 via-surface-1 to-surface-1",
  imdb: "border-amber-400/30 bg-gradient-to-br from-amber-500/15 via-surface-1 to-surface-1",
  rotten_tomatoes: "border-red-400/30 bg-gradient-to-br from-red-500/15 via-surface-1 to-surface-1",
  metacritic: "border-lime-400/30 bg-gradient-to-br from-lime-500/15 via-surface-1 to-surface-1"
};

export function RatingStrip({ ratings, fallbackRating }: RatingStripProps) {
  const visibleRatings = normalizeRatings(ratings, fallbackRating);

  if (visibleRatings.length === 0) {
    return (
      <div className="rounded-xl border border-hairline bg-surface-1 p-3 text-[length:var(--type-body-sm)] text-muted-foreground">
        No ratings stored yet. Refresh metadata after provider setup to add rating sources.
      </div>
    );
  }

  return (
    <div className="grid gap-2 sm:grid-cols-2">
      {visibleRatings.map((rating) => {
        const value = formatRating(rating);
        const tone = SOURCE_TONES[rating.source] ?? "border-hairline bg-surface-1";
        const content = (
          <div className={`rounded-xl border p-3 transition hover:border-primary/35 hover:bg-surface-2 ${tone}`}>
            <div className="flex items-center justify-between gap-3">
              <span className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.14em] text-muted-foreground">
                {rating.label}
              </span>
              <span className="h-2 w-2 rounded-full bg-primary shadow-[0_0_12px_hsl(var(--primary)/0.8)]" aria-hidden="true" />
            </div>
            <p className="mt-2 font-display text-2xl font-semibold tracking-tight text-foreground">
              {value}
            </p>
            {rating.voteCount ? (
              <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">
                {rating.voteCount.toLocaleString()} votes
              </p>
            ) : null}
          </div>
        );

        return rating.url ? (
          <a key={`${rating.source}-${rating.label}`} href={rating.url} target="_blank" rel="noreferrer" className="no-underline">
            {content}
          </a>
        ) : (
          <div key={`${rating.source}-${rating.label}`}>{content}</div>
        );
      })}
    </div>
  );
}

/**
 * The same scores, on one line.
 *
 * <p>{@link RatingStrip} is a grid of cards, sized for the narrow aside it was
 * built for. Dropped into a header row it dominated the title, pushed the chips
 * down and left the certification badge floating beside a box — James: <i>"its
 * all cock eyed and out of alignment"</i>. That is not a fault in the strip; it
 * is a card being asked to be a line.</p>
 *
 * <p>So this is the line. Same sources, same order, same formatting — it reads
 * `normalizeRatings` and `formatRating` rather than keeping its own copy, so the
 * two can never disagree about what a score says.</p>
 */
export function RatingLine({ ratings, fallbackRating }: RatingStripProps) {
  const visible = normalizeRatings(ratings, fallbackRating);
  if (visible.length === 0) return null;

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
      {visible.map((rating) => (
        <span key={rating.source} className="flex items-baseline gap-1.5 whitespace-nowrap">
          <span className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.12em] text-muted-foreground">
            {rating.label}
          </span>
          <span className="text-sm font-semibold text-foreground">{formatRating(rating)}</span>
        </span>
      ))}
    </div>
  );
}

function normalizeRatings(ratings?: MetadataRatingItem[] | null, fallbackRating?: number | null) {
  if (ratings?.length) {
    return ratings.filter((rating) => rating.score !== null || rating.voteCount !== null);
  }

  return fallbackRating === null || fallbackRating === undefined
    ? []
    : [
        {
          source: "tmdb",
          label: "TMDb",
          score: fallbackRating,
          maxScore: 10,
          voteCount: null,
          url: null,
          kind: null
        }
      ];
}

function formatRating(rating: MetadataRatingItem) {
  if (rating.score === null || rating.score === undefined) {
    return "Unknown";
  }

  if (rating.source === "rotten_tomatoes" || rating.source === "metacritic" || rating.maxScore === 100) {
    return `${Math.round(rating.score)}%`;
  }

  if (rating.maxScore) {
    return `${rating.score.toFixed(1)}/${rating.maxScore.toLocaleString()}`;
  }

  return rating.score.toFixed(1);
}
