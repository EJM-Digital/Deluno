import { describe, expect, it } from "vitest";
import { describeSearchRequestFailure } from "./search-reasons";

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("describeSearchRequestFailure", () => {
  it("never answers with the old catch-all sentence", async () => {
    const cases = await Promise.all([
      describeSearchRequestFailure(null, new Error("network down")),
      describeSearchRequestFailure(jsonResponse(401, {}), new Error("x")),
      describeSearchRequestFailure(jsonResponse(500, { error: "An unexpected error occurred." }), new Error("x")),
      describeSearchRequestFailure(jsonResponse(400, { error: "Bad library." }), new Error("x")),
    ]);

    for (const explained of cases) {
      expect(explained.title).not.toBe("The search request failed.");
      expect(explained.title.length).toBeGreaterThan(0);
    }
  });

  it("uses the server's own words when it gave any", async () => {
    const explained = await describeSearchRequestFailure(
      jsonResponse(500, { error: "An unexpected error occurred." }),
      new Error("movie-search-failed"),
    );

    expect(explained.title).toContain("500");
    expect(explained.description).toContain("An unexpected error occurred.");
    // A 500 is Deluno's fault, so it must not send the reader off to blame
    // their indexers.
    expect(explained.description).toContain("fault inside Deluno");
    expect(explained.action?.href).toBe("/system");
  });

  it("points a client-side failure at the things the owner can actually change", async () => {
    const explained = await describeSearchRequestFailure(
      jsonResponse(400, { error: "No library is linked." }),
      new Error("movie-search-failed"),
    );

    expect(explained.description).toContain("No library is linked.");
    expect(explained.action?.href).toBe("/indexers/indexers");
  });

  it("names a lost session rather than calling it a search problem", async () => {
    const explained = await describeSearchRequestFailure(jsonResponse(401, {}), new Error("x"));

    expect(explained.title).toContain("signed in");
    expect(explained.action).toBeUndefined();
  });

  it("says the request never completed when there was no response at all", async () => {
    const explained = await describeSearchRequestFailure(null, new Error("Failed to fetch"));

    expect(explained.title).toContain("could not reach");
    expect(explained.description).toContain("Failed to fetch");
  });

  it("still answers when the body is not JSON", async () => {
    const explained = await describeSearchRequestFailure(
      new Response("<html>502</html>", { status: 502 }),
      new Error("x"),
    );

    expect(explained.title).toContain("502");
    expect(explained.description).toContain("fault inside Deluno");
  });
});
