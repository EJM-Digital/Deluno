import { useEffect, useMemo, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { KeyRound, LockKeyhole, Plus, ShieldAlert, Wifi, X } from "lucide-react";
import { Link } from "react-router-dom";
import { plainAddress } from "../lib/utils";
import { fetchJson, type AllowedHostsOverview, type LocalNetworkAuthOverview } from "../lib/api";
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
  localNetworkAuth: LocalNetworkAuthOverview;
}

export async function systemSecurityLoader(): Promise<SystemSecurityLoaderData> {
  const [allowedHosts, localNetworkAuth] = await Promise.all([
    fetchJson<AllowedHostsOverview>("/api/security/allowed-hosts"),
    fetchJson<LocalNetworkAuthOverview>("/api/security/local-network-auth")
  ]);
  return { allowedHosts, localNetworkAuth };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { areas: [ConfigAreas.security] };

export function SystemSecurityPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { allowedHosts, localNetworkAuth } = useLoaderData() as SystemSecurityLoaderData;
  const revalidator = useRevalidator();

  return (
    <div className="flex flex-col gap-(--page-gap)">
      <AllowedHostsCard overview={allowedHosts} onSaved={() => revalidator.revalidate()} />
      <LocalNetworkAuthCard overview={localNetworkAuth} onSaved={() => revalidator.revalidate()} />
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
    // An address just added is listed at once, marked unsaved, until it is saved or unticked.
    for (const host of selected) {
      if (seen.has(host.toLowerCase())) continue;
      seen.add(host.toLowerCase());
      combined.push({ host, reason: "Added by hand, not saved yet" });
    }
    return combined;
  }, [overview.detectedHosts, overview.allowedHosts, selected]);

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
        <CardContent className="space-y-(--page-gap)">
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

/**
 * "Skip sign-in for devices on your home network" (#993). Off by default
 * (#936). The allow list, when it has entries, narrows the trusted set
 * further -- only listed addresses skip sign-in, still only if they are
 * also local; empty means every local address does, as designed.
 */
