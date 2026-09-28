import { describe, expect, it } from "vitest";
import { describeTitleRouting } from "./title-routing";

/**
 * #990: the title page says which of its library's indexers/download
 * clients will actually be used for it, and why — mirroring the backend's
 * tag-routing rule (#808) so the screen and the search behaviour never
 * disagree.
 */
describe("describeTitleRouting", () => {
  it("uses an untagged connection for any title", () => {
    const [entry] = describeTitleRouting([{ id: "idx-1", name: "NZBGeek", tags: "" }], []);
    expect(entry.used).toBe(true);
    expect(entry.reason).toContain("NZBGeek");
    expect(entry.reason).toContain("any title");
  });

  it("uses a tagged connection only when the title shares one of its tags", () => {
    const connections = [{ id: "idx-1", name: "Anime Tracker", tags: "anime" }];

    const matching = describeTitleRouting(connections, ["kids", "anime"]);
    expect(matching[0]!.used).toBe(true);
    expect(matching[0]!.reason).toContain("Anime Tracker");
    expect(matching[0]!.reason).toContain("anime");

    const nonMatching = describeTitleRouting(connections, ["kids"]);
    expect(nonMatching[0]!.used).toBe(false);
    expect(nonMatching[0]!.reason).toContain("not");
  });

  it("treats a title with no tags as excluded from every tagged connection", () => {
    const [entry] = describeTitleRouting([{ id: "client-1", name: "Anime qBittorrent", tags: "anime" }], []);
    expect(entry.used).toBe(false);
  });

  it("matches case-insensitively and ignores surrounding whitespace in the connection's tags", () => {
    const [entry] = describeTitleRouting([{ id: "idx-1", name: "Anime Tracker", tags: " Anime , 4K " }], ["anime"]);
    expect(entry.used).toBe(true);
  });

  it("describes multiple connections independently, in the order given", () => {
    const entries = describeTitleRouting(
      [
        { id: "idx-1", name: "General indexer", tags: "" },
        { id: "idx-2", name: "Anime indexer", tags: "anime" }
      ],
      ["anime"]
    );
    expect(entries).toHaveLength(2);
    expect(entries[0]!.name).toBe("General indexer");
    expect(entries[0]!.used).toBe(true);
    expect(entries[1]!.name).toBe("Anime indexer");
    expect(entries[1]!.used).toBe(true);
  });
});
