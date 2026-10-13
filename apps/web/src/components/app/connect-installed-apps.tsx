import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { useCoalescedRevalidate } from "../../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "../../lib/use-signalr";
import { CheckCircle2, Loader2 } from "lucide-react";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { LIST_TRACK, ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { Field } from "../ui/field";
import { Input } from "../ui/input";
import {
  fetchJson,
  type BundledComponentConnectAllResponse,
  type BundledComponentConnectRequest,
  type BundledComponentConnectResult,
  type BundledComponentStatus
} from "../../lib/api";

const WEIR_KIND = "weir";

/** Asks Deluno to connect one installed app now. Only Weir takes a body: the person's own account, used once. */
export function connectInstalledApp(kind: string, account?: BundledComponentConnectRequest): Promise<BundledComponentConnectResult> {
  return fetchJson<BundledComponentConnectResult>(
    `/api/system/bundled-components/${encodeURIComponent(kind)}/connect`,
    account
      ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(account) }
      : { method: "POST" }
  );
}

export function connectAllInstalledApps(): Promise<BundledComponentConnectAllResponse> {
  return fetchJson<BundledComponentConnectAllResponse>("/api/system/bundled-components/connect-all", { method: "POST" });
}

function appState(app: BundledComponentStatus): { tone: "ok" | "warn" | "idle"; label: string } {
  if (app.connected) return { tone: "ok", label: "Connected" };
  if (app.kind === WEIR_KIND && app.needsAccount) return { tone: "warn", label: "Needs your Weir account" };
  return { tone: "idle", label: "Not connected" };
}

/**
 * "Connect the apps you installed" (#1061, #968).
 *
 * Deluno connects nothing by itself after install. Each installed app gets its
 * own Connect button, and "Connect all" does the rest in one go. Weir is the
 * exception: it asks the person to create their own account (or sign in when it
 * already has one). Deluno uses that once and never keeps it, and no screen ever
 * shows an app's password. Renders nothing when no installed app applies.
 */
