import { useEffect, useRef } from "react";

/**
 * A page that edits what a loader read keeps its own copy while the person types. When the loader reads
 * again (a live push, or a save somewhere else) and the person has no unsaved edits, the copy follows the
 * new data; with unsaved edits the copy is left alone. Runs only when the loaded value itself changes, so
 * the page's own save is never undone by the still-old loader data.
 */
export function useFollowLoaded<T>(loaded: T, dirty: boolean, adopt: (next: T) => void) {
  const previous = useRef(loaded);
  const dirtyNow = useRef(dirty);
  const adoptNow = useRef(adopt);
  dirtyNow.current = dirty;
  adoptNow.current = adopt;
  useEffect(() => {
    if (previous.current === loaded) return;
    previous.current = loaded;
    if (!dirtyNow.current) adoptNow.current(loaded);
  }, [loaded]);
}
