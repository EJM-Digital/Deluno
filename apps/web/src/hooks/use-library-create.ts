import { useEffect, useRef, useState } from "react";
import type { MetadataSearchResult } from "../lib/api";

export type LibraryVariant = "movies" | "shows";

export type CreateFormDraft = {
  title: string;
  year: string;
  imdbId: string;
  monitored: boolean;
  /** Search for this title's missing content right away, once it is added (#793). Off by default. */
  searchForMissingNow: boolean;
  /** Search for this title's cutoff-unmet content right away, once it is added (#793). Off by default. */
  searchForCutoffUnmetNow: boolean;
  metadata: MetadataSearchResult | null;
};

export function createInitialLibraryForm(): CreateFormDraft {
  return {
    title: "",
    year: "",
    imdbId: "",
    monitored: true,
    searchForMissingNow: false,
    searchForCutoffUnmetNow: false,
    metadata: null
  };
}

export type IdPrefixedSearch = { source: "tmdb" | "imdb" | "tvdb"; id: string };

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
  const id = match[2].trim();
  if (!id) return null;
  return { source: match[1].toLowerCase() as IdPrefixedSearch["source"], id };
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

/** Keeps add-title state local to each movie or TV library screen. */
export function useLibraryCreate(variant: LibraryVariant, addRequested: boolean) {
  const [showCreate, setShowCreate] = useState(addRequested);
  const [isCreating, setIsCreating] = useState(false);
  const [createForm, setCreateForm] = useState(createInitialLibraryForm);
  const [metadataResults, setMetadataResults] = useState<MetadataSearchResult[]>([]);
  const [selectedMetadataResults, setSelectedMetadataResults] = useState<MetadataSearchResult[]>([]);
  const [isSearchingMetadata, setIsSearchingMetadata] = useState(false);
  const metadataSearchSequence = useRef(0);

  useEffect(() => {
    setCreateForm(createInitialLibraryForm());
  }, [variant]);

  useEffect(() => {
    if (addRequested) setShowCreate(true);
  }, [addRequested]);

  return {
    showCreate, setShowCreate, isCreating, setIsCreating, createForm, setCreateForm,
    metadataResults, setMetadataResults, selectedMetadataResults, setSelectedMetadataResults,
    isSearchingMetadata, setIsSearchingMetadata, metadataSearchSequence
  };
}