export function ConnectInstalledApps({
  category,
  hideConnected = false,
  onNeedsSignIn,
  onChange,
  onConnected
}: {
  /** Limit the list to one kind of app (the Connections and processor screens); the setup guide shows all. */
  category?: "download-client" | "processor";
  /** Once an app is connected it drops off the list, so a finished screen has nothing to nag about. */
  hideConnected?: boolean;
  /** Deluno could not sign in: open the existing connection form with the host and port filled in. */
  onNeedsSignIn?: (app: BundledComponentStatus, result: BundledComponentConnectResult) => void;
  /** The installed apps and whether each is connected, after every load and connect. */
  onChange?: (apps: BundledComponentStatus[]) => void;
  /** An app has just become connected, so the screen's own list of connections can reload and show it (#1130). */
  onConnected?: () => void;
}) {
  const [apps, setApps] = useState<BundledComponentStatus[] | null>(null);
  const [messages, setMessages] = useState<Record<string, BundledComponentConnectResult>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [weirOpen, setWeirOpen] = useState(false);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [weirError, setWeirError] = useState<string | null>(null);

  const connectedBefore = useRef<number | null>(null);
  const refresh = useCallback(async () => {
    try {
      const next = await fetchJson<BundledComponentStatus[]>("/api/system/bundled-components");
      setApps(next);
      // The screen's own list of connections reloads each time one more app is connected.
      const connectedNow = next.filter((app) => app.installed && app.connected).length;
      if (connectedBefore.current !== null && connectedNow > connectedBefore.current) onConnected?.();
      connectedBefore.current = connectedNow;
    } catch {
      // Keep what is already on screen.
    }
  }, [onConnected]);

  // Each app shows as connected the moment it is, not when the last one is: the server says when
  // one is wired (#1179), so nothing asks on a timer while Connect all runs.
  const reload = useCoalescedRevalidate(() => { void refresh(); }, 200);
  useSignalREvent("BundledComponentChanged", RealtimeGroups.Dashboard, reload);

  useEffect(() => {
    let active = true;
    void fetchJson<BundledComponentStatus[]>("/api/system/bundled-components")
      .then((next) => {
        if (!active) return;
        setApps(next);
        connectedBefore.current = next.filter((app) => app.installed && app.connected).length;
      })
      .catch(() => { if (active) setApps(null); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (apps) onChange?.(apps.filter((app) => app.installed && (!category || app.category === category)));
  }, [apps, category, onChange]);

  const installed = (apps ?? []).filter((app) => app.installed && (!category || app.category === category));
  if (installed.length === 0) return null;
  const waiting = installed.filter((app) => !app.connected);
  const said = installed.filter((app) => messages[app.kind]);
  // Everything is connected: a finished screen has nothing to list. After a Connect it says so in one line.
  if (hideConnected && waiting.length === 0) {
    return said.length > 0 ? (
      <p className="mb-3 flex items-center gap-2 text-(length:--type-caption) text-success" role="status">
        <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden />
        {installed.length === 1 ? `${installed[0]!.displayName} is connected.` : "All the apps you installed are connected."}
      </p>
    ) : null;
  }

  const weir = installed.find((app) => app.kind === WEIR_KIND);
  const creating = Boolean(weir?.needsAccount) && !weir?.accountExists;
  function record(result: BundledComponentConnectResult, kind: string) {
    setMessages((current) => ({ ...current, [kind]: result }));
  }

  async function connect(app: BundledComponentStatus) {
    if (app.kind === WEIR_KIND) {
      setWeirOpen((open) => !open);
      setWeirError(null);
      return;
    }
    setBusy(app.kind);
    try {
      const result = await connectInstalledApp(app.kind);
      record(result, app.kind);
      await refresh();
      if (!result.healthy && result.needsSignIn) onNeedsSignIn?.(app, result);
    } catch (error) {
      record({ name: app.displayName, healthy: false, message: error instanceof Error ? error.message : `${app.displayName} could not be connected.` }, app.kind);
    } finally {
      setBusy(null);
    }
  }

  async function connectAll() {
    setBusy("all");
    setWeirError(null);
    try {
      const response = await connectAllInstalledApps();
      for (const result of response.results) {
        record(result, result.kind ?? result.name.toLowerCase());
      }
      await refresh();
    } catch (error) {
      setWeirError(error instanceof Error ? error.message : "The apps could not be connected.");
    } finally {
      setBusy(null);
    }
  }

  async function submitWeir() {
    if (!username.trim() || !password) {
      setWeirError("Enter a username and password for your Weir account.");
      return;
    }
    if (creating && password !== confirm) {
      setWeirError("The two passwords do not match.");
      return;
    }
    setBusy(WEIR_KIND);
    setWeirError(null);
    try {
      const result = await connectInstalledApp(WEIR_KIND, { username: username.trim(), password });
      record(result, WEIR_KIND);
      if (result.healthy) {
        setWeirOpen(false);
        setUsername("");
      }
      await refresh();
    } catch (error) {
      setWeirError(error instanceof Error ? error.message : "Weir could not be connected.");
    } finally {
      // Used once, never kept: whatever happened, the passwords leave the screen.
      setPassword("");
      setConfirm("");
      setBusy(null);
    }
  }

  return (
    <div className="mb-3" aria-label="Connect the apps you installed" role="region">
      <ListCard
        title="Connect the apps you installed"
        titleCase={false}
        count={waiting.length > 0 ? `${waiting.length} waiting` : "all connected"}
        actions={waiting.length > 0 ? (
          <Button type="button" size="sm" variant="outline" disabled={busy !== null} onClick={() => void connectAll()}>
            {busy === "all" ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden /> : null}
            Connect all
          </Button>
        ) : undefined}
      >
        <p className="border-b border-hairline px-(--card-pad-x) py-2 text-(length:--type-caption) text-muted-foreground">
          Deluno connects nothing by itself. Press Connect for each app, or Connect all.
        </p>
        <ListTable
          listId="connect-installed-apps"
          chevron={false}
          columns={[
            { id: "app", label: "App" },
            { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, mobileWidth: "7.5rem" },
            { id: "action", label: "Action", width: LIST_TRACK.action, srOnly: true, mobile: true, mobileWidth: "6rem" }
          ]}
        >
          {installed.map((app) => {
            const state = appState(app);
            return (
              <ListRow key={app.kind}>
                <ListNameCell name={app.displayName} />
                <ListCell mobile>
                  <Chip tone={state.tone}>{state.label}</Chip>
                </ListCell>
                <ListCell mobile align="end">
                  {!app.connected ? (
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={busy !== null}
                      className="w-full"
                      aria-label={`Connect ${app.displayName}`}
                      aria-expanded={app.kind === WEIR_KIND ? weirOpen : undefined}
                      onClick={() => void connect(app)}
                    >
                      {busy === app.kind ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden /> : null}
                      {/* The row already names the app; a phone has room only for the verb. */}
                      <span className="md:hidden">Connect</span>
                      <span className="hidden md:inline">Connect {app.displayName}</span>
                    </Button>
                  ) : null}
                </ListCell>
              </ListRow>
            );
          })}
        </ListTable>
        {said.length > 0 || (weirError && !weirOpen) ? (
          <div className="grid gap-1 border-t border-hairline px-(--card-pad-x) py-2">
            {said.map((app) => {
              const message = messages[app.kind]!;
              return (
                <p key={app.kind} className={message.healthy ? "text-(length:--type-caption) text-success" : "text-(length:--type-caption) text-destructive"} role={message.healthy ? "status" : "alert"}>
                  {message.message}
                  {!message.healthy && message.needsSignIn ? (
                    onNeedsSignIn ? (
                      <Button type="button" size="sm" variant="ghost" className="ml-2" onClick={() => onNeedsSignIn(app, message)}>
                        Enter your own login for {app.displayName}
                      </Button>
                    ) : (
                      <Link className="ml-2 font-medium text-primary hover:underline" to="/indexers/download-clients">
                        Enter your own login for {app.displayName}
                      </Link>
                    )
                  ) : null}
                </p>
              );
            })}
            {weirError && !weirOpen ? <p className="text-(length:--type-caption) text-destructive" role="alert">{weirError}</p> : null}
          </div>
        ) : null}
        {weir && weirOpen && !weir.connected ? (
          // Not a <form>: the guided setup renders this panel inside its own step form, and a nested
          // form sends the submit to the outer one (which reloaded the page).
          <div
            role="group"
            className="grid gap-2 border-t border-hairline p-(--card-pad-x)"
            aria-label="Your Weir account"
            onKeyDown={(event) => {
              if (event.key === "Enter" && event.target instanceof HTMLInputElement) {
                event.preventDefault();
                void submitWeir();
              }
            }}
          >
            <p className="text-(length:--type-caption) text-muted-foreground">
              {creating
                ? "Weir has no account yet. Choose your own username and password. Deluno uses them once to set Weir up and never keeps them."
                : "Weir already has an account. Sign in with it. Deluno uses it once to connect and never keeps it."}
            </p>
            <Field label="Weir username">
              <Input value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="username" />
            </Field>
            <Field label="Weir password">
              <Input type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete={creating ? "new-password" : "current-password"} />
            </Field>
            {creating ? (
              <Field label="Confirm Weir password">
                <Input type="password" value={confirm} onChange={(event) => setConfirm(event.target.value)} autoComplete="new-password" />
              </Field>
            ) : null}
            {weirError ? <p className="text-(length:--type-caption) text-destructive" role="alert">{weirError}</p> : null}
            {busy === WEIR_KIND ? (
              <p className="text-(length:--type-caption) text-muted-foreground" role="status">
                Connecting to Weir. If Weir is still starting, this can take a few minutes after an update.
              </p>
            ) : null}
            <Button type="button" size="sm" className="w-max" disabled={busy !== null} onClick={() => void submitWeir()}>
              {creating ? "Create Weir account and connect" : "Sign in to Weir and connect"}
            </Button>
          </div>
        ) : null}
      </ListCard>
    </div>
  );
}
