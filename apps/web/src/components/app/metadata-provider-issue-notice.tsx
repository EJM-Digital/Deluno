import { useId, useState } from "react";
import { CircleHelp, LoaderCircle, RefreshCw } from "lucide-react";
import type { MetadataProviderIssue } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { Button } from "../ui/button";
import { Card, CardContent } from "../ui/card";
import { toast } from "../shell/toaster";

interface MetadataProviderIssueNoticeProps {
  issue: MetadataProviderIssue | null;
  subjectLabel: "movie" | "show";
  acknowledgeUrl: string;
  onAcknowledged: () => void;
  onFindAnother: () => void;
  onRetry: () => void;
}

/**
 * A title-level metadata decision, not a system alert. The notice disappears
 * when acknowledged and stays gone while the provider evidence is unchanged.
 */
export function MetadataProviderIssueNotice({
  issue,
  subjectLabel,
  acknowledgeUrl,
  onAcknowledged,
  onFindAnother,
  onRetry
}: MetadataProviderIssueNoticeProps) {
  const [isAcknowledging, setIsAcknowledging] = useState(false);
  const headingId = useId();
  if (!issue || issue.acknowledgedUtc) return null;

  // A note about the episode list (#1090) is about a service that did not
  // answer, not about the provider dropping the title: different words, and
  // "find another match" does not apply to it.
  const catalogueNote = issue.kind === "catalogue-unreadable" || issue.kind === "catalogue-tvdb-unavailable";
  const heading = issue.kind === "catalogue-unreadable"
    ? `Deluno could not read this ${subjectLabel}'s episode list`
    : issue.kind === "catalogue-tvdb-unavailable"
      ? `This ${subjectLabel}'s episodes use TMDb's numbering for now`
      : `This ${subjectLabel} is no longer listed by ${issue.provider.toLowerCase() === "tmdb" ? "TMDb" : issue.provider}`;
  const body = issue.kind === "catalogue-unreadable"
    ? "The metadata service did not answer, so the episodes Deluno already has were left as they are. It tries again at the next refresh."
    : issue.kind === "catalogue-tvdb-unavailable"
      ? "TheTVDB did not answer when this show was added, so its episodes are numbered the way TMDb numbers them. Try again to see if TheTVDB has the list now."
      : "Deluno kept the title, monitoring, history, and files. You can keep the stored details, link another match, or try the provider again.";
  const dismissLabel = catalogueNote ? "Clear this note" : `Keep this ${subjectLabel}`;

  async function acknowledge() {
    setIsAcknowledging(true);
    try {
      const response = await authedFetch(acknowledgeUrl, { method: "POST" });
      if (!response.ok && response.status !== 204) throw new Error("metadata-issue-acknowledge-failed");
      toast.success(catalogueNote ? "The note was cleared." : `Kept this ${subjectLabel}. The metadata note was cleared.`);
      onAcknowledged();
    } catch {
      toast.error("That metadata note could not be cleared.");
    } finally {
      setIsAcknowledging(false);
    }
  }

  return (
    <Card as="section" aria-labelledby={headingId} className="border-hairline bg-surface-1">
      <CardContent className="flex flex-col gap-(--grid-gap) p-(--tile-pad) sm:flex-row sm:items-center sm:justify-between">
        <div className="flex min-w-0 items-start gap-3">
          <CircleHelp className="mt-0.5 h-5 w-5 shrink-0 text-muted-foreground" aria-hidden="true" />
          <div className="min-w-0">
            <p id={headingId} className="font-medium text-foreground">{heading}</p>
            <p className="mt-1 text-sm text-muted-foreground">
              {body}
            </p>
          </div>
        </div>
        <div className="flex shrink-0 flex-wrap gap-2">
          <Button type="button" variant="ghost" onClick={onRetry}>
            <RefreshCw className="h-4 w-4" aria-hidden="true" />
            Try again
          </Button>
          {catalogueNote ? null : <Button type="button" variant="outline" onClick={onFindAnother}>Find another match</Button>}
          <Button
            type="button"
            onClick={() => void acknowledge()}
            disabled={isAcknowledging}
            aria-busy={isAcknowledging}
          >
            {isAcknowledging ? <LoaderCircle className="h-4 w-4 animate-spin" aria-hidden="true" /> : null}
            {dismissLabel}
          </Button>
        </div>
      </CardContent>
      {/*
        The spinner is the only feedback this action gives, and a spinner is
        nothing at all to a screen reader: the button simply went quiet and
        stopped responding. Say what is happening.
      */}
      <p role="status" aria-live="polite" className="sr-only">
        {isAcknowledging ? (catalogueNote ? "Clearing the note." : `Keeping this ${subjectLabel}.`) : ""}
      </p>
    </Card>
  );
}