function LocalNetworkAuthCard({ overview, onSaved }: { overview: LocalNetworkAuthOverview; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(overview.enabled);
  const [allowList, setAllowList] = useState<string[]>(overview.allowList);
  const [newEntry, setNewEntry] = useState("");
  const [entryError, setEntryError] = useState<string | null>(null);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

  const dirty = enabled !== overview.enabled || JSON.stringify([...allowList].sort()) !== JSON.stringify([...overview.allowList].sort());

  function addEntry() {
    const trimmed = newEntry.trim();
    if (!trimmed) return;
    if (!isValidAddressOrSubnet(trimmed)) {
      setEntryError(`"${trimmed}" is not a valid address or subnet. Use an IP address (192.168.1.20) or a CIDR range (192.168.1.0/28).`);
      return;
    }
    setEntryError(null);
    if (!allowList.some((item) => item.toLowerCase() === trimmed.toLowerCase())) {
      setAllowList((current) => [...current, trimmed]);
    }
    setNewEntry("");
  }

  function removeEntry(entry: string) {
    setAllowList((current) => current.filter((item) => item.toLowerCase() !== entry.toLowerCase()));
  }

  async function save() {
    if (saveState === "saving") return;
    setSaveState("saving");
    setMessage(null);
    try {
      await fetchJson("/api/security/local-network-auth", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          enabled,
          additionalLocalNetworks: overview.additionalLocalNetworks,
          allowList,
          trustedProxies: overview.trustedProxies
        })
      });
      setSaveState("saved");
      setMessage(enabled ? "Devices on your local network now skip sign-in." : "Every device signs in again, including ones on your local network.");
      onSaved();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "This could not be saved.");
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
            <Wifi className="h-4 w-4 text-primary" />
            Skip sign-in for devices on your home network
          </CardTitle>
          <CardDescription>
            Off, every device signs in, exactly as Deluno has always done. On, a device Deluno can prove is on your local network (not one behind a reverse proxy from outside) is already signed in -- no password needed on your own network.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-(--page-gap)">
          <SwitchRow
            label="Skip sign-in on the local network"
            description="Counts as local: private network addresses (10.x, 172.16-31.x, 192.168.x), this machine (loopback), link-local addresses, and IPv6 private (ULA) addresses."
            checked={enabled}
            onCheckedChange={setEnabled}
          />

          <p className="text-xs text-muted-foreground">
            You are connecting from <span className="font-medium text-foreground">{plainAddress(overview.currentAddress)}</span>, which
            {overview.currentAddressIsLocal ? " is a local address" : " is not a local address"}
            {enabled && overview.currentAddressIsLocal ? (overview.currentAddressTrusted ? " and would skip sign-in." : " but is not on your allow list below, so it would still sign in.") : "."}
          </p>

          {enabled && !overview.allowedHostsEnabled ? (
            <p className="rounded-lg border border-warning/35 bg-warning/10 px-3 py-2 text-xs leading-relaxed text-warning">
              Allowed Hosts (above) is off. Deluno currently answers on any address, so any device that can reach a local address here skips sign-in too. Turn on Allowed Hosts as well for this to only apply on your own network.
            </p>
          ) : null}

          {enabled ? (
            <div className="space-y-2">
              <p className="text-sm font-semibold text-foreground">Narrow this to specific addresses (optional)</p>
              <p className="text-xs text-muted-foreground">
                Leave empty and every local device skips sign-in. Add addresses here and only those skip sign-in -- still only if they are also local.
              </p>
              <div className="space-y-1">
                {allowList.map((entry) => (
                  <div key={entry} className="flex items-center justify-between gap-2 rounded-lg border border-hairline bg-surface-1 px-3 py-2">
                    <span className="text-sm text-foreground">{entry}</span>
                    <Button type="button" variant="ghost" size="sm" onClick={() => removeEntry(entry)} aria-label={`Remove ${entry} from the allow list`}>
                      <X className="h-4 w-4" />
                    </Button>
                  </div>
                ))}
              </div>
              <div className="flex flex-wrap items-end gap-2">
                <Field label="Add an address or subnet" help="192.168.1.20 or 192.168.1.0/28" className="min-w-[16rem] flex-1">
                  <Input
                    value={newEntry}
                    onChange={(event) => {
                      setNewEntry(event.target.value);
                      setEntryError(null);
                    }}
                    placeholder="192.168.1.0/28"
                    onKeyDown={(event) => {
                      if (event.key === "Enter") {
                        event.preventDefault();
                        addEntry();
                      }
                    }}
                  />
                </Field>
                <Button type="button" variant="outline" onClick={addEntry} disabled={!newEntry.trim()} aria-label="Add address to the allow list">
                  <Plus className="h-4 w-4" />
                  Add
                </Button>
              </div>
              {entryError ? <p className="text-xs text-destructive">{entryError}</p> : null}
            </div>
          ) : null}
        </CardContent>
      </Card>

      <PageFooter
        state={footerState}
        message={message}
        saveLabel="Save"
        saveType="button"
        disabled={!dirty || saveState === "saving"}
        onSave={() => void save()}
      />
    </>
  );
}

/** Same IP/CIDR shape the backend accepts (LocalNetworkMatcher.TryParseCidr) -- kept simple so an obviously wrong entry is refused before it ever reaches the server. */
function isValidAddressOrSubnet(value: string): boolean {
  const [address, prefix] = value.split("/");
  if (prefix !== undefined && !/^\d+$/.test(prefix)) return false;
  const isIPv4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.test(address);
  if (isIPv4) {
    const octets = address.split(".").map(Number);
    if (octets.some((octet) => octet < 0 || octet > 255)) return false;
    if (prefix !== undefined && Number(prefix) > 32) return false;
    return true;
  }
  // IPv6: a permissive shape check -- the server is the source of truth for full validation.
  const isIPv6 = /^[0-9a-fA-F:]+$/.test(address) && address.includes(":");
  if (isIPv6) {
    if (prefix !== undefined && Number(prefix) > 128) return false;
    return true;
  }
  return false;
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
