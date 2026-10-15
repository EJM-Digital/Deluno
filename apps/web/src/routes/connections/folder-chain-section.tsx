import { useEffect, useState } from "react";
import { Loader2, RefreshCw } from "lucide-react";
import { Button } from "../../components/ui/button";
import { Chip } from "../../components/ui/chip";
import { DrawerSection } from "../../components/ui/drawer";
import { fetchJson } from "../../lib/api";

export type FolderChainLink = { step: string; status: "ok" | "problem" | "unknown" | string; message: string; path?: string | null };
export type FolderChainClient = { clientId: string; clientName: string; protocol: string; category: string; links: FolderChainLink[] };
export type FolderChainReport = {
  libraryId: string;
  libraryName: string;
  usesProcessor: boolean;
  processorName?: string | null;
  allMatch: boolean;
  clients: FolderChainClient[];
  libraryLinks: FolderChainLink[];
};

/** Longer than the server's own per-client and per-folder limits add up to for a normal setup. */
export const CHECK_TIMEOUT_MS = 45_000;
const CHECK_TIMED_OUT = "The folder check took too long and was stopped. A download client or a network folder may not be answering. Check that they are running, then check again.";

const LABEL: Record<string, string> = { ok: "Matches", problem: "Doesn't match", unknown: "Can't tell" };
const TONE: Record<string, "ok" | "bad" | "warn"> = { ok: "ok", problem: "bad", unknown: "warn" };

/**
 * A library's folders checked end to end (#616): each routed client's real
 * category folder, read from the client, against the library's downloads
 * folder, the processor's ready folder when there is one, and the library
 * root. Each link says whether it matches, and what to change when it does
 * not.
 */
export function FolderChainSection({ libraryId, refreshKey }: { libraryId: string; refreshKey?: unknown }) {
  const [report, setReport] = useState<FolderChainReport | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    let active = true;
    setBusy(true);
    // A new check never shows the last one beside its own result, or under its error.
    setReport(null);
    setFailed(null);
    // #1079: the check always finishes. Whatever the request does (a client
    // that never answers, a dropped connection, a promise that never settles),
    // it is abandoned after CHECK_TIMEOUT_MS and the section says so.
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timedOut = new Promise<never>((_, reject) => {
      timer = setTimeout(() => {
        controller.abort();
        reject(new Error(CHECK_TIMED_OUT));
      }, CHECK_TIMEOUT_MS);
    });
    void Promise.race([fetchJson<FolderChainReport>(`/api/libraries/${libraryId}/folder-chain`, { signal: controller.signal }), timedOut])
      .then((next) => { if (active) setReport(next); })
      .catch((error) => {
        if (!active) return;
        setFailed(error instanceof Error && error.name !== "AbortError" && error.message ? error.message : CHECK_TIMED_OUT);
      })
      .finally(() => {
        clearTimeout(timer);
        if (active) setBusy(false);
      });
    return () => { active = false; clearTimeout(timer); controller.abort(); };
  }, [libraryId, refreshKey, nonce]);

  const aside = busy ? "Checking…" : report ? (report.allMatch ? "Everything matches" : "Something doesn't match") : undefined;

  return (
    <DrawerSection title="Folder check" aside={aside}>
      <p className="text-(length:--type-caption) text-muted-foreground">
        Where each download client really saves this library's downloads, read from the client, and whether that is the folder
        {report?.usesProcessor ? ` ${report.processorName ?? "the processor"} and ` : " "}Deluno expect{report?.usesProcessor ? "" : "s"}. This checks the saved routing.
      </p>
      {failed ? <p role="alert" className="text-(length:--type-body-sm) text-destructive">{failed}</p> : null}
      {report ? (
        <ul className="grid gap-(--grid-gap)" aria-label="Folder check">
          {report.clients.map((client) =>
            client.links.map((link) => (
              <ChainLine key={`${client.clientId}-${link.step}`} link={link} title={`${client.clientName} · ${clientStepTitle(link.step, client.category)}`} />
            ))
          )}
          {report.libraryLinks.map((link) => (
            <ChainLine key={`library-${link.step}`} link={link} title={STEP_TITLE[link.step] ?? link.step} />
          ))}
        </ul>
      ) : null}
      <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => setNonce((value) => value + 1)} disabled={busy}>
        {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
        Check again
      </Button>
    </DrawerSection>
  );
}

/** What a client's line is called: where it downloads (in progress) is set beside where it finishes (#1164). */
export function clientStepTitle(step: string, category: string): string {
  if (step === "category") return `category ${category}`;
  if (step === "layout") return "file layout";
  if (step === "incomplete") return "where it downloads";
  if (step === "in-library") return "saves inside a library";
  return "where it finishes";
}

const STEP_TITLE: Record<string, string> = {
  clients: "Download clients",
  downloads: "Downloads folder",
  ready: "Ready folder",
  processor: "Folder the processor watches",
  "processor-output": "Folder the processor writes to",
  library: "Library folder"
};

function ChainLine({ link, title }: { link: FolderChainLink; title: string }) {
  return (
    <li className="grid gap-1 text-(length:--type-body-sm)">
      <span className="flex flex-wrap items-center gap-2">
        <Chip tone={TONE[link.status] ?? "idle"}>{LABEL[link.status] ?? link.status}</Chip>
        <span className="font-medium text-foreground">{title}</span>
      </span>
      <span className="text-muted-foreground">{link.message}</span>
    </li>
  );
}
