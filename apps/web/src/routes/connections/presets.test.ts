import { describe, expect, it } from "vitest";
import { CLIENT_PRESETS, HOST_OPTIONS, INDEXER_PRESETS, PORT_OPTIONS } from "./presets";

const MOVIES = "2000,2010,2020,2030,2040,2045,2050,2060,2070";
const TV = "5000,5010,5020,5030,5040,5045,5050,5060,5070";
const BOTH = "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050";

function preset(protocol: string) {
  const found = INDEXER_PRESETS.find((item) => item.protocol === protocol);
  if (!found) throw new Error(`no ${protocol} preset`);
  return found;
}

describe("the indexer types offered when adding an indexer", () => {
  it.each(["tracker", "torznab", "newznab"])("%s searches the film categories, the show categories, or both, as the person chose", (protocol) => {
    const { defaultCategories } = preset(protocol);
    expect(defaultCategories("movies")).toBe(MOVIES);
    expect(defaultCategories("tv")).toBe(TV);
    expect(defaultCategories("both")).toBe(BOTH);
  });

  it.each(["rss", "custom"])("%s starts with no categories whatever the scope", (protocol) => {
    const { defaultCategories } = preset(protocol);
    expect(defaultCategories("movies")).toBe("");
    expect(defaultCategories("tv")).toBe("");
    expect(defaultCategories("both")).toBe("");
  });

  it("asks for a key only where the site needs one", () => {
    expect(INDEXER_PRESETS.map((item) => [item.protocol, item.requiresApiKey])).toEqual([
      ["tracker", false], ["torznab", true], ["newznab", true], ["rss", false], ["custom", false]
    ]);
  });

  it("describes each type in words a person can pick by", () => {
    expect(INDEXER_PRESETS.map((item) => item.label)).toEqual(["Tracker (built in)", "Torznab", "Newznab", "RSS feed", "Custom"]);
    for (const item of INDEXER_PRESETS) {
      expect(item.hint.length).toBeGreaterThan(10);
      expect(item.placeholder).toMatch(/^https?:\/\//);
    }
  });
});

describe("the download client types and the choices beside them", () => {
  it("lists every client with the port it normally listens on", () => {
    expect(CLIENT_PRESETS.map((item) => [item.protocol, item.defaultPort])).toEqual([
      ["qbittorrent", 8080], ["transmission", 9091], ["deluge", 8112], ["utorrent", 8080], ["rtorrent", 8000], ["sabnzbd", 8080], ["nzbget", 6789]
    ]);
  });

  it("names each port once, with every client that uses it", () => {
    expect(PORT_OPTIONS.find((option) => option.value === "8080")?.label).toBe("qBittorrent / uTorrent / SABnzbd default (8080)");
    expect(PORT_OPTIONS).toHaveLength(5);
  });

  it("offers this machine first when picking a host", () => {
    expect(HOST_OPTIONS[0]).toEqual({ label: "This machine (localhost)", value: "localhost" });
  });
});
