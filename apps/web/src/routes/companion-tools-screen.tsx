import { toneSortValue } from "../lib/status-tones";
import { localizeResetTimes } from "../lib/reset-times";
import { useEffect, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { ExternalLink, Loader2 } from "lucide-react";
import { fetchJson, type BundledComponentStatus } from "../lib/api";
import { withInstalledApps, type CompanionTool, type CompanionToolsResponse } from "./companion-tools-merge";
import { authedFetch } from "../lib/use-auth";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Checkbox } from "../components/ui/checkbox";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";

const TABS = configurationNavAreas.find((area) => area.to === "/indexers/indexers")?.items ?? [];

async function loadCompanionTools(): Promise<CompanionToolsResponse> {
  const [tools, installed] = await Promise.all([
    fetchJson<CompanionToolsResponse>("/api/companion-tools"),
    fetchJson<BundledComponentStatus[]>("/api/system/bundled-components").catch(() => [] as BundledComponentStatus[])
  ]);
  return withInstalledApps(tools, installed);
}

export async function companionToolsLoader() {
  return loadCompanionTools();
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
  const [notice, setNotice] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);

  useEffect(() => setData(loaded), [loaded]);

  const working = data.tools.some((tool) => tool.job?.status === "running" || tool.job?.status === "queued");
  // The server says when a setup moves on (#1179), so a finished or failed job shows up at once,
  // in a backgrounded window too, and nothing asks on a timer.
  const reload = useCoalescedRevalidate(() => {
    void loadCompanionTools().then(setData).catch(() => undefined);
  }, 300);
  useSignalREvent("CompanionJobChanged", RealtimeGroups.Dashboard, reload);

  const open = data.tools.find((tool) => tool.id === openId) ?? null;
  // An app Deluno's installer already put on this computer cannot be set up again here; one it has not is asked of the tray.
  const selectable = (tool: CompanionTool) => data.availability.available && tool.state !== "connected" && !working && !tool.installedByInstaller;

  async function install() {
    setBusy(true);
    setMessage(null);
    setNotice(null);
    try {
      const trayIds = selected.filter((id) => data.tools.find((tool) => tool.id === id)?.installedByTray);
      const ownIds = selected.filter((id) => !trayIds.includes(id));
      for (const [url, toolIds] of [["/api/companion-tools/install", ownIds], ["/api/companion-tools/tray-install", trayIds]] as const) {
        if (toolIds.length === 0) continue;
        const response = await authedFetch(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ toolIds })
        });
        if (!response.ok) {
          const body = (await response.json().catch(() => null)) as { message?: string } | null;
          setMessage(body?.message ?? `Setting up could not start (HTTP ${response.status}).`);
          return;
        }
      }
      if (trayIds.length > 0) {
        const names = trayIds.map((id) => data.tools.find((tool) => tool.id === id)?.name ?? id).join(", ");
        setNotice(`Asked the Deluno icon in your taskbar to install ${names}. Its installer window opens in a few seconds; if it does not, choose "Try again" from the Deluno icon.`);
      }
      setSelected([]);
      setData(await loadCompanionTools());
    } catch {
      setMessage("Setting up could not start. Deluno could not be reached.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        tabs={TABS}
        actions={
          <PageToolbarAction type="button" onClick={() => void install()} disabled={busy || working || selected.length === 0 || !data.availability.available}>
            {selected.length ? `Set up ${selected.length} ${selected.length === 1 ? "tool" : "tools"}` : "Set up tools"}
          </PageToolbarAction>
        }
      />

      <ListCard title={`${data.tools.filter((tool) => tool.state === "connected").length} of ${data.tools.length} connected`} titleCase={false}>
        {!data.availability.available ? (
          <p className="px-(--card-pad-x) py-3 text-(length:--type-caption) text-warning">{data.availability.reason}</p>
        ) : message ? (
          <p className="px-(--card-pad-x) py-3 text-(length:--type-caption) text-destructive" role="alert">{localizeResetTimes(message)}</p>
        ) : notice ? (
          <p className="px-(--card-pad-x) py-3 text-(length:--type-caption) text-muted-foreground" role="status">{notice}</p>
        ) : null}
        {data.tools.length === 0 ? (
          <ListEmpty title="No companion tools" description="This build offers none." />
        ) : (
          <ListTable
            listId="companion-tools"
            items={data.tools}
            columns={[
              { id: "tool", label: "Tool", sort: (tool) => tool.name.toLowerCase() },
              { id: "onThisComputer", label: "On this computer", sort: (tool) => (tool.state === "connected" ? 2 : tool.state === "installed" ? 1 : 0) },
              { id: "lastStep", label: "Last step", sort: (tool) => (tool.job?.message ?? "").toLowerCase() },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (tool) => { const chip = stateChip(tool); return toneSortValue(chip.tone, chip.label); } },
              { id: "setUp", label: "Set up", width: LIST_TRACK.toggle, mobile: true, sortable: false }
            ]}
          >
            {(rows) => rows.map((tool) => {
              const chip = stateChip(tool);
              return (
                <ListRow key={tool.id} onClick={() => { setOpenId(tool.id); }} selected={openId === tool.id}>
                  <ListNameCell name={tool.name} sub={tool.description} />
                  <ListCell
                    primary={tool.state === "connected" ? `Connected as “${tool.connectionName ?? tool.name}”` : tool.state === "installed" ? "Installed" : "Not installed"}
                  />
                  <ListCell primary={tool.job?.message ? localizeResetTimes(tool.job.message) : <span className="text-muted-foreground">—</span>} />
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
        <p className="px-(--card-pad-x) py-3 text-(length:--type-caption) text-muted-foreground">{data.uninstallNote}</p>
      </ListCard>

      <Drawer
        open={open !== null}
        onOpenChange={(next) => {
          if (!next) {
            setOpenId(null);
            revalidator.revalidate();
          }
        }}
        title={open?.name ?? "Tool"}
        description={open?.description}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => { setOpenId(null); }} />}
      >
        {open ? (
          <>
            <DrawerSection title="Status" aside={stateChip(open).label}>
              <p className="text-(length:--type-body-sm) text-foreground">
                {(open.job?.message ? localizeResetTimes(open.job.message) : null) ?? (open.state === "connected" ? `Connected to Deluno as “${open.connectionName ?? open.name}”.` : open.state === "installed" ? open.installedByInstaller ? "Installed on this computer but not connected to Deluno yet. Connect it under Download Clients." : "Installed on this computer but not connected to Deluno yet. Tick it and set it up to connect it." : "Not installed. Tick it and set it up.")}
              </p>
              {open.job?.status === "running" ? <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" aria-label="Working" /> : null}
            </DrawerSection>
            {open.id === "weir" ? (
              <DrawerSection title="Your Weir account">
                <p className="text-(length:--type-caption) text-muted-foreground">
                  Weir asks you to create your account in Deluno's Connect step, or the first time you open it.
                </p>
                {open.url ? (
                  <Button asChild variant="outline" size="sm" className="w-max">
                    <a href={open.url} target="_blank" rel="noreferrer">
                      <ExternalLink className="h-3.5 w-3.5" />
                      Open Weir
                    </a>
                  </Button>
                ) : null}
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>
    </div>
  );
}
