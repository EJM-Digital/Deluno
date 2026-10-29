import { describe, expect, it } from "vitest";
import { ApiRequestError, type IntegrationFailure, type MetadataProviderStatus, type MetadataTestResponse } from "./api";
import { describeMetadataCheck, describeMetadataSearchError } from "./metadata-check";

function failure(kind: string): IntegrationFailure {
  return {
    serviceType: "metadata",
    serviceId: "metadata:broker:x",
    serviceName: "the metadata service",
    operation: "metadata.broker.search",
    kind,
    retryState: "RetryScheduled",
    message: "The service returned HTTP 503.",
    code: null,
    httpStatus: 503,
    upstreamDetail: null,
    externalId: null,
    retryAfterUtc: null,
    attempts: 3,
    isTransient: true,
    legacyCategory: "connectivity",
    summary: "",
    nextAction: "Check the service and network."
  };
}

const status = (over: Partial<MetadataProviderStatus> = {}): MetadataProviderStatus => ({
  provider: "deluno",
  isConfigured: true,
  mode: "broker",
  message: "Deluno's managed metadata service is ready for title matching.",
  sources: [],
  ...over
});

const test = (over: Partial<MetadataTestResponse> = {}): MetadataTestResponse => ({
  provider: "deluno",
  isConfigured: true,
  mode: "broker",
  message: "ready",
  resultCount: 5,
  sampleResults: [],
  ...over
});

describe("describeMetadataCheck", () => {
  it("says Ready only when nothing has failed", () => {
    expect(describeMetadataCheck(status(), null)).toMatchObject({ chip: "Ready", tone: "ok" });
  });

  it("does not say Ready when the last lookup failed, and says why in plain words", () => {
    const view = describeMetadataCheck(
      status({ lastFailure: failure("Unavailable"), message: "The metadata service isn't answering; try again in a minute." }),
      null
    );
    expect(view.chip).toBe("Not answering");
    expect(view.tone).toBe("bad");
    expect(view.secondary).toBe("The metadata service isn't answering; try again in a minute.");
  });

  it("calls a rate-limited service busy, not broken", () => {
    const view = describeMetadataCheck(status({ lastFailure: failure("RateLimit") }), null);
    expect(view).toMatchObject({ chip: "Busy", tone: "warn" });
  });

  it("does not say Working when the check hit a failure, even though the settings are filled in", () => {
    const view = describeMetadataCheck(
      status(),
      test({ failure: failure("Timeout"), resultCount: 0, message: "The metadata service isn't answering; try again in a minute." })
    );
    expect(view.chip).toBe("Not answering");
    expect(view.chip).not.toBe("Working");
    expect(view.primary).toBe("The service did not answer the check");
  });

  it("says Working only with matches and no failure", () => {
    expect(describeMetadataCheck(status(), test())).toMatchObject({ chip: "Working", tone: "ok", primary: "5 matches for “The Matrix”" });
    expect(describeMetadataCheck(status(), test({ resultCount: 1 })).primary).toBe("1 match for “The Matrix”");
  });

  it("an answer with no matches is not Working and not a failure either", () => {
    expect(describeMetadataCheck(status(), test({ resultCount: 0 }))).toMatchObject({ chip: "No match", tone: "warn" });
  });

  it("an unconfigured service is Unavailable, and a failed-to-run check says so", () => {
    expect(describeMetadataCheck(status({ isConfigured: false }), null).chip).toBe("Unavailable");
    expect(describeMetadataCheck(null, test({ isConfigured: false, message: "The check could not be run." })).chip).toBe("Failed");
  });
});

describe("describeMetadataSearchError", () => {
  it("says the service is not answering instead of reading an outage as no matches", () => {
    const words = "The metadata service isn't answering; try again in a minute.";
    const error = new ApiRequestError(words, 503, "/api/metadata/search", JSON.stringify({ error: "metadata-unavailable", message: words }));
    expect(describeMetadataSearchError(error)).toEqual({
      message: words,
      description: "You can still add the title by hand.",
      outage: true
    });
  });

  it("keeps the server's sentence for any other refusal and a plain fallback for anything else", () => {
    expect(describeMetadataSearchError(new ApiRequestError("Nope.", 400, "/api/metadata/search", "{}"))).toEqual({ message: "Nope.", outage: false });
    expect(describeMetadataSearchError(new Error("x")).message).toBe("Metadata search failed.");
  });
});

describe("describeMetadataCheck, the other ways a check can end", () => {
  it("calls a check that hit a rate limit busy, and one that carries no message falls back to the failure's next step", () => {
    const view = describeMetadataCheck(status(), test({ failure: failure("QuotaExceeded"), resultCount: 0, message: "" }));
    expect(view).toMatchObject({ primary: "The service is busy", chip: "Busy", tone: "warn", secondary: "Check the service and network." });
  });

  it("falls back to the last failure's next step when the status has no message of its own", () => {
    const view = describeMetadataCheck(status({ lastFailure: failure("Unavailable"), message: "" }), null);
    expect(view.secondary).toBe("Check the service and network.");
  });

  it("reads a failure body that is not JSON as an ordinary refusal", () => {
    expect(describeMetadataSearchError(new ApiRequestError("Bad gateway.", 502, "/api/metadata/search", "<html>"))).toEqual({ message: "Bad gateway.", outage: false });
  });
});
