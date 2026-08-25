/**
 * Guard against losing edits: blocks in-app navigation (react-router data
 * router) and the browser unload while `dirty` is true.
 *
 * Returns the blocker for callers that need lower-level control. Normal pages
 * register with the shared UnsavedChangesProvider, which renders the modal
 * navigation decision for them.
 */
import { createContext, useCallback, useContext, useEffect, useId, useRef, type MutableRefObject } from "react";
import { useBlocker, type BlockerFunction } from "react-router-dom";

export type NavigationBlocker = ReturnType<typeof useBlocker>;

export interface UnsavedChangesRegistration {
  id: string;
  dirty: boolean;
  blocker: NavigationBlocker;
  description?: string;
  saveRef: MutableRefObject<(() => void | Promise<void>) | undefined>;
}

export interface UnsavedChangesContextValue {
  register: (registration: UnsavedChangesRegistration) => void;
  unregister: (id: string) => void;
}

export const UnsavedChangesContext = createContext<UnsavedChangesContextValue | null>(null);

export function useUnsavedChanges(dirty: boolean, description?: string, save?: () => void | Promise<void>) {
  // These guards protect editor routes, not in-page search/hash changes. The
  // router's boolean form keeps the guard current as soon as the editor turns
  // dirty and avoids capturing an older form snapshot in a callback.
  const shouldBlock = useCallback<BlockerFunction>(
    ({ currentLocation, nextLocation }) => dirty && currentLocation.pathname !== nextLocation.pathname,
    [dirty]
  );
  const blocker = useBlocker(shouldBlock);
  const context = useContext(UnsavedChangesContext);
  const id = useId();
  const saveRef = useRef<(() => void | Promise<void>) | undefined>(undefined);
  saveRef.current = save;

  useEffect(() => {
    if (!context) return;
    context.register({ id, dirty, blocker, description, saveRef });
    return () => context.unregister(id);
  }, [blocker, context, description, dirty, id]);

  // A successful form submission makes the route safe to leave. When the
  // navigation was already blocked, continue it immediately after the dirty
  // state clears rather than making the user click the same menu twice.
  useEffect(() => {
    if (blocker.state === "blocked" && !dirty) blocker.proceed();
  }, [blocker, blocker.state, dirty]);

  useEffect(() => {
    if (!dirty) return;
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Chrome requires returnValue to be set for the prompt to show.
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [dirty]);

  return blocker;
}
