import { describe, expect, it } from "vitest";
import { historyPath, indexerPageLink, legacyActivityTarget, paramsWithView, transfersViewFromParams } from "./transfers-view";

describe("the views of Transfers (#1426)", () => {
  it("reads the view from the address; no view, or one that is not a view, is the live downloads", () => {
    expect(transfersViewFromParams(new URLSearchParams(""))).toBe("live");
    expect(transfersViewFromParams(new URLSearchParams("view=handoffs"))).toBe("handoffs");
    expect(transfersViewFromParams(new URLSearchParams("view=history"))).toBe("history");
    expect(transfersViewFromParams(new URLSearchParams("view=live"))).toBe("live");
    expect(transfersViewFromParams(new URLSearchParams("view=nonsense"))).toBe("live");
    expect(transfersViewFromParams(new URLSearchParams("manualImportPath=D%3A%2Fx"))).toBe("live");
  });

  it("choosing a view keeps nothing of the last one's filters; live is the absence of a view", () => {
    expect(paramsWithView("live").toString()).toBe("");
    expect(paramsWithView("handoffs").toString()).toBe("view=handoffs");
    expect(paramsWithView("history").toString()).toBe("view=history");
  });

  it("writes the history's address with the view first and every other parameter kept", () => {
    expect(historyPath()).toBe("/queue?view=history");
    expect(historyPath("status=failed&since=today")).toBe("/queue?view=history&status=failed&since=today");
    expect(historyPath(new URLSearchParams({ file: "C:/a b.mkv" }))).toBe("/queue?view=history&file=C%3A%2Fa+b.mkv");
    expect(historyPath("view=live&status=done")).toBe("/queue?view=history&status=done");
  });

  it("sends the old Activity address to the history, keeping all it carried", () => {
    expect(legacyActivityTarget("")).toBe("/queue?view=history");
    expect(legacyActivityTarget("?status=failed")).toBe("/queue?view=history&status=failed");
    expect(legacyActivityTarget("?section=jobs")).toBe("/queue?view=history&section=jobs");
    expect(legacyActivityTarget("?event=evt-1&since=2026-10-01&until=2026-10-01")).toBe("/queue?view=history&event=evt-1&since=2026-10-01&until=2026-10-01");
  });

  it("links an indexer by name to the Indexers list with that indexer opened", () => {
    expect(indexerPageLink("NZBgeek")).toBe("/indexers/indexers?indexer=NZBgeek");
    expect(indexerPageLink("Nyaa.si & Co")).toBe("/indexers/indexers?indexer=Nyaa.si+%26+Co");
  });
});
