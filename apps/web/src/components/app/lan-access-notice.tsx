import { useEffect, useState } from "react";
import { Loader2, ShieldAlert } from "lucide-react";
import { Button } from "../ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../ui/card";
import { fetchJson } from "../../lib/api";

export type LanAccessStatus = {
  state: "open" | "blocked" | "notChecked" | string;
  port: number | null;
  ruleName: string | null;
  message: string;
  canAllow: boolean;
};

/**
 * Says when Windows Firewall turns other devices away, with the one step that
 * lets them in (#623). Nothing shows while they can reach Deluno, or on a host
 * with no firewall for Deluno to look after.
 */
export function LanAccessNotice({ showResult = false, address }: {
  /** Setup asks the question, so it shows the answer too (#1141): reachable or not, and the address to use from another device. */
  showResult?: boolean;
  address?: string;
} = {}) {
  const [status, setStatus] = useState<LanAccessStatus | null>(null);
  const [checked, setChecked] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let active = true;
    void fetchJson<LanAccessStatus>("/api/system/lan-access")
      .then((next) => { if (active) { setStatus(next); setChecked(true); } })
      .catch(() => { if (active) { setStatus(null); setChecked(true); } });
    return () => { active = false; };
  }, []);

  async function allow() {
    setBusy(true);
    try {
      setStatus(await fetchJson<LanAccessStatus>("/api/system/lan-access/allow", { method: "POST" }));
    } catch (error) {
      setStatus((current) => current && {
        ...current,
        message: error instanceof Error ? error.message : "Deluno could not ask Windows to change the firewall."
      });
    } finally {
      setBusy(false);
    }
  }

  if (showResult && !checked) {
    return (
      <p role="status" className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
        Checking whether other devices can reach Deluno...
      </p>
    );
  }

  if (showResult && (!status || status.state !== "blocked")) {
    const where = status?.port && address ? ` From another device, open http://${address}:${status.port}.` : "";
    return (
      <p role="status" aria-label="Other devices on your network" className="rounded-xl border border-hairline bg-surface-2 p-3 text-sm text-foreground">
        {!status
          ? "Deluno could not check whether other devices can reach it. Nothing is blocked that Deluno can see."
          : status.state === "open"
            ? `Other devices can reach Deluno. ${status.message}${where}`
            : `Nothing to allow here. ${status.message}`}
      </p>
    );
  }

  if (!status || status.state !== "blocked") {
    return null;
  }

  return (
    <Card role="status" aria-label="Other devices on your network">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldAlert className="h-4 w-4 text-warning" aria-hidden />
          Other devices cannot reach Deluno
        </CardTitle>
        <CardDescription>{status.message}</CardDescription>
      </CardHeader>
      {status.canAllow ? (
        <CardContent className="flex flex-wrap items-center gap-3">
          <Button onClick={() => void allow()} disabled={busy}>
            {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> : null}
            Allow other devices
          </Button>
          <span className="text-sm text-muted-foreground">
            Windows asks for permission on the computer Deluno runs on.
          </span>
        </CardContent>
      ) : null}
    </Card>
  );
}
