import { useEffect, useRef, useState } from "react";
import type { MetadataSearchResult } from "../lib/api";

export type LibraryVariant = "movies" | "shows";

/**
 * Which episodes a newly added show starts out monitoring (#991), mirroring
 * Sonarr's own Add Series presets. Only meaningful for shows — movies have no
 * episodes, so this is ignored for the "movies" variant. "all" is the same
 * request as sending nothing at all (today's fully-monitored behaviour).
 */
export type ShowMonitorPreset = "all" | "future" | "missing" | "firstSeason" | "recent" | "pilot" | "none";

export const SHOW_MONITOR_PRESETS: { value: ShowMonitorPreset; label: string; help: string }[] = [
  { value: "all", label: "All episodes", help: "Every season and episode, including ones already out." },
  { value: "future", label: "Future episodes only", help: "Nothing that has already aired." },
  { value: "missing", label: "Missing episodes only", help: "Only episodes Deluno doesn't already hold a file for." },
  { value: "firstSeason", label: "First season only", help: "Only the earliest season, everything after stays unmonitored." },
  { value: "recent", label: "Latest season only", help: "Only the newest season Deluno knows about." },
  { value: "pilot", label: "Pilot only", help: "Only the very first episode." },
  { value: "none", label: "None", help: "Add the show, but don't monitor or search for anything yet." }
];

export type CreateFormDraft = {
  title: string;
  year: string;
  imdbId: string;
  monitored: boolean;
  /** Which episodes to start monitoring for a new show (#991). Ignored for movies. */
  monitorPreset: ShowMonitorPreset;
  /**
   * Search for it as soon as it is added (#1145). The one choice: ticked searches straight away, unticked
   * leaves it for the next scheduled search. Monitoring alone no longer starts a search.
   */
  searchNow: boolean;
  /** The quality profile chosen in the dialog (#1145). Empty follows the chosen library's own profile. */
  qualityProfileId: string;
  /** Tags to put on the title as it is added (#1038), comma-separated. Empty means none. */
  tags: string;
  metadata: MetadataSearchResult | null;
};

export function createInitialLibraryForm(): CreateFormDraft {
  return {
    title: "",
    year: "",
    imdbId: "",
    monitored: true,
    monitorPreset: "all",
    searchNow: true,
    qualityProfileId: "",
    tags: "",
    metadata: null
  };
}

const REMEMBERED_TAGS_KEY = "deluno:add-tags:";

/** The tags last used when adding into this library (#1038), so the picker starts where the person left off. */
export function readRememberedAddTags(libraryId: string | null | undefined): string {
  if (!libraryId) return "";
  try {
    return window.localStorage.getItem(REMEMBERED_TAGS_KEY + libraryId) ?? "";
  } catch {
    return "";
  }
}

export function rememberAddTags(libraryId: string | null | undefined, tags: string) {
  if (!libraryId) return;
  try {
    if (tags.trim()) window.localStorage.setItem(REMEMBERED_TAGS_KEY + libraryId, tags);
    else window.localStorage.removeItem(REMEMBERED_TAGS_KEY + libraryId);
  } catch {
    // Remembering is a convenience; adding still works without it.
  }
}

export type IdPrefixedSearch ={ source: "tmdb" | "imdb" | "tvdb"; id: string };

/**
 * Recognizes an explicit `tmdb:<id>`, `imdb:<id>` or `tvdb:<id>` prefix typed
 * into the add-title search box (#792), the same syntax Sonarr's add-series
 * search accepts. A recognized prefix is an identity assertion - it is routed
 * to a direct id lookup instead of a title search - so this only matches text
 * that starts with one of the three prefixes, never a bare id or a title that
 * happens to contain a colon.
 */
export function parseIdPrefixedSearch(text: string): IdPrefixedSearch | null {
  const match = /^(tmdb|imdb|tvdb):\s*(\S+)$/i.exec(text.trim());
  if (!match) return null;
  return { source: match[1].toLowerCase() as IdPrefixedSearch["source"], id: match[2] };
}

