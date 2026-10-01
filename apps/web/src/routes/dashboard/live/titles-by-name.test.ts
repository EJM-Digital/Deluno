import { describe, expect, it } from "vitest";
import type { JourneyCard } from "./journey";
import { nameSearchOf, pickByName } from "./titles";

const card = (title: string, extra: Partial<JourneyCard> = {}): JourneyCard => ({ key: title, stage: "downloading", title, state: { tone: "info", label: "Working" }, to: "/queue", ...extra });

describe("matching a download no sent download names to the library by its release name", () => {
  it("reads a film with its year and a show with its episode", () => {
    expect(nameSearchOf(card("The General (1926)"))).toEqual({ kind: "movie", name: "The General", year: 1926 });
    expect(nameSearchOf(card("Breaking Bad S01E07"))).toEqual({ kind: "series", name: "Breaking Bad" });
  });
  it("leaves alone a card that already knows its title, or has no year or episode to go on", () => {
    expect(nameSearchOf(card("The General (1926)", { entity: { kind: "movie", id: "m1" } }))).toBeNull();
    expect(nameSearchOf(card("Some Name"))).toBeNull();
  });
  it("takes only an exact name, and a film's year must agree", () => {
    const rows = [{ id: "a", title: "The General Hospital", releaseYear: 1926 }, { id: "b", title: "The General", releaseYear: 1998 }, { id: "c", title: "The General", releaseYear: 1926, posterUrl: "/p" }];
    expect(pickByName({ kind: "movie", name: "The General", year: 1926 }, rows)?.id).toBe("c");
    expect(pickByName({ kind: "movie", name: "Nothing", year: 1926 }, rows)).toBeNull();
  });
});
