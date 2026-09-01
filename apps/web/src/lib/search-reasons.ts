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
        title: "Every indexer failed to answer",
        description: "Deluno reached the linked indexers, but they all failed. Check their health and credentials.",
        action: { label: "Check indexers", href: "/indexers/indexers" }
      };
    case "circuit_open":
      return {
        title: "Indexer search is temporarily paused",
        description: "Deluno is waiting for an indexer circuit breaker to recover.",
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
