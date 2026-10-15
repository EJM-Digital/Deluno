import type { IntegrationFailure } from "./api/types";

export interface SearchReasonExplanation {
  title: string;
  description?: string;
  action?: {
    label: string;
    href: string;
  };
}

/** Keep source failures visible when a search also has a usable result. */
export function formatSearchFailureNotice(failures?: IntegrationFailure[] | null): string | undefined {
  if (!failures?.length) return undefined;

  const [first] = failures;
  const summary = first.summary || `${first.serviceName} ${first.operation} failed: ${first.message}`;
  const nextAction = first.nextAction ? ` ${first.nextAction}` : "";
  const remainder = failures.length > 1
    ? ` ${failures.length - 1} other source${failures.length === 2 ? "" : "s"} also need attention.`
    : "";
  return `${summary}${nextAction}${remainder}`;
}

/**
 * A failed request that kept its response.
 *
 * `throw new Error("search-failed")` is the shape every one of these call
 * sites used, and it discards the only thing that could explain the failure.
 * Carrying the response means a caller several frames away can still be told
 * what the service actually said (#338).
 */
export class RequestFailedError extends Error {
  constructor(readonly response: Response, message = "request-failed") {
    super(message);
    this.name = "RequestFailedError";
  }
}

export interface RequestFailureContext {
  /**
   * What the person was trying to do, as a verb phrase that completes
   * "Deluno could not ...": "send that release to the download client".
   */
  action: string;
  /** Where to send them when their own configuration is the likely cause. */
  check?: { label: string; href: string };
}

/**
 * What to say when a request to Deluno's own API did not come back.
 *
 * Every one of these call sites used to `catch` and say a single fixed
 * sentence - "The search request failed.", "That release could not be sent to
 * the download client." - and nothing else. Not the status, not the body, not
 * which service. Those sentences are true of every possible cause and useful
 * for none of them, and each threw away a response body that usually says
 * exactly what went wrong.
 *
 * #338 asks that no external-service failure is reduced to a generic "request
 * failed". This is the one place that decides what is said instead.
 */
export async function describeRequestFailure(
  response: Response | null,
  error: unknown,
  context: RequestFailureContext,
): Promise<SearchReasonExplanation> {
  response ??= error instanceof RequestFailedError ? error.response : null;

  if (!response) {
    const detail = error instanceof Error ? error.message : undefined;
    return {
      title: "Deluno could not reach its own API",
      description: [
        `The request to ${context.action} never completed.`,
        detail,
        "Check that Deluno is still running, then try again.",
      ].filter(Boolean).join(" "),
    };
  }

  if (response.status === 401 || response.status === 403) {
    return {
      title: "Your session is no longer signed in",
      description: `Sign in again, then ${context.action}.`,
    };
  }

  // The server's own words, when it has any. A bare status is the fallback,
  // not the first answer.
  let detail: string | undefined;
  let failure: IntegrationFailure | undefined;
  let retryAfterSeconds: number | undefined;
  try {
    const body = await response.clone().json() as {
      retryAfterSeconds?: number;
      error?: string;
      detail?: string;
      title?: string;
      message?: string;
      failure?: IntegrationFailure | null;
    };
    detail = body.error ?? body.detail ?? body.message ?? body.title;
    failure = body.failure ?? undefined;
    retryAfterSeconds = typeof body.retryAfterSeconds === "number" ? body.retryAfterSeconds : undefined;
  } catch {
    detail = undefined;
  }

  // Too many attempts (the sign-in limiter, #1339): say how long to wait, and do not send the person to Settings.
  if (response.status === 429) {
    const header = Number(response.headers.get("Retry-After"));
    const seconds = retryAfterSeconds ?? (Number.isFinite(header) && header > 0 ? header : undefined);
    return {
      title: "Too many attempts",
      description: [
        detail,
        seconds
          ? `Wait ${seconds} second${seconds === 1 ? "" : "s"}, then ${context.action} again.`
          : `Wait a minute, then ${context.action} again.`,
      ].filter(Boolean).join(" "),
    };
  }

  // A typed failure already knows which service failed, what it said, and
  // whether Deluno will try again. Restating that as "HTTP 503" would be
  // throwing the answer away at the last step.
  if (failure) {
    return {
      title: detail ?? failure.summary,
      description: [
        detail ? undefined : failure.summary,
        failure.upstreamDetail && failure.upstreamDetail !== failure.message
          ? failure.upstreamDetail
          : undefined,
        failure.nextAction,
      ].filter(Boolean).join(" "),
      action: context.check,
    };
  }

  const isDelunoFault = response.status >= 500;

  return {
    title: `Deluno could not ${context.action} (HTTP ${response.status})`,
    description: [
      detail,
      isDelunoFault
        ? "This is a fault inside Deluno rather than a problem with the service it was talking to. Check System health, and the host log for this request."
        : "Check the settings for this action, then try again.",
    ].filter(Boolean).join(" "),
    action: isDelunoFault ? { label: "Open System health", href: "/system" } : context.check,
  };
}

