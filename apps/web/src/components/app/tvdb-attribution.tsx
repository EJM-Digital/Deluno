import { cn } from "../../lib/utils";

/**
 * TheTVDB's terms require attribution with a direct link to thetvdb.com to
 * anyone who views metadata that came from it. Shown wherever a TV title's
 * details or search matches are on screen (#989).
 */
export function TvdbAttribution({ className }: { className?: string }) {
  return (
    <p className={cn("text-xs text-muted-foreground", className)}>
      TV metadata provided by{" "}
      <a
        href="https://thetvdb.com"
        target="_blank"
        rel="noreferrer noopener"
        className="font-medium text-foreground underline underline-offset-2 hover:text-primary"
      >
        TheTVDB
      </a>
    </p>
  );
}
