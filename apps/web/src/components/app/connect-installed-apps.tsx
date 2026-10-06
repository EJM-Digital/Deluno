import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { CheckCircle2, Loader2 } from "lucide-react";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
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
  onChange
}: {
  /** Limit the list to one kind of app (the Connections and processor screens); the setup guide shows all. */
  category?: "download-client" | "processor";
  /** Once an app is connected it drops off the list, so a finished screen has nothing to nag about. */
  hideConnected?: boolean;
  /** Deluno could not sign in: open the existing connection form with the host and port filled in. */
  onNeedsSignIn?: (app: BundledComponentStatus, result: BundledComponentConnectResult) => void;
  /** The installed apps and whether each is connected, after every load and connect. */
  onChange?: (apps: BundledComponentStatus[]) => void;
}) {
  const [apps, setApps] = useState<BundledComponentStatus[] | null>(null);
  const [messages, setMessages] = useState<Record<string, BundledComponentConnectResult>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [weirOpen, setWeirOpen] = useState(false);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [weirError, setWeirError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setApps(await fetchJson<BundledComponentStatus[]>("/api/system/bundled-components"));
    } catch {
      // Keep what is already on screen.
    }
  }, []);

  useEffect(() => {
    let active = true;
    void fetchJson<BundledComponentStatus[]>("/api/system/bundled-components")
      .then((next) => { if (active) setApps(next); })
      .catch(() => { if (active) setApps(null); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (apps) onChange?.(apps.filter((app) => app.installed && (!category || app.category === category)));
  }, [apps, category, onChange]);

  const installed = (apps ?? []).filter((app) => app.installed && (!category || app.category === category));
  const visible = hideConnected ? installed.filter((app) => !app.connected || messages[app.kind]) : installed;
  if (visible.length === 0) return null;

  const weir = installed.find((app) => app.kind === WEIR_KIND);
  const creating = Boolean(weir?.needsAccount) && !weir?.accountExists;
  const waiting = installed.filter((app) => !app.connected);

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

  async function submitWeir(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
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
    <section className="mb-3 grid gap-2 rounded-md border border-dashed border-border/70 bg-muted/30 px-3 py-2 text-sm" aria-label="Connect the apps you installed">
      <div className="flex flex-wrap items-center gap-2">
        <CheckCircle2 className="h-4 w-4 shrink-0 text-success" aria-hidden />
        <h3 className="font-medium text-foreground">Connect the apps you installed</h3>
        {waiting.length > 0 ? (
          <Button type="button" size="sm" variant="outline" className="ml-auto" disabled={busy !== null} onClick={() => void connectAll()}>
            {busy === "all" ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden /> : null}
            Connect all
          </Button>
        ) : null}
      </div>
      <p className="text-(length:--type-caption) text-muted-foreground">
        Deluno connects nothing by itself. Press Connect for each app, or Connect all.
      </p>
      <ul className="grid gap-2">
        {visible.map((app) => {
          const state = appState(app);
          const message = messages[app.kind];
          return (
            <li key={app.kind} className="grid gap-1">
              <div className="flex flex-wrap items-center gap-2">
                <span className="min-w-0 font-medium text-foreground">{app.displayName}</span>
                <Chip tone={state.tone}>{state.label}</Chip>
                {!app.connected ? (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    className="ml-auto"
                    disabled={busy !== null}
                    aria-expanded={app.kind === WEIR_KIND ? weirOpen : undefined}
                    onClick={() => void connect(app)}
                  >
                    {busy === app.kind ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden /> : null}
                    Connect {app.displayName}
                  </Button>
                ) : null}
              </div>
              {message ? (
                <p className={message.healthy ? "text-(length:--type-caption) text-success" : "text-(length:--type-caption) text-destructive"} role={message.healthy ? "status" : "alert"}>
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
              ) : null}
              {app.kind === WEIR_KIND && weirOpen && !app.connected ? (
                <form onSubmit={(event) => void submitWeir(event)} className="grid gap-2 rounded-md border border-hairline bg-card p-3" aria-label="Your Weir account">
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
                  <Button type="submit" size="sm" className="w-max" disabled={busy !== null}>
                    {creating ? "Create Weir account and connect" : "Sign in to Weir and connect"}
                  </Button>
                </form>
              ) : null}
            </li>
          );
        })}
      </ul>
      {weirError && !weirOpen ? <p className="text-(length:--type-caption) text-destructive" role="alert">{weirError}</p> : null}
    </section>
  );
}