export function describeSearchReason(reason: string | undefined, fallback: string): SearchReasonExplanation {
  switch (reason) {
    case "no_indexers":
      return {
        title: "No indexers are linked to this library",
        description: "Deluno had nowhere to search. Link an indexer to this library's policy first.",
        action: { label: "Open Indexers", href: "/indexers/indexers" }
      };
    case "all_indexers_failed":
      return {
        // Not "reached": a timeout never reached anything, and this line is
        // shown for that case too. Each indexer's own failure follows and says
        // precisely what happened to it, so this one only has to be true.
        title: "Every indexer failed to answer",
        description: "No linked indexer returned a usable answer.",
        action: { label: "Check indexers", href: "/indexers/indexers" }
      };
    case "circuit_open":
      return {
        title: "Indexer search is temporarily paused",
        // Not "circuit breaker": that is Deluno's word for it, not the
        // owner's. The failure that follows names the indexer and the time
        // it will be tried again, so this line only sets the scene.
        description: "Deluno stopped querying an indexer after it failed repeatedly, and will try it again by itself.",
        action: { label: "Check indexers", href: "/indexers/indexers" }
      };
    case "no_results":
      return {
        title: "No matching releases were returned",
        description: "The linked indexers answered, but none returned a release for this title."
      };
    // Nobody was asked (#598): not an empty search, and nothing is wrong
    // with the indexers. Saying "no results" here blamed them for a queue.
    case "indexers_busy":
      return {
        title: "The indexers were busy",
        description: "Each indexer was still working through other searches, so none was asked for this title. Try again in a few minutes."
      };
    // Found, compared, and waiting on a person. This used to report as
    // "no_results", so a search that returned three candidates told the owner
    // the indexers had returned none.
    case "held_for_review":
      return {
        title: "Releases were found and are waiting on you",
        description: "Deluno compared what the indexers returned and did not send any of them automatically. Choose the one to send."
      };
    case "no_usable_release":
      return {
        title: "No release met the active policy",
        description: "Deluno found releases, but none passed the current quality and custom-format rules."
      };
    case "not_searchable":
      return {
        title: "This title is not linked to a searchable library",
        description: "Link the title to a library with a search policy before searching.",
        action: { label: "Open Libraries", href: "/settings/libraries" }
      };
    case "library_missing":
      return {
        title: "The linked library is missing",
        description: "Choose an existing library before searching this title.",
        action: { label: "Open Libraries", href: "/settings/libraries" }
      };
    case "season_pack_replacement_requires_episode_scope":
      return {
        title: "Season upgrades need episode review",
        description: "This season already has episode files. Deluno held the whole-season replacement so each installed file can be compared under the current plan; search the selected episodes instead."
      };
    case "season_pack_installed_evidence_missing":
      return {
        title: "Installed episodes need file evaluation",
        description: "Deluno held the season search because at least one installed episode does not yet have evidence under the current release plan. Let the file probe finish or search episodes individually."
      };
    case "season_pack_candidate_not_upgrade_for_every_episode":
      return {
        title: "The season pack would not improve every installed episode",
        description: "Deluno found a pack, but replacing the whole season would be lateral or worse for at least one installed episode. No download was sent."
      };
    default:
      return { title: fallback };
  }
}

