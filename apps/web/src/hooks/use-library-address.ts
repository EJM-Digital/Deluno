import { useCallback, useEffect } from "react";
import { useSearchParams } from "react-router-dom";

/**
 * The library picker lives in the address (#1103): `?library=<id>` opens a page filtered to a library, and picking one
 * writes it back, replacing the entry rather than adding a step to Back.
 *
 * `knownIds` is null until the libraries have loaded. Once they have, an id that no longer exists falls back to All
 * libraries, and a library shown from memory (not from the address) is written to the address, so the link is the
 * page as it is.
 */
export function useLibraryAddress(
  libraryId: string | null,
  pickLibrary: (next: string | null) => void,
  knownIds: readonly string[] | null
): (next: string | null) => void {
  const [searchParams, setSearchParams] = useSearchParams();

  const changeLibrary = useCallback((next: string | null) => {
    pickLibrary(next);
    setSearchParams((current) => {
      if ((current.get("library") ?? null) === next) return current;
      const params = new URLSearchParams(current);
      if (next) params.set("library", next);
      else params.delete("library");
      return params;
    }, { replace: true });
  }, [pickLibrary, setSearchParams]);

  const inAddress = searchParams.get("library");
  const known = knownIds === null ? null : knownIds.join("|");
  useEffect(() => {
    if (knownIds === null) return;
    if (libraryId && !knownIds.includes(libraryId)) {
      changeLibrary(null);
    } else if (libraryId && inAddress !== libraryId) {
      setSearchParams((current) => {
        const params = new URLSearchParams(current);
        params.set("library", libraryId);
        return params;
      }, { replace: true });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [known, libraryId, inAddress]);

  return changeLibrary;
}
