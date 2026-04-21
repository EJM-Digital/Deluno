export function getSeriesDomainSummary() {
  return {
    name: "series",
    ownedConcepts: [
      "shows",
      "seasons",
      "episodes",
      "specials",
      "alternate orderings",
      "season pack logic",
      "episode import rules"
    ],
    forbiddenConcepts: [
      "movie editions",
      "movie-only release semantics",
      "collection-only movie logic"
    ],
    firstVerticalSlice: [
      "add show",
      "monitor show",
      "list shows",
      "stub episode search pipeline",
      "stub episode import pipeline"
    ]
  };
}

