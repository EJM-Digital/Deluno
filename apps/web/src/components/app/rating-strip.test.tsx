import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { MetadataRatingItem } from "../../lib/api";
import { RatingLine, RatingStrip } from "./rating-strip";

const rating = (over: Partial<MetadataRatingItem>): MetadataRatingItem => ({
  source: "tmdb", label: "TMDb", score: 7.84, maxScore: 10, voteCount: null, url: null, kind: null, ...over
});

describe("the rating cards", () => {
  it("says no ratings are stored when there are none and no fallback", () => {
    const { rerender } = render(<RatingStrip ratings={[]} />);
    expect(screen.getByText(/No ratings stored yet/)).toBeTruthy();
    rerender(<RatingStrip ratings={null} fallbackRating={null} />);
    expect(screen.getByText(/No ratings stored yet/)).toBeTruthy();
    rerender(<RatingStrip ratings={[rating({ score: null, voteCount: null })]} />);
    expect(screen.getByText(/No ratings stored yet/)).toBeTruthy();
  });

  it("shows the fallback score as TMDb when no source rated it", () => {
    render(<RatingStrip fallbackRating={6.5} />);
    expect(screen.getByText("6.5/10")).toBeTruthy();
    expect(screen.getByText("TMDB")).toBeTruthy();
  });

  it("writes each source the way the source does, with votes and a link only when it has them", () => {
    render(
      <RatingStrip
        ratings={[
          rating({ source: "tmdb", score: 7.84, maxScore: 10, voteCount: 12345, url: "https://tmdb.example/1" }),
          rating({ source: "rotten_tomatoes", label: "Rotten Tomatoes", score: 91.4, maxScore: 100 }),
          rating({ source: "metacritic", label: "Metacritic", score: 70, maxScore: null }),
          rating({ source: "other100", label: "Other 100", score: 55.5, maxScore: 100 }),
          rating({ source: "plain", label: "Plain", score: 3.14159, maxScore: null }),
          rating({ source: "novote", label: "No score", score: null, voteCount: 5 })
        ]}
      />
    );
    expect(screen.getByText("7.8/10")).toBeTruthy();
    expect(screen.getByText("12,345 votes")).toBeTruthy();
    expect(screen.getByText("91%")).toBeTruthy();
    expect(screen.getByText("70%")).toBeTruthy();
    expect(screen.getByText("56%")).toBeTruthy();
    expect(screen.getByText("3.1")).toBeTruthy();
    expect(screen.getByText("Unknown")).toBeTruthy();
    const link = screen.getByRole("link");
    expect(link.getAttribute("href")).toBe("https://tmdb.example/1");
    expect(link.getAttribute("target")).toBe("_blank");
    expect(screen.getAllByRole("link")).toHaveLength(1);
  });
});

describe("the rating line", () => {
  it("draws nothing when nothing rated the title", () => {
    const { container } = render(<RatingLine ratings={[]} fallbackRating={undefined} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("lists the same scores on one line", () => {
    render(<RatingLine ratings={[rating({}), rating({ source: "imdb", label: "IMDb", score: 8, maxScore: 10 })]} />);
    expect(screen.getByText("7.8/10")).toBeTruthy();
    expect(screen.getByText("8.0/10")).toBeTruthy();
  });

  it("falls back to the TMDb score", () => {
    render(<RatingLine fallbackRating={9} />);
    expect(screen.getByText("9.0/10")).toBeTruthy();
  });
});
