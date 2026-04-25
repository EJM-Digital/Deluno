import type { ActiveDownload, IndexerHealthItem, MediaItem, MediaStatus, MediaType } from "./media-types";
export type { ActiveDownload, IndexerHealthItem, MediaItem, MediaStatus, MediaType } from "./media-types";

export interface UpcomingRelease {
  id: string;
  day: string;
  title: string;
  episode: string;
  dateLabel: string;
  network: string;
  poster: string;
}

const movieSeed = [
  ["dune-part-two", "Dune: Part Two", 2024, "Bluray-2160p", "downloaded", 87.6, 8.7, ["Sci-Fi", "Adventure"], "2 days ago"],
  ["anora", "Anora", 2024, "WEB-DL 2160p", "downloaded", 22.4, 8.1, ["Drama", "Comedy"], "6 hours ago"],
  ["conclave", "Conclave", 2024, "Bluray-1080p", "monitored", 15.2, 7.9, ["Thriller", "Drama"], "Yesterday"],
  ["challengers", "Challengers", 2024, "WEB-DL 2160p", "downloading", 19.8, 7.4, ["Drama", "Sport"], "1 hour ago"],
  ["the-substance", "The Substance", 2024, "Bluray-2160p", "missing", 24.3, 7.6, ["Horror", "Sci-Fi"], "3 days ago"],
  ["civil-war", "Civil War", 2024, "WEB-DL 2160p", "downloaded", 18.9, 7.3, ["Action", "Drama"], "4 days ago"],
  ["past-lives", "Past Lives", 2023, "Bluray-1080p", "downloaded", 11.4, 8.0, ["Romance", "Drama"], "1 week ago"],
  ["poor-things", "Poor Things", 2023, "Bluray-2160p", "downloaded", 28.6, 8.2, ["Fantasy", "Comedy"], "2 weeks ago"],
  ["zone-of-interest", "The Zone of Interest", 2023, "Bluray-1080p", "monitored", 12.1, 7.8, ["Drama", "War"], "3 days ago"],
  ["perfect-days", "Perfect Days", 2023, "WEB-DL 1080p", "downloaded", 9.4, 7.9, ["Drama"], "5 days ago"],
  ["furiosa", "Furiosa", 2024, "WEB-DL 2160p", "downloading", 31.7, 7.5, ["Action", "Adventure"], "3 hours ago"],
  ["sing-sing", "Sing Sing", 2024, "WEB-DL 1080p", "missing", 13.8, 8.4, ["Drama"], "Today"]
] as const;

const showSeed = [
  ["severance", "Severance", 2022, "WEB-DL 2160p", "downloaded", 42.7, 8.7, ["Sci-Fi", "Thriller"], "Today", "Apple TV+"],
  ["shogun", "Shōgun", 2024, "WEB-DL 2160p", "downloaded", 54.1, 8.8, ["Drama", "History"], "Yesterday", "FX"],
  ["the-bear", "The Bear", 2022, "WEB-DL 2160p", "downloaded", 24.6, 8.5, ["Drama", "Comedy"], "2 days ago", "FX"],
  ["slow-horses", "Slow Horses", 2022, "WEB-DL 2160p", "monitored", 33.9, 8.3, ["Thriller", "Drama"], "1 week ago", "Apple TV+"],
  ["fallout", "Fallout", 2024, "WEB-DL 2160p", "downloaded", 48.4, 8.4, ["Sci-Fi", "Action"], "4 days ago", "Prime Video"],
  ["silo", "Silo", 2023, "WEB-DL 2160p", "monitored", 31.8, 8.1, ["Sci-Fi", "Mystery"], "6 days ago", "Apple TV+"],
  ["andor", "Andor", 2022, "Bluray-2160p", "downloaded", 61.1, 8.4, ["Sci-Fi", "Adventure"], "2 weeks ago", "Disney+"],
  ["succession", "Succession", 2023, "Bluray-1080p", "downloaded", 38.5, 8.9, ["Drama"], "1 month ago", "HBO"],
  ["ripley", "Ripley", 2024, "WEB-DL 2160p", "missing", 29.2, 8.0, ["Thriller", "Drama"], "5 hours ago", "Netflix"],
  ["house-of-dragon", "House of the Dragon", 2024, "WEB-DL 2160p", "downloading", 57.8, 8.3, ["Fantasy", "Drama"], "Today", "HBO"],
  ["mr-and-mrs-smith", "Mr. & Mrs. Smith", 2024, "WEB-DL 2160p", "downloaded", 26.8, 7.6, ["Action", "Comedy"], "3 days ago", "Prime Video"],
  ["true-detective-night-country", "True Detective: Night Country", 2024, "WEB-DL 1080p", "monitored", 18.5, 7.1, ["Crime", "Mystery"], "1 week ago", "HBO"]
] as const;

