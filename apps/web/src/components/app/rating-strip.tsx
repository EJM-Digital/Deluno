import { Badge } from "../ui/badge";
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
        const content = (
          <div className="rounded-xl border border-hairline bg-surface-1 p-3 transition hover:border-primary/25 hover:bg-surface-2">
            <div className="flex items-center justify-between gap-3">
              <span className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.14em] text-muted-foreground">
                {rating.label}
              </span>
              <Badge variant={badgeVariant(rating.source)}>{rating.kind ?? "rating"}</Badge>
            </div>
            <p className="mt-2 font-display text-[length:var(--type-title-sm)] font-semibold tracking-tight text-foreground">
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
          kind: "community"
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

function badgeVariant(source: string) {
  if (source === "rotten_tomatoes") return "destructive" as const;
  if (source === "imdb") return "warning" as const;
  if (source === "tmdb") return "info" as const;
  return "default" as const;
}
