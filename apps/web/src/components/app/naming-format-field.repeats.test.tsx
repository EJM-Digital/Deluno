import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { NamingFormatField, type NamingFormatKind } from "./naming-format-field";
import { repeatedSentences } from "../../test/repeated-text";

const KINDS: NamingFormatKind[] = [
  "movie-folder", "series-folder", "movie-file", "episode-file", "daily-episode-file", "anime-episode-file", "destination-movie", "destination-series"
];

// #1162: Media Naming printed "Use tokens to create a format that fits your
// library." once per section and the quality line three times.
describe("Media Naming's sections", () => {
  it("do not repeat the same sentence of help", () => {
    const { container } = render(
      <div>
        {KINDS.map((kind) => <NamingFormatField key={kind} kind={kind} value="" onChange={() => undefined} />)}
      </div>
    );

    expect(repeatedSentences(container)).toEqual([]);
  });
});

describe("the example beside a chosen style", () => {
  it("shows the chosen style's example, and leaves it out when the page shows examples itself", () => {
    const { rerender } = render(<NamingFormatField kind="movie-folder" value="{Movie Title} ({Release Year})" onChange={() => undefined} />);
    expect(screen.getAllByText("Blade Runner 2049 (2017)").length).toBeGreaterThan(0);

    rerender(<NamingFormatField kind="movie-folder" value="{Movie Title} ({Release Year})" onChange={() => undefined} showExamples={false} />);
    expect(screen.queryByText("Blade Runner 2049 (2017)")).not.toBeInTheDocument();
  });
});
