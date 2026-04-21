export function getMovieDomainSummary() {
  return {
    name: "movies",
    ownedConcepts: [
      "movies",
      "editions",
      "quality profiles",
      "grab history",
      "import history",
      "movie naming rules"
    ],
    forbiddenConcepts: [
      "seasons",
      "episodes",
      "anime absolute numbering",
      "season packs"
    ],
    firstVerticalSlice: [
      "add movie",
      "monitor movie",
      "list movies",
      "stub search pipeline",
      "stub import pipeline"
    ]
  };
}

