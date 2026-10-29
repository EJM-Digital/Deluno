import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { Band } from "./band";

afterEach(cleanup);

const HOUR = 3_600_000;
const episode = (n: number) => ({
  seriesId: `s${n}`,
  title: `Show ${n}`,
  startYear: 2024,
  posterUrl: null,
  episodeId: `e${n}`,
  seasonNumber: 1,
  episodeNumber: n,
  episodeTitle: `Episode ${n}`,
  airDateUtc: new Date(Date.now() + n * HOUR).toISOString()
}) as never;

describe("Live top band: Next", () => {
  it("shows up to three \"then\" lines under the first figure, each with its time and a link to the episode", () => {
    const upcoming = [1, 2, 3, 4, 5].map(episode);
    render(
      <MemoryRouter>
        <Band summary={undefined} samples={[]} operations={[]} upcoming={upcoming} />
      </MemoryRouter>
    );
    const next = screen.getByRole("region", { name: "Next" });
    const lines = next.querySelectorAll("ul.lb-then > li");
    expect(lines).toHaveLength(3);
    const first = within(lines[0] as HTMLElement).getByRole("link");
    expect(first).toHaveTextContent(/^then Show 2 S01E02 airs$/);
    expect(first).toHaveAttribute("href", "/tv/s2");
    for (const line of Array.from(lines)) {
      expect(line.querySelector("span")!.textContent!.length).toBeGreaterThan(0);
      expect(line.querySelector("a")).not.toBeNull();
    }
    expect(within(next).queryByRole("link", { name: /Show 5/ })).toBeNull();
  });
});
