import { describe, expect, it } from "vitest";
import { adaptMovieItems, adaptSeriesItems } from "./ui-adapters";
import { titleBar } from "./status-tones";
import type { MovieListItem, SeriesListItem } from "./api/types/catalogue";

/**
 * The two adapters carry the subtitle numbers to the bar.
 *
 * <b>Why this test exists.</b> `adaptMovieItems` and `adaptSeriesItems` copy the
 * catalogue row field by field, twice, and the server had been sending
 * `subtitleLanguagesSettled` for a whole deploy before anybody noticed the
 * poster was drawing none of it: the API said two, the bar said zero, and
 * nothing failed. Typing does not catch it because every field is optional, and
 * it is the same one-rule-written-twice shape as everything else here.
 *
 * So the bar's inputs are asserted through the adapters rather than assumed to
 * survive them.
 */
describe("the adapters carry what the subtitle bar reads", () => {
  const subtitleFields = {
    subtitleLanguagesWanted: 2,
    subtitleLanguagesHeld: 2,
    subtitleLanguagesSettled: 1
  };

  it("carries them off a movie", () => {
    const [item] = adaptMovieItems([
      { id: "m1", title: "Dune", hasFile: true, ...subtitleFields } as MovieListItem
    ]);

    expect(titleBar(item)).toMatchObject({ wanted: 2, held: 2, settled: 1 });
  });

  it("carries them off a show, over the episodes it holds", () => {
    const [item] = adaptSeriesItems([
      {
        id: "s1",
        title: "Severance",
        hasFile: true,
        airedWithFileCount: 3,
        subtitleLanguagesWanted: 1,
        subtitleLanguagesHeld: 3,
        subtitleLanguagesSettled: 2
      } as SeriesListItem
    ]);

    // Three files, one language each: three wanted, three held, two of them done.
    expect(titleBar(item)).toMatchObject({ wanted: 3, held: 3, settled: 2 });
  });

  it("does not invent a settled count the server did not send", () => {
    const [item] = adaptMovieItems([
      { id: "m2", title: "Arrival", hasFile: true, subtitleLanguagesWanted: 1, subtitleLanguagesHeld: 1 } as MovieListItem
    ]);

    expect(titleBar(item).settled).toBe(0);
  });
});
