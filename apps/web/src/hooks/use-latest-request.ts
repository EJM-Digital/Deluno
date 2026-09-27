import { useCallback, useRef } from "react";

/**
 * Guards against a response that arrives after a newer request has already
 * superseded it.
 *
 * Filed under #934: click a chip, then click a different one before the
 * first click's response lands, and the first response must never be the one
 * that ends up on screen — whichever finishes last otherwise wins, which is
 * how a fast double-click on Movies/TV could leave the wrong filter's titles
 * showing even after everything had "settled".
 *
 * `next()` returns a token for "the request that starts now" and retires
 * every earlier one in the same move, so a caller never has to compare
 * tokens by hand. `isStale(token)` is the check to make right before turning
 * a response into state — after an await, always, since that is the only
 * place a newer request could have started in the meantime.
 */
export function useLatestRequest() {
  const currentRef = useRef(0);

  const next = useCallback(() => {
    currentRef.current += 1;
    return currentRef.current;
  }, []);

  const isStale = useCallback((token: number) => token !== currentRef.current, []);

  return { next, isStale };
}
