import { describe, expect, it } from "vitest";
import { qualityShare } from "./standing";

const counts = (total: number, qualityMet: number, upgradable: number) => ({ total, qualityMet, upgradable });
const summary = (movies: ReturnType<typeof counts>, shows: ReturnType<typeof counts>) => ({ library: { movies, shows } }) as never;

describe("the share of the library that meets its quality goal", () => {
  it("says nothing before there is a summary or a library section", () => {
    expect(qualityShare(undefined)).toBeNull();
    expect(qualityShare({ library: null } as never)).toBeNull();
  });

  it("says nothing for an empty library", () => {
    expect(qualityShare(summary(counts(0, 0, 0), counts(0, 0, 0)))).toBeNull();
  });

  it("adds films and shows together and rounds to a whole percent", () => {
    expect(qualityShare(summary(counts(2, 1, 1), counts(1, 1, 2)))).toEqual({ percent: 67, met: 2, total: 3, upgradable: 3 });
  });

  it("counts a library of only films or only shows", () => {
    expect(qualityShare(summary(counts(4, 1, 0), counts(0, 0, 0)))).toMatchObject({ percent: 25, total: 4 });
    expect(qualityShare(summary(counts(0, 0, 0), counts(1, 1, 0)))).toMatchObject({ percent: 100, total: 1 });
  });

  it("never reports more met than there are titles", () => {
    expect(qualityShare(summary(counts(2, 5, 0), counts(1, 4, 0)))).toMatchObject({ met: 3, percent: 100 });
  });
});
