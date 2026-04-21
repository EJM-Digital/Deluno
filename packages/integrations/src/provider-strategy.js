export function getProviderStrategy() {
  return {
    principle: "providers are replaceable and media-type aware",
    currentRecommendation: {
      movies: {
        primaryMetadata: "pluggable",
        identity: ["imdb"],
        notes: "Keep movie metadata abstract so we can validate UX before committing to a commercial provider path."
      },
      series: {
        primaryMetadata: "tv-native provider",
        identity: ["imdb"],
        notes: "Series operational metadata should remain separate from ratings and enrichment concerns."
      }
    },
    providerInterfaces: [
      "MovieMetadataProvider",
      "SeriesMetadataProvider",
      "RatingsProvider",
      "DownloadClientAdapter",
      "IndexerAdapter"
    ]
  };
}
