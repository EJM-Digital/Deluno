import { useEffect, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Copy, Eye, Loader2 } from "lucide-react";
import { fetchJson } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Checkbox } from "../components/ui/checkbox";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";

const TABS = configurationNavAreas.find((area) => area.to === "/indexers/indexers")?.items ?? [];

/** Mirrors `Deluno.Companions.CompanionToolStatus`. */
interface CompanionTool {
  id: string;
  name: string;
  description: string;
  state: "not-installed" | "installed" | "connected" | string;
  connectionName: string | null;
  hasLogin: boolean;
  job: { status: "queued" | "running" | "done" | "skipped" | "failed" | string; step: string; message: string; updatedUtc: string } | null;
}

interface CompanionToolsResponse {
  availability: { available: boolean; reason: string | null };
  tools: CompanionTool[];
  uninstallNote: string;
}

interface CompanionLogin {
  url: string;
  username: string | null;
  secret: string;
  secretLabel: string;
}

export async function companionToolsLoader() {
  return fetchJson<CompanionToolsResponse>("/api/companion-tools");
}

function stateChip(tool: CompanionTool): { tone: "ok" | "info" | "warn" | "bad" | "idle"; label: string } {
  if (tool.job?.status === "running") return { tone: "info", label: "Setting up" };
  if (tool.job?.status === "queued") return { tone: "info", label: "Waiting its turn" };
  if (tool.job?.status === "failed") return { tone: "bad", label: "Needs you" };
  if (tool.state === "connected") return { tone: "ok", label: "Connected" };
  if (tool.state === "installed") return { tone: "warn", label: "Installed, not connected" };
  return { tone: "idle", label: "Not installed" };
}

/**
 * Get the tools Deluno needs (#506).
 *
 * Tick what you want; Deluno downloads each tool's official build, checks it is
 * that build, installs it, and connects it - no host, port or password to type.
 * A tool already on this computer is connected rather than installed again, and
 * one somebody already configured is never overwritten.
 */
