import { useCallback, useEffect, useState } from "react";
import { Loader2 } from "lucide-react";
import { fetchJson } from "../../lib/api";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { DrawerSection } from "../ui/drawer";
import { Field, FieldRow } from "../ui/field";
import { Input } from "../ui/input";
import { SwitchRow } from "../ui/switch";

type ClientStatus = { clientId: string; clientName: string; hasProvider: boolean; known: boolean };
type ClientResult = { clientId: string; clientName: string; saved: boolean; message: string };

/**
 * "Your Usenet provider" (#1142): SABnzbd and NZBGet download nothing until they have a news server, and the
 * server can only be typed into the client itself. This takes it once and Deluno writes it to every Usenet
 * client it manages. The password is passed on and is never kept or shown again.
 */
export function UsenetProviderPanel({ clientId, onSaved }: { clientId?: string; onSaved?: () => void }) {
  const [statuses, setStatuses] = useState<ClientStatus[] | null>(null);
  const [host, setHost] = useState("");
  const [port, setPort] = useState("563");
  const [useSsl, setUseSsl] = useState(true);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [connections, setConnections] = useState("8");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [results, setResults] = useState<ClientResult[] | null>(null);

  const refresh = useCallback(async () => {
    try {
      const next = await fetchJson<{ clients: ClientStatus[] }>("/api/download-clients/usenet-provider");
      setStatuses(next.clients);
    } catch {
      setStatuses(null);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const mine = statuses?.find((item) => item.clientId === clientId) ?? statuses?.[0] ?? null;

  async function save() {
    setBusy(true);
    setProblem(null);
    setResults(null);
    try {
      const response = await fetchJson<{ saved: boolean; clients: ClientResult[] }>("/api/download-clients/usenet-provider", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          host: host.trim(),
          port: Number(port),
          useSsl,
          username: username.trim() || null,
          password: password || null,
          connections: Number(connections)
        })
      });
      setResults(response.clients);
      setPassword("");
      await refresh();
      onSaved?.();
    } catch (error) {
      setProblem(error instanceof Error ? error.message : "Deluno could not save the provider.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <DrawerSection title="Your Usenet provider" aside="written to SABnzbd and NZBGet">
      <div className="flex flex-wrap items-center gap-2">
        {mine ? (
          mine.known ? (
            <Chip tone={mine.hasProvider ? "ok" : "warn"}>{mine.hasProvider ? "Provider set up" : "No Usenet provider set up"}</Chip>
          ) : (
            <Chip tone="idle">Could not check</Chip>
          )
        ) : null}
        <p className="text-sm text-muted-foreground">
          Usenet clients download nothing until they have a news server. Enter the details your provider gave you once and Deluno writes them to every Usenet client it manages.
        </p>
      </div>
      <FieldRow>
        <Field label="News server address" help="For example news.example.com">
          <Input value={host} onChange={(event) => setHost(event.target.value)} autoComplete="off" placeholder="news.example.com" />
        </Field>
        <Field label="News server port" help="563 for a secure connection, 119 for a plain one.">
          <Input type="number" value={port} onChange={(event) => setPort(event.target.value)} />
        </Field>
      </FieldRow>
      <SwitchRow label="Secure connection" description="Use SSL to the news server. Most providers expect this on port 563." checked={useSsl} onCheckedChange={setUseSsl} />
      <FieldRow>
        <Field label="Provider username" optional>
          <Input value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="off" />
        </Field>
        <Field label="Provider password" optional help="Passed to your clients and not kept by Deluno.">
          <Input type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="new-password" />
        </Field>
      </FieldRow>
      <Field label="Connections" help="How many your provider's plan allows.">
        <Input type="number" value={connections} onChange={(event) => setConnections(event.target.value)} />
      </Field>
      {problem ? <p role="alert" className="text-sm text-destructive">{problem}</p> : null}
      {results ? (
        <ul className="grid gap-1 text-sm" aria-label="Where the provider was saved">
          {results.map((result) => (
            <li key={result.clientId} className={result.saved ? "text-foreground" : "text-destructive"}>{result.message}</li>
          ))}
        </ul>
      ) : null}
      <div>
        <Button type="button" onClick={() => void save()} disabled={busy || !host.trim()}>
          {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> : null}
          Save provider to my Usenet clients
        </Button>
      </div>
    </DrawerSection>
  );
}
