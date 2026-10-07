import { describe, expect, it } from "vitest";
import type { LibraryRoutingSnapshot } from "../../lib/api";
import { planClientRouting, planIndexerRouting } from "./auto-routing";

const libraries = [
  { id: "m", name: "Movies", mediaType: "movies" },
  { id: "t", name: "TV Shows", mediaType: "tv" }
];
const empty = (libraryId: string): LibraryRoutingSnapshot => ({ libraryId, libraryName: libraryId, sources: [], downloadClients: [] });
const source = (libraryId: string, indexerId: string, priority = 1) => ({ id: "x", libraryId, indexerId, indexerName: indexerId, priority, requiredTags: "", excludedTags: "", createdUtc: "", updatedUtc: "" });
const link = (libraryId: string, downloadClientId: string) => ({ id: "y", libraryId, downloadClientId, downloadClientName: downloadClientId, priority: 1, category: null, createdUtc: "", updatedUtc: "" });

/** #1135: what an indexer or client was added for routes it, so Library Routing does not ask again. */
describe("planIndexerRouting", () => {
  it("routes a Movies indexer to the movie library only", () => {
    const plans = planIndexerRouting({ id: "i", mediaScope: "movies" }, libraries, [empty("m"), empty("t")]);
    expect(plans.map((plan) => plan.libraryId)).toEqual(["m"]);
    expect(plans[0]!.body.sources).toEqual([{ indexerId: "i", priority: 1, requiredTags: "", excludedTags: "" }]);
  });

  it("routes a Both indexer to every library and keeps what a library already searches", () => {
    const plans = planIndexerRouting({ id: "i", mediaScope: "both" }, libraries, [{ ...empty("m"), sources: [source("m", "older", 1)] }, empty("t")]);
    expect(plans.map((plan) => plan.libraryId)).toEqual(["m", "t"]);
    expect(plans[0]!.body.sources.map((item) => [item.indexerId, item.priority])).toEqual([["older", 1], ["i", 2]]);
  });

  it("leaves a library that already searches it alone", () => {
    expect(planIndexerRouting({ id: "i", mediaScope: "both" }, libraries, [{ ...empty("m"), sources: [source("m", "i")] }, empty("t")]).map((plan) => plan.libraryId)).toEqual(["t"]);
  });
});

describe("planClientRouting", () => {
  it("routes the first client to every library type it serves", () => {
    const plans = planClientRouting({ id: "c", moviesCategory: "movies", tvCategory: "tv" }, libraries, [empty("m"), empty("t")]);
    expect(plans.map((plan) => plan.libraryId)).toEqual(["m", "t"]);
  });

  it("does not take over a library that already has a client", () => {
    const plans = planClientRouting({ id: "c", moviesCategory: "movies", tvCategory: "tv" }, libraries, [{ ...empty("m"), downloadClients: [link("m", "first")] }, empty("t")]);
    expect(plans.map((plan) => plan.libraryId)).toEqual(["t"]);
  });

  it("serves only the types its categories name, and both when it has no categories", () => {
    expect(planClientRouting({ id: "c", moviesCategory: "movies" }, libraries, [empty("m"), empty("t")]).map((plan) => plan.libraryId)).toEqual(["m"]);
    expect(planClientRouting({ id: "c" }, libraries, [empty("m"), empty("t")]).map((plan) => plan.libraryId)).toEqual(["m", "t"]);
  });
});
