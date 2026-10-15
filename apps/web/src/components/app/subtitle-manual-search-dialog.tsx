/**
 * #841's per-title subtitle panel: browse every ranked candidate by hand,
 * pick one, blacklist one a person judged wrong without ever downloading
 * it, or upload their own .srt/.ass. Shared between the movie page and a
 * show's episode drawer — both catalogues expose the identical
 * candidates/pick/blacklist/upload endpoints under their own base path.
 */
import { useEffect, useRef, useState } from "react";
import * as Dialog from "../ui/dialog-primitives";
import { Loader2, ShieldBan, Upload, X } from "lucide-react";
import { Button } from "../ui/button";
import { toast } from "../shell/toaster";
import { authedFetch } from "../../lib/use-auth";
import { fetchJson } from "../../lib/api";
import { describeRequestFailure } from "../../lib/search-reasons";
import { cn } from "../../lib/utils";
import type { SubtitleSearchCandidateItem } from "../../lib/api";

export function SubtitleManualSearchDialog({
  open,
  onOpenChange,
  basePath,
  language,
  onChanged
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** e.g. `/api/movies/{id}` or `/api/series/episodes/{episodeId}` */
  basePath: string;
  language: string;
  onChanged?: () => void;
}) {
  const [candidates, setCandidates] = useState<SubtitleSearchCandidateItem[] | null>(null);
  const [loadFailed, setLoadFailed] = useState<string | null>(null);
  const [busyToken, setBusyToken] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setCandidates(null);
    setLoadFailed(null);
    fetchJson<SubtitleSearchCandidateItem[]>(`${basePath}/subtitles/${language}/candidates`)
      .then((rows) => {
        if (!cancelled) setCandidates(rows);
      })
      .catch(() => {
        if (!cancelled) setLoadFailed("Deluno could not search for candidates. Check that Deluno is reachable, then try again.");
      });
    return () => {
      cancelled = true;
    };
  }, [open, basePath, language]);

  async function pick(candidate: SubtitleSearchCandidateItem) {
    setBusyToken(candidate.downloadToken);
    let response: Response | null = null;
    try {
      response = await authedFetch(`${basePath}/subtitles/${language}/candidates/pick`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ providerKey: candidate.providerKey, downloadToken: candidate.downloadToken })
      });
      const payload = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error("subtitle-candidate-pick-failed");
      toast.success(payload?.message ?? "That subtitle was written and is now held.");
      onChanged?.();
      onOpenChange(false);
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "download this subtitle" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusyToken(null);
    }
  }

  async function blacklist(candidate: SubtitleSearchCandidateItem) {
    setBusyToken(candidate.downloadToken);
    let response: Response | null = null;
    try {
      response = await authedFetch(`${basePath}/subtitles/${language}/blacklist`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          providerKey: candidate.providerKey,
          downloadToken: candidate.downloadToken,
          releaseName: candidate.releaseName,
          reason: "You blacklisted this candidate from manual search."
        })
      });
      if (!response.ok) throw new Error("subtitle-candidate-blacklist-failed");
      toast.success("That candidate is blacklisted and will never be offered again for this title.");
      setCandidates((current) => current?.filter((item) => item.downloadToken !== candidate.downloadToken) ?? null);
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "blacklist this candidate" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusyToken(null);
    }
  }

  async function uploadFile(file: File) {
    setUploading(true);
    let response: Response | null = null;
    try {
      const body = new FormData();
      body.append("file", file);
      response = await authedFetch(`${basePath}/subtitles/${language}/upload`, { method: "POST", body });
      const payload = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error("subtitle-upload-failed");
      toast.success(payload?.message ?? "Your subtitle was uploaded and is now held.");
      onChanged?.();
      onOpenChange(false);
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "upload this subtitle" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setUploading(false);
      if (fileInputRef.current) fileInputRef.current.value = "";
    }
  }

  const busy = busyToken !== null || uploading;

  return (
    <Dialog.Root open={open} onOpenChange={(next) => { if (!busy) onOpenChange(next); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px] data-[state=open]:animate-fade-in-stable" />
        <Dialog.Content
          className={cn(
            "fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2 -translate-y-1/2",
            "overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl dark:border-white/8",
            "data-[state=open]:animate-fade-in-stable"
          )}
          aria-describedby="subtitle-manual-search-description"
        >
          <div className="max-h-[80vh] overflow-y-auto p-8 sm:p-9">
            <div className="flex items-start justify-between gap-3">
              <div>
                <Dialog.Title className="text-(length:--type-title-md) font-semibold tracking-[-0.02em] text-foreground">
                  Search {language.toUpperCase()} subtitles by hand
                </Dialog.Title>
                <p id="subtitle-manual-search-description" className="mt-1 max-w-prose text-(length:--type-body) text-muted-foreground leading-relaxed">
                  Every candidate Deluno's providers found, ranked. Pick one to download it now, or blacklist one you know is wrong.
                </p>
              </div>
              <Dialog.Close asChild>
                <Button type="button" variant="ghost" size="sm" aria-label="Close manual subtitle search" disabled={busy}>
                  <X className="h-4 w-4" />
                </Button>
              </Dialog.Close>
            </div>

            <div className="mt-5 flex flex-col gap-2">
              {loadFailed ? (
                <p className="text-(length:--type-body-sm) text-destructive">{loadFailed}</p>
              ) : candidates === null ? (
                <div className="flex items-center gap-2 py-6 text-muted-foreground">
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Searching every provider…
                </div>
              ) : candidates.length === 0 ? (
                <p className="py-6 text-(length:--type-body-sm) text-muted-foreground">
                  No provider offered a {language.toUpperCase()} subtitle. Upload your own below.
                </p>
              ) : (
                candidates.map((candidate) => {
                  const candidateBusy = busyToken === candidate.downloadToken;
                  return (
                    <div
                      key={`${candidate.providerKey}-${candidate.downloadToken}`}
                      className="flex items-center justify-between gap-3 rounded-xl border border-hairline bg-surface-1 px-4 py-3"
                    >
                      <div className="min-w-0">
                        <p className="truncate text-(length:--type-body-sm) font-semibold text-foreground">
                          {candidate.releaseName ?? candidate.providerName}
                        </p>
                        <p className="truncate text-(length:--type-caption) text-muted-foreground">
                          {candidate.providerName} · {candidate.matchDescription}
                          {candidate.hearingImpaired ? " · Hearing-impaired" : ""}
                          {candidate.downloadCount !== null ? ` · ${candidate.downloadCount} downloads` : ""}
                        </p>
                      </div>
                      <div className="flex shrink-0 items-center gap-1.5">
                        <Button type="button" size="sm" disabled={busy} onClick={() => void pick(candidate)}>
                          {candidateBusy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                          Use this one
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          disabled={busy}
                          aria-label={`Blacklist ${candidate.releaseName ?? candidate.providerName}`}
                          onClick={() => void blacklist(candidate)}
                        >
                          <ShieldBan className="h-3.5 w-3.5" />
                        </Button>
                      </div>
                    </div>
                  );
                })
              )}
            </div>

            <div className="mt-6 border-t border-hairline pt-5">
              <p className="text-(length:--type-body-sm) font-semibold text-foreground">Or upload your own</p>
              <p className="mt-1 text-(length:--type-caption) text-muted-foreground">
                A .srt or .ass file, written through the same policy a fetched subtitle gets.
              </p>
              <div className="mt-3">
                <input
                  ref={fileInputRef}
                  type="file"
                  accept=".srt,.ass"
                  disabled={busy}
                  aria-label={`Upload a ${language.toUpperCase()} subtitle file`}
                  onChange={(event) => {
                    const file = event.target.files?.[0];
                    if (file) void uploadFile(file);
                  }}
                  className="hidden"
                  id="subtitle-manual-upload-input"
                />
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={busy}
                  onClick={() => fileInputRef.current?.click()}
                >
                  {uploading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Upload className="h-3.5 w-3.5" />}
                  Choose a file…
                </Button>
              </div>
            </div>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
