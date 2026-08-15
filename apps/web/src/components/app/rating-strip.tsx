import type { MetadataRatingItem } from "../../lib/api";

interface RatingStripProps {
  ratings?: MetadataRatingItem[] | null;
  fallbackRating?: number | null;
}

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
        const sourceTone = rating.source === "tmdb"
          ? "border-sky-400/30 bg-gradient-to-br from-sky-500/15 via-surface-1 to-surface-1"
          : rating.source === "imdb"
            ? "border-amber-400/30 bg-gradient-to-br from-amber-500/15 via-surface-1 to-surface-1"
            : "border-hairline bg-surface-1";
        const content = (
          <div className={`rounded-xl border p-3 transition hover:border-primary/35 hover:bg-surface-2 ${sourceTone}`}>
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
