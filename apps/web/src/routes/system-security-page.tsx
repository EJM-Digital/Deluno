import { useEffect, useMemo, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { KeyRound, LockKeyhole, Plus, ShieldAlert } from "lucide-react";
import { Link } from "react-router-dom";
import { fetchJson, type AllowedHostsOverview } from "../lib/api";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { CheckboxRow } from "../components/ui/checkbox";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { SwitchRow } from "../components/ui/switch";
import { PageFooter } from "../components/ui/page-footer";
import type { DrawerSaveState } from "../components/ui/drawer";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

interface SystemSecurityLoaderData {
  allowedHosts: AllowedHostsOverview;
}

export async function systemSecurityLoader(): Promise<SystemSecurityLoaderData> {
  const allowedHosts = await fetchJson<AllowedHostsOverview>("/api/security/allowed-hosts");
  return { allowedHosts };
}

export function SystemSecurityPage() {
  const { allowedHosts } = useLoaderData() as SystemSecurityLoaderData;
  const revalidator = useRevalidator();

  return (
    <div className="flex flex-col gap-[var(--page-gap)]">
      <AllowedHostsCard overview={allowedHosts} onSaved={() => revalidator.revalidate()} />
      <SignInCard />
      <ApiKeysCard />
    </div>
  );
}

/**
 * The Allowed Hosts card is where #939 lives. Every address on it was
 * detected -- the current one, this machine's name, its LAN IPs, localhost,
 * and anything seen forwarded from a reverse proxy or Docker -- so ticking
 * one never requires knowing what a hostname is. Adding a custom address
 * (a reverse-proxy domain, a wildcard like *.example.com) is the only typing
 * involved, and is optional.
 */
function AllowedHostsCard({ overview, onSaved }: { overview: AllowedHostsOverview; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(overview.enabled);
  const [selected, setSelected] = useState<string[]>(overview.allowedHosts);
  const [customHost, setCustomHost] = useState("");
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

  // Detected addresses plus anything already saved (which may include a
  // custom entry, or one Deluno no longer detects because the network
  // changed) -- so a saved choice never silently vanishes from the list.
  const rows = useMemo(() => {
    const seen = new Set<string>();
    const combined: { host: string; reason: string }[] = [];
    for (const candidate of overview.detectedHosts) {
      if (seen.has(candidate.host.toLowerCase())) continue;
      seen.add(candidate.host.toLowerCase());
      combined.push(candidate);
    }
    for (const host of overview.allowedHosts) {
      if (seen.has(host.toLowerCase())) continue;
      seen.add(host.toLowerCase());
      combined.push({ host, reason: "Added by hand" });
    }
    return combined;
  }, [overview.detectedHosts, overview.allowedHosts]);

  const dirty = enabled !== overview.enabled || JSON.stringify([...selected].sort()) !== JSON.stringify([...overview.allowedHosts].sort());
  const currentHostChecked = selected.some((host) => host.toLowerCase() === overview.currentHost.toLowerCase());
  const wouldLockOut = enabled && !currentHostChecked && overview.currentHost.toLowerCase() !== "localhost";

  function toggle(host: string, checked: boolean) {
    setSelected((current) => (checked ? [...current, host] : current.filter((item) => item.toLowerCase() !== host.toLowerCase())));
  }

  function addCustomHost() {
    const trimmed = customHost.trim();
    if (!trimmed) return;
    if (!selected.some((host) => host.toLowerCase() === trimmed.toLowerCase())) {
      setSelected((current) => [...current, trimmed]);
    }
    setCustomHost("");
  }

  async function save() {
    if (saveState === "saving") return;
    setSaveState("saving");
    setMessage(null);
    try {
      await fetchJson("/api/security/allowed-hosts", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled, allowedHosts: selected })
      });
      setSaveState("saved");
      setMessage(enabled ? "Deluno now only answers on the addresses you ticked." : "Deluno answers on any address again.");
      onSaved();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Allowed Hosts could not be saved.");
    }
  }

  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  return (
    <>
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <ShieldAlert className="h-4 w-4 text-primary" />
            Allowed Hosts
          </CardTitle>
          <CardDescription>
            Without this, Deluno answers a request no matter which address it was sent to -- which is how a web page open in your browser can be tricked into talking to it. Tick the addresses you actually use, and Deluno refuses the rest.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-[var(--page-gap)]">
          <SwitchRow
            label="Only answer on the addresses you choose"
            description="Off is exactly what Deluno has always done: answer on any address."
            checked={enabled}
            onCheckedChange={setEnabled}
          />

          <div className="space-y-1">
            {rows.map((row) => (
              <CheckboxRow
                key={row.host}
                label={row.host}
                description={row.reason}
                checked={selected.some((host) => host.toLowerCase() === row.host.toLowerCase())}
                onCheckedChange={(checked) => toggle(row.host, checked)}
              />
            ))}
          </div>

          <div className="flex flex-wrap items-end gap-2">
            <Field label="Add an address" help="A reverse-proxy domain, or a wildcard subdomain like *.example.com." className="min-w-[16rem] flex-1">
              <Input
                value={customHost}
                onChange={(event) => setCustomHost(event.target.value)}
                placeholder="*.example.com"
                onKeyDown={(event) => {
                  if (event.key === "Enter") {
                    event.preventDefault();
                    addCustomHost();
                  }
                }}
              />
            </Field>
            <Button type="button" variant="outline" onClick={addCustomHost} disabled={!customHost.trim()}>
              <Plus className="h-4 w-4" />
              Add
            </Button>
          </div>

          <p className="text-xs text-muted-foreground">
            localhost and 127.0.0.1 on this machine always work, whatever is ticked here -- there is no way to lock yourself out of Deluno on its own computer. If a browser somewhere else is refused, sign in on an address that is still allowed and add the new one here, or edit the config file / environment variable directly, or use the Windows tray.
          </p>

          {wouldLockOut ? (
            <p className="rounded-lg border border-warning/35 bg-warning/10 px-3 py-2 text-xs leading-relaxed text-warning">
              The address you're using right now ({overview.currentHost}) is not ticked. Saving with it turned on would be refused rather than lock you out.
            </p>
          ) : null}
        </CardContent>
      </Card>

      <PageFooter
        state={footerState}
        message={message}
        saveLabel="Save Allowed Hosts"
        saveType="button"
        disabled={!dirty || saveState === "saving"}
        onSave={() => void save()}
      />
    </>
  );
}

function SignInCard() {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <LockKeyhole className="h-4 w-4 text-primary" />
          Sign-in & password
        </CardTitle>
        <CardDescription>Deluno is single-user: one password protects everything it can do.</CardDescription>
      </CardHeader>
      <CardContent>
        <p className="text-sm leading-relaxed text-muted-foreground">
          Change your password from the account menu, top right of any page.
        </p>
      </CardContent>
    </Card>
  );
}

function ApiKeysCard() {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <KeyRound className="h-4 w-4 text-primary" />
          API keys
        </CardTitle>
        <CardDescription>Separate credentials for scripts, dashboards and other integrations, each scoped to only what it needs.</CardDescription>
      </CardHeader>
      <CardContent>
        <Button type="button" variant="outline" asChild>
          <Link to="/system/api">
            <KeyRound className="h-4 w-4" />
            Manage API keys
          </Link>
        </Button>
      </CardContent>
    </Card>
  );
}
