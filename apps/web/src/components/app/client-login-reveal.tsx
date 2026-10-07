import { useState } from "react";
import { Loader2 } from "lucide-react";
import { fetchJson } from "../../lib/api";
import { Button } from "../ui/button";
import { DrawerSection } from "../ui/drawer";

type Login = { address: string; username: string | null; secret: string | null; secretLabel: string };

/** A client that runs on this computer: the only kind whose login Deluno will show (#1142). */
export function isOnThisComputer(host: string): boolean {
  const value = host.trim().toLowerCase();
  return value === "localhost" || value === "::1" || value === "[::1]" || value.startsWith("127.");
}

/**
 * "Open this client" (#1142): the client's own web address and a way to show the login Deluno holds for it,
 * so a person can sign in to a client the installer put on. Nothing is shown until they ask.
 */
export function ClientLoginReveal({ clientId, name }: { clientId: string; name: string }) {
  const [login, setLogin] = useState<Login | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  async function reveal() {
    setBusy(true);
    setProblem(null);
    try {
      setLogin(await fetchJson<Login>(`/api/download-clients/${encodeURIComponent(clientId)}/reveal-login`, { method: "POST" }));
    } catch (error) {
      setProblem(error instanceof Error ? error.message : "Deluno could not show this login.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <DrawerSection title={`Sign in to ${name}`} aside="its own web page">
      {login ? (
        <dl className="grid gap-1 text-sm" aria-label={`${name} login`}>
          <div className="flex flex-wrap gap-2">
            <dt className="text-muted-foreground">Address</dt>
            <dd><a className="font-medium text-primary hover:underline" href={login.address} target="_blank" rel="noreferrer">{login.address}</a></dd>
          </div>
          <div className="flex flex-wrap gap-2">
            <dt className="text-muted-foreground">Username</dt>
            <dd className="font-mono">{login.username ?? "none needed"}</dd>
          </div>
          <div className="flex flex-wrap gap-2">
            <dt className="text-muted-foreground">{login.secretLabel}</dt>
            <dd className="font-mono break-all">{login.secret ?? "none saved"}</dd>
          </div>
        </dl>
      ) : (
        <p className="text-sm text-muted-foreground">
          {name} has its own web page, which only opens on this computer. Deluno holds the login it uses, and shows it only when you ask.
        </p>
      )}
      {problem ? <p role="alert" className="text-sm text-destructive">{problem}</p> : null}
      <div className="flex flex-wrap gap-2">
        {login ? (
          <Button type="button" variant="outline" size="sm" onClick={() => setLogin(null)}>Hide the login</Button>
        ) : (
          <Button type="button" variant="outline" size="sm" onClick={() => void reveal()} disabled={busy}>
            {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> : null}
            Show the login for {name}
          </Button>
        )}
      </div>
    </DrawerSection>
  );
}
