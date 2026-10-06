import type { ConnectionVersion } from "../../lib/connection-versions";
import { cn } from "../../lib/utils";

/**
 * A connection's own version, in the row's secondary style (#1117): "4.6.7", and when a newer stable release is
 * out "4.6.7 · 5.0.2 available" with the "available" part in the info tone, linked to the release page when
 * Deluno has one. Nothing at all when the connection reported no version, and no "available" part when the
 * newer version is not known.
 */
export function ConnectionVersionText({ info, className }: { info: ConnectionVersion | null | undefined; className?: string }) {
  if (!info?.version) return null;
  const update = info.updateAvailable && info.latestVersion ? `${info.latestVersion} available` : null;
  return (
    <span className={cn("font-normal text-muted-foreground", className)}>
      {info.version}
      {update ? (
        <>
          {" · "}
          {info.releaseUrl ? (
            <a
              href={info.releaseUrl}
              target="_blank"
              rel="noreferrer noopener"
              className="text-info hover:underline"
              aria-label={`${update}: read the release notes`}
              onClick={(event) => event.stopPropagation()}
            >
              {update}
            </a>
          ) : (
            <span className="text-info">{update}</span>
          )}
        </>
      ) : null}
    </span>
  );
}

/**
 * The version set beside a connection's name in its list row: a little muted text after the name. `fallback` is a
 * version read some other way (a download client's live reading) used only when no stored one has arrived yet.
 */
export function ConnectionVersionBeside({ info, fallback }: { info: ConnectionVersion | null | undefined; fallback?: string | null }) {
  const shown = info ?? (fallback ? ({ kind: "", id: "", version: fallback, latestVersion: null, updateAvailable: false, releaseUrl: null } satisfies ConnectionVersion) : null);
  if (!shown?.version) return null;
  return <ConnectionVersionText info={shown} className="ml-2 text-(length:--type-caption)" />;
}