function mediaFromSeed(
  type: MediaType,
  row: readonly [string, string, number, string, MediaStatus, number, number, readonly string[], string, string?]
): MediaItem {
  const [slug, title, year, quality, status, sizeGb, rating, genres, added, network] = row;
  const hash = Array.from(slug).reduce((sum, char) => sum + char.charCodeAt(0), 0);
  const releaseGroups = ["NTb", "FraMeSToR", "FLUX", "DON", "CtrlHD", "EVO", "KiNGS"];
  const sources = ["WEB-DL", "Bluray", "Remux", "HDTV"];
  const codecs = ["H.264", "H.265", "AV1"];
  const audioCodecs = ["AAC", "DD+", "DTS-HD MA", "TrueHD Atmos"];
  const audioChannels = ["2.0", "5.1", "7.1"];
  const languages = ["English", "Japanese", "Korean", "Spanish"];
  const hdrFormats = ["SDR", "HDR10", "HDR10+", "Dolby Vision"];
  const tagSets = [
    ["4K", "priority"],
    ["upgrades"],
    ["anime"],
    ["family"],
    ["criterion"],
    ["dolby-vision"]
  ];
  return {
    id: slug,
    title,
    year,
    type,
    poster: `https://picsum.photos/seed/${slug}/400/600`,
    backdrop: `https://picsum.photos/seed/${slug}-wide/1200/700`,
    quality,
    status,
    monitored: status !== "missing",
    sizeGb,
    rating,
    genres: [...genres],
    added,
    network,
    overview: `${title} is tracked inside Deluno with quality targeting, upgrade logic, and full operational context across downloads, indexers, and history.`,
    bitrateMbps: Number((8 + (hash % 340) / 10).toFixed(1)),
    releaseGroup: releaseGroups[hash % releaseGroups.length],
    tags: tagSets[hash % tagSets.length],
    source: sources[hash % sources.length],
    codec: codecs[hash % codecs.length],
    audioCodec: audioCodecs[hash % audioCodecs.length],
    audioChannels: audioChannels[hash % audioChannels.length],
    language: languages[hash % languages.length],
    hdrFormat: hdrFormats[hash % hdrFormats.length],
    releaseStatus: status === "missing" ? "Wanted" : status === "downloading" ? "Downloading" : "Available"
  };
}

export const movies: MediaItem[] = movieSeed.map((row) => mediaFromSeed("movie", row));
export const shows: MediaItem[] = showSeed.map((row) => mediaFromSeed("show", row));
export const allMedia: MediaItem[] = [...movies, ...shows];

export const activeDownloads: ActiveDownload[] = [
  {
    id: "challengers",
    title: "Challengers",
    poster: movies[3].poster,
    quality: "WEB-DL 2160p",
    progress: 74,
    speedMbps: 18.2,
    etaMinutes: 7,
    peers: 29,
    indexer: "TorrentGalaxy"
  },
  {
    id: "furiosa",
    title: "Furiosa",
    poster: movies[10].poster,
    quality: "WEB-DL 2160p",
    progress: 41,
    speedMbps: 22.9,
    etaMinutes: 18,
    peers: 43,
    indexer: "1337x"
  },
  {
    id: "house-of-dragon",
    title: "House of the Dragon",
    poster: shows[9].poster,
    quality: "WEB-DL 2160p",
    progress: 63,
    speedMbps: 14.6,
    etaMinutes: 11,
    peers: 58,
    indexer: "Torrentio"
  },
  {
    id: "ripley",
    title: "Ripley",
    poster: shows[8].poster,
    quality: "WEB-DL 2160p",
    progress: 18,
    speedMbps: 26.4,
    etaMinutes: 24,
    peers: 17,
    indexer: "Nyaa"
  }
];

export const recentlyAdded: MediaItem[] = [...movies.slice(0, 6), ...shows.slice(0, 4)];

export const upcomingReleases: UpcomingRelease[] = [
  {
    id: "silo-s02e08",
    day: "Today",
    title: "Silo",
    episode: "S02E08",
    dateLabel: "Today · 9:00 PM",
    network: "Apple TV+",
    poster: shows[5].poster ?? ""
  },
  {
    id: "severance-s02e10",
    day: "Today",
    title: "Severance",
    episode: "S02E10",
    dateLabel: "Today · 11:00 PM",
    network: "Apple TV+",
    poster: shows[0].poster ?? ""
  },
  {
    id: "the-bear-s04e01",
    day: "Tomorrow",
    title: "The Bear",
    episode: "S04E01",
    dateLabel: "Tomorrow · 8:00 PM",
    network: "FX",
    poster: shows[2].poster ?? ""
  },
  {
    id: "slow-horses-s05e03",
    day: "Friday",
    title: "Slow Horses",
    episode: "S05E03",
    dateLabel: "Fri · 10:00 PM",
    network: "Apple TV+",
    poster: shows[3].poster ?? ""
  },
  {
    id: "andor-s02e04",
    day: "Saturday",
    title: "Andor",
    episode: "S02E04",
    dateLabel: "Sat · 7:00 PM",
    network: "Disney+",
    poster: shows[6].poster ?? ""
  }
];

export const indexerHealth: IndexerHealthItem[] = [
  { id: "torrentio", name: "Torrentio", status: "healthy", responseMs: 142 },
  { id: "1337x", name: "1337x", status: "healthy", responseMs: 218 },
  { id: "nyaa", name: "Nyaa", status: "degraded", responseMs: 684 },
  { id: "torrentgalaxy", name: "TorrentGalaxy", status: "healthy", responseMs: 191 },
  { id: "rarbg-mirror", name: "RARBG Mirror", status: "down", responseMs: 0 },
  { id: "publichd", name: "PublicHD", status: "degraded", responseMs: 731 }
];

export const librarySparkline = [42, 44, 43, 46, 47, 49, 51, 52, 53, 55, 57, 59, 60, 61, 64];
export const monitorSparkline = [110, 112, 114, 114, 116, 118, 119, 121, 125, 128, 132, 141, 148, 156, 168];
export const healthSparkline = [91, 92, 94, 95, 94, 95, 96, 97, 97, 98, 97, 98, 99, 97, 97];

export function getLibraryItems(variant: "movies" | "shows" | "all") {
  if (variant === "movies") {
    return movies;
  }
  if (variant === "shows") {
    return shows;
  }
  return allMedia;
}