export function CompanionToolsPage() {
  const loaded = useLoaderData() as CompanionToolsResponse;
  const revalidator = useRevalidator();
  const [data, setData] = useState(loaded);
  const [selected, setSelected] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const [login, setLogin] = useState<CompanionLogin | null>(null);

  useEffect(() => setData(loaded), [loaded]);

  const working = data.tools.some((tool) => tool.job?.status === "running" || tool.job?.status === "queued");
  useEffect(() => {
    if (!working) return;
    const timer = window.setInterval(() => {
      void fetchJson<CompanionToolsResponse>("/api/companion-tools").then(setData).catch(() => undefined);
    }, 2000);
    return () => window.clearInterval(timer);
  }, [working]);

  const open = data.tools.find((tool) => tool.id === openId) ?? null;
  const selectable = (tool: CompanionTool) => data.availability.available && tool.state !== "connected" && !working;

  async function install() {
    setBusy(true);
    setMessage(null);
    try {
      const response = await authedFetch("/api/companion-tools/install", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ toolIds: selected })
      });
      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as { message?: string } | null;
        setMessage(body?.message ?? `Setting up could not start (HTTP ${response.status}).`);
        return;
      }
      setSelected([]);
      setData(await fetchJson<CompanionToolsResponse>("/api/companion-tools"));
    } finally {
      setBusy(false);
    }
  }

  async function showLogin(tool: CompanionTool) {
    setLogin(await fetchJson<CompanionLogin>(`/api/companion-tools/${tool.id}/login`).catch(() => null));
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={TABS}
        actions={
          <PageToolbarAction type="button" onClick={() => void install()} disabled={busy || working || selected.length === 0 || !data.availability.available}>
            {selected.length ? `Set up ${selected.length} ${selected.length === 1 ? "tool" : "tools"}` : "Set up tools"}
          </PageToolbarAction>
        }
      />

      <ListCard title="Companion tools" count={`${data.tools.filter((tool) => tool.state === "connected").length} of ${data.tools.length} connected`}>
        {!data.availability.available ? (
          <p className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-caption)] text-warning">{data.availability.reason}</p>
        ) : message ? (
          <p className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-caption)] text-destructive" role="alert">{message}</p>
        ) : null}
        {data.tools.length === 0 ? (
          <ListEmpty title="No companion tools" description="This build offers none." />
        ) : (
          <ListTable
            columns={[
              { label: "Tool" },
              { label: "On this computer" },
              { label: "Last step" },
              { label: "Status", width: LIST_TRACK.status, mobile: true },
              { label: "Set up", width: LIST_TRACK.toggle, mobile: true }
            ]}
          >
            {data.tools.map((tool) => {
              const chip = stateChip(tool);
              return (
                <ListRow key={tool.id} onClick={() => { setOpenId(tool.id); setLogin(null); }} selected={openId === tool.id}>
                  <ListNameCell name={tool.name} sub={tool.description} />
                  <ListCell
                    primary={tool.state === "connected" ? `Connected as “${tool.connectionName}”` : tool.state === "installed" ? "Installed" : "Not installed"}
                  />
                  <ListCell primary={tool.job?.message ?? <span className="text-muted-foreground">—</span>} />
                  <ListCell mobile><Chip tone={chip.tone}>{chip.label}</Chip></ListCell>
                  <ListCell mobile>
                    <span onClick={(event) => event.stopPropagation()}>
                      <Checkbox
                        aria-label={`Set up ${tool.name}`}
                        checked={selected.includes(tool.id)}
                        disabled={!selectable(tool)}
                        onCheckedChange={(checked) => setSelected((current) => (checked === true ? [...current, tool.id] : current.filter((id) => id !== tool.id)))}
                      />
                    </span>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
        <p className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-caption)] text-muted-foreground">{data.uninstallNote}</p>
      </ListCard>

      <Drawer
        open={open !== null}
        onOpenChange={(next) => {
          if (!next) {
            setOpenId(null);
            setLogin(null);
            revalidator.revalidate();
          }
        }}
        title={open?.name ?? "Tool"}
        description={open?.description}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => { setOpenId(null); setLogin(null); }} />}
      >
        {open ? (
          <>
            <DrawerSection title="Status" aside={stateChip(open).label}>
              <p className="text-[length:var(--type-body-sm)] text-foreground">
                {open.job?.message ?? (open.state === "connected" ? `Connected to Deluno as “${open.connectionName}”.` : open.state === "installed" ? "Installed on this computer but not connected to Deluno yet. Tick it and set it up to connect it." : "Not installed. Tick it and set it up.")}
              </p>
              {open.job?.status === "running" ? <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" aria-label="Working" /> : null}
            </DrawerSection>
            {open.hasLogin ? (
              <DrawerSection title="Login Deluno created">
                <p className="text-[length:var(--type-caption)] text-muted-foreground">Kept encrypted. Use it to open {open.name} yourself.</p>
                {login ? (
                  <dl className="grid grid-cols-[120px_1fr] gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
                    <dt className="text-muted-foreground">Address</dt>
                    <dd className="font-mono text-foreground">{login.url}</dd>
                    {login.username ? (<><dt className="text-muted-foreground">Username</dt><dd className="font-mono text-foreground">{login.username}</dd></>) : null}
                    <dt className="text-muted-foreground">{login.secretLabel}</dt>
                    <dd className="flex items-center gap-2 font-mono text-foreground">
                      <span className="min-w-0 truncate">{login.secret}</span>
                      <Button type="button" variant="ghost" size="icon" aria-label={`Copy ${login.secretLabel}`} onClick={() => void navigator.clipboard?.writeText(login.secret)}>
                        <Copy className="h-3.5 w-3.5" />
                      </Button>
                    </dd>
                  </dl>
                ) : (
                  <Button type="button" variant="outline" size="sm" className="w-max" onClick={() => void showLogin(open)}>
                    <Eye className="h-3.5 w-3.5" />
                    Show login
                  </Button>
                )}
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>
    </div>
  );
}
