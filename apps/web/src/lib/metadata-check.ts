import { ApiRequestError, type IntegrationFailure, type MetadataProviderStatus, type MetadataTestResponse } from "./api";

/**
 * What the Check now row on Metadata maintenance says (#1090).
 *
 * It used to say "Working" whenever the settings were filled in, even when the
 * service had just failed, and "Ready" from the configuration alone. Now the
 * words come from what the service actually did: the check's own answer first,
 * then the last failure the server remembers, and "Ready" only when neither
 * has anything bad to say.
 */
export interface MetadataCheckView {
  primary: string;
  secondary: string;
  tone: "ok" | "warn" | "bad";
  chip: string;
}

const NOT_CHECKED = "Not checked this session";

function isBusy(failure: IntegrationFailure | null | undefined) {
  return failure?.kind === "RateLimit" || failure?.kind === "QuotaExceeded";
}

export function describeMetadataCheck(
  status: MetadataProviderStatus | null,
  test: MetadataTestResponse | null,
  sampleTitle = "The Matrix"
): MetadataCheckView {
  if (test) {
    if (!test.isConfigured) {
      return {
        primary: "The check could not reach the service",
        secondary: test.message,
        tone: "bad",
        chip: "Failed"
      };
    }

    if (test.failure) {
      const busy = isBusy(test.failure);
      return {
        primary: busy ? "The service is busy" : "The service did not answer the check",
        secondary: test.message || test.failure.nextAction,
        tone: busy ? "warn" : "bad",
        chip: busy ? "Busy" : "Not answering"
      };
    }

    if (test.resultCount === 0) {
      return {
        primary: `No matches for “${sampleTitle}”`,
        secondary: "The service answered, but found nothing for this title.",
        tone: "warn",
        chip: "No match"
      };
    }

    return {
      primary: `${test.resultCount} ${test.resultCount === 1 ? "match" : "matches"} for “${sampleTitle}”`,
      secondary: "Deluno can match titles and collect their details.",
      tone: "ok",
      chip: "Working"
    };
  }

  if (status?.lastFailure) {
    const busy = isBusy(status.lastFailure);
    return {
      primary: busy ? "The service is busy" : "The last lookup did not get an answer",
      secondary: status.message || status.lastFailure.nextAction,
      tone: busy ? "warn" : "bad",
      chip: busy ? "Busy" : "Not answering"
    };
  }

  if (status?.isConfigured) {
    return {
      primary: NOT_CHECKED,
      secondary: "Deluno can match titles and collect their details.",
      tone: "ok",
      chip: "Ready"
    };
  }

  return {
    primary: NOT_CHECKED,
    secondary: "You can still add a movie or show by hand.",
    tone: "warn",
    chip: "Unavailable"
  };
}

/**
 * What the Add screen says when the title search fails (#1090). An outage
 * arrives as a 503 with the plain sentence in its body and must never read as
 * "No metadata matches found": nothing was found because nothing was asked.
 */
export function describeMetadataSearchError(error: unknown): { message: string; description?: string; outage: boolean } {
  if (error instanceof ApiRequestError) {
    let typed = false;
    try {
      typed = (JSON.parse(error.responseBody) as { error?: unknown }).error === "metadata-unavailable";
    } catch {
      typed = false;
    }

    if (typed) {
      return { message: error.message, description: "You can still add the title by hand.", outage: true };
    }

    return { message: error.message, outage: false };
  }

  return { message: "Metadata search failed.", outage: false };
}