export function metadataCreatePayload(metadata: MetadataSearchResult | null) {
  if (!metadata) return {};
  // `libraryEntryId` is this install's answer right now, not a fact about the
  // title, so it is stripped before the rest is stored as the entry's metadata.
  // Keeping it would freeze "you already have this" into the row that is the
  // having.
  const { libraryEntryId: _libraryEntryId, ...storedMetadata } = metadata;
  return {
    metadataProvider: metadata.provider,
    metadataProviderId: metadata.providerId,
    originalTitle: metadata.originalTitle,
    overview: metadata.overview,
    posterUrl: metadata.posterUrl,
    backdropUrl: metadata.backdropUrl,
    rating: metadata.rating,
    genres: metadata.genres.join(", "),
    externalUrl: metadata.externalUrl,
    metadataJson: JSON.stringify(storedMetadata)
  };
}

const ADD_DRAFT_KEY = "deluno:add-draft:";

type AddDraft = {
  createForm: CreateFormDraft;
  metadataResults: MetadataSearchResult[];
  selectedMetadataResults: MetadataSearchResult[];
  /** When it was set aside; an old one is not brought back. */
  savedAt?: number;
};

/**
 * Sets the add screen's search aside before the person leaves it to look at a title they already
 * hold (#1507), so coming back shows the same search text, matches and selections.
 */
export function setAsideAddDraft(variant: LibraryVariant, draft: AddDraft) {
  try {
    window.sessionStorage.setItem(ADD_DRAFT_KEY + variant, JSON.stringify({ ...draft, savedAt: Date.now() }));
  } catch {
    // The add screen still works without it; the search just starts over.
  }
}

function peekAddDraft(variant: LibraryVariant): AddDraft | null {
  try {
    const stored = window.sessionStorage.getItem(ADD_DRAFT_KEY + variant);
    if (!stored) return null;
    const draft = JSON.parse(stored) as AddDraft;
    return Date.now() - (draft.savedAt ?? 0) < 60 * 60 * 1000 ? draft : null;
  } catch {
    return null;
  }
}

export function clearAddDraft(variant: LibraryVariant) {
  try {
    window.sessionStorage.removeItem(ADD_DRAFT_KEY + variant);
  } catch {
    // Nothing to clear.
  }
}

// The set-aside search stays until the add screen is closed, because the library screen can mount more than once on the way back.
/** Keeps add-title state local to each movie or TV library screen. */
export function useLibraryCreate(variant: LibraryVariant, addRequested: boolean) {
  const [restored] = useState(() => (addRequested ? peekAddDraft(variant) : null));
  const [showCreate, setShowCreate] = useState(addRequested);
  const [isCreating, setIsCreating] = useState(false);
  const [createForm, setCreateForm] = useState(() => restored?.createForm ?? createInitialLibraryForm());
  const [metadataResults, setMetadataResults] = useState<MetadataSearchResult[]>(() => restored?.metadataResults ?? []);
  const [selectedMetadataResults, setSelectedMetadataResults] = useState<MetadataSearchResult[]>(() => restored?.selectedMetadataResults ?? []);
  const [isSearchingMetadata, setIsSearchingMetadata] = useState(false);
  const metadataSearchSequence = useRef(0);
  /** The search text that came back with the set-aside matches; asking again would replace them. */
  const restoredSearchTitle = useRef<string | null>(restored ? restored.createForm.title.trim() : null);
  const firstRun = useRef(true);

  useEffect(() => {
    if (firstRun.current && restored) {
      firstRun.current = false;
      return;
    }
    firstRun.current = false;
    setCreateForm(createInitialLibraryForm());
  }, [variant, restored]);

  useEffect(() => {
    if (addRequested) setShowCreate(true);
  }, [addRequested]);

  return {
    showCreate, setShowCreate, isCreating, setIsCreating, createForm, setCreateForm,
    metadataResults, setMetadataResults, selectedMetadataResults, setSelectedMetadataResults,
    isSearchingMetadata, setIsSearchingMetadata, metadataSearchSequence, restoredSearchTitle
  };
}
