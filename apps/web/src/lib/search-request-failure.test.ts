import { describe, expect, it } from "vitest";
import { describeRequestFailure, RequestFailedError } from "./search-reasons";

const SEARCH = {
  action: "search for this title",
  check: { label: "Check indexers", href: "/indexers/indexers" }
} as const;

const DISPATCH = {
  action: "send that release to the download client",
  check: { label: "Check download clients", href: "/indexers/download-clients" }
} as const;

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}

describe("describeRequestFailure", () => {
  it("never answers with a sentence that fits every possible cause", async () => {
    const cases = await Promise.all([
      describeRequestFailure(null, new Error("network down"), SEARCH),
      describeRequestFailure(jsonResponse(401, {}), new Error("x"), SEARCH),
      describeRequestFailure(jsonResponse(500, { error: "An unexpected error occurred." }), new Error("x"), SEARCH),
      describeRequestFailure(jsonResponse(400, { error: "Bad library." }), new Error("x"), SEARCH)
    ]);

    for (const explained of cases) {
      expect(explained.title).not.toBe("The search request failed.");
      expect(explained.title).not.toBe("That release could not be sent to the download client.");
      expect(explained.title.length).toBeGreaterThan(0);
    }

    // Each cause reads differently. That is the whole point of #338.
    expect(new Set(cases.map((explained) => explained.title)).size).toBe(cases.length);
  });

  it("uses the server's own words when it gave any", async () => {
    const explained = await describeRequestFailure(
      jsonResponse(500, { error: "An unexpected error occurred." }),
      new Error("movie-search-failed"),
      SEARCH
    );

    expect(explained.title).toContain("500");
    expect(explained.description).toContain("An unexpected error occurred.");
    // A 500 is Deluno's fault, so it must not send the reader off to blame
    // the service Deluno was talking to.
    expect(explained.description).toContain("fault inside Deluno");
    expect(explained.action?.href).toBe("/system");
  });

  it("names the action that failed, so two different failures do not read alike", async () => {
    const search = await describeRequestFailure(jsonResponse(400, {}), new Error("x"), SEARCH);
    const dispatch = await describeRequestFailure(jsonResponse(400, {}), new Error("x"), DISPATCH);

    expect(search.title).toContain("search for this title");
    expect(dispatch.title).toContain("send that release to the download client");
    expect(search.title).not.toBe(dispatch.title);
  });

  it("points a client-side failure at the thing the owner can actually change", async () => {
    const explained = await describeRequestFailure(
      jsonResponse(400, { error: "No library is linked." }),
      new Error("x"),
      DISPATCH
    );

    expect(explained.description).toContain("No library is linked.");
    expect(explained.action?.href).toBe("/indexers/download-clients");
  });

  it("names a lost session rather than calling it a service problem", async () => {
    const explained = await describeRequestFailure(jsonResponse(401, {}), new Error("x"), SEARCH);

    expect(explained.title).toContain("signed in");
    expect(explained.action).toBeUndefined();
  });

  it("says the request never completed when there was no response at all", async () => {
    const explained = await describeRequestFailure(null, new Error("Failed to fetch"), SEARCH);

    expect(explained.title).toContain("could not reach");
    expect(explained.description).toContain("Failed to fetch");
  });

  it("still answers when the body is not JSON", async () => {
    const explained = await describeRequestFailure(
      new Response("<html>502</html>", { status: 502 }),
      new Error("x"),
      SEARCH
    );

    expect(explained.title).toContain("502");
    expect(explained.description).toContain("fault inside Deluno");
  });

  it("reads a message field, which is what most of the API returns", async () => {
    const explained = await describeRequestFailure(
      jsonResponse(409, { message: "That episode already has a file." }),
      new Error("x"),
      DISPATCH
    );

    expect(explained.description).toContain("That episode already has a file.");
  });
  it("uses the typed failure the service supplied rather than restating its status code", async () => {
    // The metadata provider path used to answer a bare 503 with no body, so
    // every surface could only say "could not be refreshed". Deluno knew which
    // provider it asked, what the provider said, and that it would try again.
    const explained = await describeRequestFailure(
      jsonResponse(503, {
        code: "metadata-provider-unavailable",
        message: "TMDB could not answer: The service did not answer in time. Sintel was left exactly as it is.",
        failure: {
          serviceType: "metadata",
          serviceId: "tmdb",
          serviceName: "tmdb",
          operation: "metadata.provider.resolve",
          kind: "Timeout",
          retryState: "RetryScheduled",
          message: "The service did not answer in time.",
          code: null,
          httpStatus: 504,
          upstreamDetail: "The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing",
          externalId: null,
          retryAfterUtc: null,
          attempts: 3,
          isTransient: true,
          legacyCategory: "timeout",
          summary: "tmdb metadata.provider.resolve failed: The service did not answer in time.",
          nextAction: "Check the service and network. Deluno will retry when the retry window allows."
        }
      }),
      new Error("movie-metadata-refresh-failed"),
      { action: "refresh this movie's metadata" }
    );

    expect(explained.title).toContain("TMDB");
    expect(explained.title).not.toContain("HTTP 503");
    expect(explained.description).toContain("Deluno will retry");
    expect(explained.description).toContain("HttpClient.Timeout");
  });

  it("reads the response out of a thrown RequestFailedError", async () => {
    // Some call sites are several frames from the fetch. Throwing away the
    // response on the way up is how the failure became generic again.
    const error = new RequestFailedError(jsonResponse(502, { message: "The indexer proxy refused the connection." }));

    const explained = await describeRequestFailure(null, error, { action: "start that search" });

    expect(explained.description).toContain("The indexer proxy refused the connection.");
    expect(explained.title).not.toContain("could not reach its own API");
  });

});
