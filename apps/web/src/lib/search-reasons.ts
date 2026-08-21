export interface SearchReasonExplanation {
  title: string;
  description?: string;
  action?: {
    label: string;
    href: string;
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
    default:
      return { title: fallback };
  }
}