export function formatGrabMessage(
  best: string,
  payload: { forceOverride?: boolean; dispatchStatus?: string | null; dispatchMessage?: string | null }
) {
  const prefix = payload.forceOverride ? "Forced" : "Sent";
  switch (payload.dispatchStatus) {
    case "sent":
      return `${prefix} ${best} to the download client.`;
    case "planned":
      return `Matched ${best}, but no downloadable URL was available yet.`;
    case "failed":
      return `Matched ${best}, but the download client rejected it${payload.dispatchMessage ? `: ${payload.dispatchMessage}` : "."}`;
    default:
      return `Matched ${best}.`;
  }
}

export interface SearchNowNotice {
  tone: "success" | "info" | "error";
  title: string;
  description?: string;
}

/**
 * What a movie's or show's **Search now** tells a person (#594).
 *
 * It used to say "Deluno selected <release>" whenever the answer named a
 * release, and the release had been held: nothing was sent. The words now
 * follow what happened. A release is sent only when a client took it, a held
 * one is held with Deluno's reason, and a show's episodes searched one by one
 * (#593) are counted. Null means nothing was found or asked for, which the
 * caller explains from the search reason.
 */
export function describeSearchNow(payload: {
  outcome?: string | null;
  summary?: string | null;
  releaseName?: string | null;
  indexerName?: string | null;
  dispatchStatus?: string | null;
  dispatchMessage?: string | null;
  plannedEpisodes?: number;
}): SearchNowNotice | null {
  const planned = payload.plannedEpisodes ?? 0;
  const episodes = planned > 0 ? `Searching for ${planned} missing episode${planned === 1 ? "" : "s"}, each on its own.` : null;
  const best = payload.releaseName ? `${payload.releaseName}${payload.indexerName ? ` via ${payload.indexerName}` : ""}` : null;

  if (best && payload.dispatchStatus) {
    return {
      tone: payload.dispatchStatus === "sent" ? "success" : payload.dispatchStatus === "failed" ? "error" : "info",
      title: formatGrabMessage(best, payload),
      description: episodes ?? undefined
    };
  }

  if (best) {
    return {
      tone: "info",
      title: payload.outcome === "held" ? `Held ${best} for review. Nothing was sent.` : `Found ${best}. Nothing was sent.`,
      description: [payload.summary, episodes].filter(Boolean).join(" ") || undefined
    };
  }

  return episodes ? { tone: "info", title: episodes } : null;
}

/**
 * The one short line a poster shows after its Search icon ran (#1156): what
 * the search did, in the same terms as the result note on the title's page.
 * A title that already has a file is looking for an upgrade, so an empty
 * search there means nothing better, not nothing at all.
 */
export function posterSearchResult(
  payload: {
    outcome?: string | null;
    releaseName?: string | null;
    dispatchStatus?: string | null;
    downloadClientName?: string | null;
  },
  hasFile: boolean
): string {
  if (payload.releaseName && payload.dispatchStatus === "sent") {
    return payload.downloadClientName ? `Sent to ${payload.downloadClientName}` : "Sent to your download client";
  }
  if (payload.releaseName && payload.dispatchStatus === "failed") return "Your download client refused it";
  if (payload.releaseName && payload.outcome === "held") return "Found one, waiting on you";
  if (payload.releaseName) return "Found one, nothing sent";
  return hasFile ? "Nothing better found" : "Nothing found yet";
}
