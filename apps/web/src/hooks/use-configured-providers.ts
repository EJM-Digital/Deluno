import { useEffect, useState } from "react";

import { fetchJson, type MetadataProviderStatus } from "../lib/api";

/**
 * Which metadata providers are actually set up.
 *
 * <p>The View drawer offers a switch per rating source, and three of the four —
 * IMDb, Rotten Tomatoes, Metacritic — come from OMDb, which is optional. With
 * no key they draw a dash on every card. A switch that can only ever do nothing
 * looks exactly like a switch that is broken, and on the rig all three read as
 * defects until the drawer started saying which they were.</p>
 *
 * <p>Fails quiet: if the status cannot be read, nothing is claimed to be
 * missing. Telling somebody their key is absent because a request failed would
 * be worse than saying nothing.</p>
 */
export function useConfiguredProviders() {
  const [configured, setConfigured] = useState<string[]>([]);

  useEffect(() => {
    let active = true;

    void fetchJson<MetadataProviderStatus>("/api/metadata/status")
      .then((status) => {
        if (!active) return;
        setConfigured((status.sources ?? []).filter((source) => source.isConfigured).map((source) => source.source));
      })
      .catch(() => {
        // Deliberately silent. See above.
      });

    return () => {
      active = false;
    };
  }, []);

  return configured;
}
