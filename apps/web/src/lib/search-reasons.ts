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
 * What to say when the search request itself did not come back.
 *
 * Both detail pages used to `catch` and say "The search request failed." and
 * nothing else - not the status, not the body, not which title. That sentence
 * is true of every possible cause and useful for none of them, and it threw
 * away a response body that often says exactly what went wrong.
 */
export async function describeSearchRequestFailure(
  response: Response | null,
  error: unknown,
): Promise<SearchReasonExplanation> {
  if (!response) {
    const detail = error instanceof Error ? error.message : undefined;
    return {
      title: "Deluno could not reach its own API",
      description: [
        "The search request never completed.",
        detail,
        "Check that Deluno is still running, then try again.",
      ].filter(Boolean).join(" "),
    };
  }

  if (response.status === 401 || response.status === 403) {
    return {
      title: "Your session is no longer signed in",
      description: "Sign in again, then run the search.",
    };
  }

  // The server's own words, when it has any. A bare status is the fallback,
  // not the first answer.
  let detail: string | undefined;
  try {
    const body = await response.clone().json() as { error?: string; detail?: string; title?: string };
    detail = body.error ?? body.detail ?? body.title;
  } catch {
    detail = undefined;
  }

  return {
    title: `The search failed (HTTP ${response.status})`,
    description: [
      detail,
      response.status >= 500
        ? "This is a fault inside Deluno rather than a problem with your indexers. Check System health, and the host log for the request."
        : "Check the title's library, sources and download client, then try again.",
    ].filter(Boolean).join(" "),
    action: response.status >= 500
      ? { label: "Open System health", href: "/system" }
      : { label: "Check indexers", href: "/indexers/indexers" },
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
