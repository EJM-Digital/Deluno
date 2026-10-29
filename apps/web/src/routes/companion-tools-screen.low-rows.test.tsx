import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CompanionTool, CompanionToolsResponse } from "./companion-tools-merge";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", async (importOriginal) => ({ ...(await importOriginal<object>()), authedFetch: vi.fn() }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({ ...(await importOriginal<object>()), useSignalREvent: vi.fn() }));

const { CompanionToolsPage, companionToolsLoader } = await import("./companion-tools-screen");

const tool = (over: Partial<CompanionTool> & { id: string; name: string }): CompanionTool => ({
  description: `${over.name} description`, state: "not-installed", connectionName: null, url: null, job: null, ...over
});

function response(tools: CompanionTool[]): CompanionToolsResponse {
  return { availability: { available: true, reason: null }, tools, uninstallNote: "To remove a tool, uninstall it in Windows." };
}

async function renderPage(data: CompanionToolsResponse, bundled: () => Promise<unknown> = async () => []) {
  fetchJson.mockImplementation(async (url: string) => (url === "/api/companion-tools" ? data : bundled()));
  const router = createMemoryRouter([{ path: "/", element: <CompanionToolsPage />, loader: companionToolsLoader }], { initialEntries: ["/"] });
  return render(<RouterProvider router={router} />);
}

const rowOrder = () => screen.getAllByRole("row").filter((r) => r.querySelector(":scope > [role=cell]")).map((r) => within(r).getAllByRole("cell")[0].textContent ?? "");

afterEach(() => { cleanup(); fetchJson.mockReset(); });

describe("the Companion Tools list, low rows (#1329)", () => {
  it("sorts by Tool, On this computer, Last step and Status, each column ascending then reversed", async () => {
    const user = userEvent.setup();
    await renderPage(response([
      tool({ id: "b", name: "Beta", state: "connected", connectionName: "Beta", job: { status: "done", step: "x", message: "Zebra step", updatedUtc: "2026-10-19T00:00:00Z" } }),
      tool({ id: "a", name: "Alpha", state: "not-installed", job: { status: "done", step: "x", message: "Mid step", updatedUtc: "2026-10-19T00:00:00Z" } }),
      tool({ id: "c", name: "Gamma", state: "installed", job: { status: "done", step: "x", message: "Apple step", updatedUtc: "2026-10-19T00:00:00Z" } })
    ]));
    await screen.findByText("Beta description");
    const header = (name: string) => screen.getByRole("columnheader", { name: new RegExp(`^${name}`) });
    const press = (name: string) => user.click(within(header(name)).getByRole("button"));
    const names = () => rowOrder().map((t) => (/Alpha|Beta|Gamma/.exec(t) ?? [""])[0]);

    await press("Tool");
    expect(names()).toEqual(["Alpha", "Beta", "Gamma"]);
    await press("Tool");
    expect(names()).toEqual(["Gamma", "Beta", "Alpha"]);

    await press("On this computer");
    expect(names()).toEqual(["Alpha", "Gamma", "Beta"]);
    await press("On this computer");
    expect(names()).toEqual(["Beta", "Gamma", "Alpha"]);

    await press("Status");
    expect(names()).toEqual(["Gamma", "Alpha", "Beta"]);
    await press("Status");
    expect(names()).toEqual(["Beta", "Alpha", "Gamma"]);

    await press("Last step");
    expect(names()).toEqual(["Gamma", "Alpha", "Beta"]);
    await press("Last step");
    expect(names()).toEqual(["Beta", "Alpha", "Gamma"]);
  });

  it("an empty list says there are no companion tools and that this build offers none", async () => {
    await renderPage(response([]));
    expect(await screen.findByText("No companion tools")).toBeTruthy();
    expect(screen.getByText("This build offers none.")).toBeTruthy();
  });

  it("Last step shows the job's message with its UTC reset time in the viewer's local time, or a dash when there is no job", async () => {
    await renderPage(response([
      tool({ id: "a", name: "Alpha", job: { status: "failed", step: "x", message: "GitHub limit. Try again after 2026-10-09 00:35 UTC.", updatedUtc: "2026-10-19T00:00:00Z" } }),
      tool({ id: "b", name: "Beta" })
    ]));
    await screen.findByText("Beta description");
    const alpha = screen.getAllByRole("row").find((r) => r.textContent?.includes("Alpha description"))!;
    const local = new Date(Date.UTC(2026, 9, 9, 0, 35)).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    expect(alpha.textContent).toContain(local);
    expect(alpha.textContent).not.toContain("00:35 UTC");
    const beta = screen.getAllByRole("row").find((r) => r.textContent?.includes("Beta description"))!;
    expect(within(beta).getAllByRole("cell")[2].textContent).toBe("—");
  });

  it("On this computer reads Connected as the name, Installed or Not installed", async () => {
    await renderPage(response([
      tool({ id: "a", name: "Alpha", state: "connected", connectionName: "My Alpha" }),
      tool({ id: "b", name: "Beta", state: "installed" }),
      tool({ id: "c", name: "Gamma" })
    ]));
    await screen.findByText("Beta description");
    const cell = (label: string) => within(screen.getAllByRole("row").find((r) => r.textContent?.includes(`${label} description`))!).getAllByRole("cell")[1].textContent;
    expect(cell("Alpha")).toContain("Connected as “My Alpha”");
    expect(cell("Beta")).toBe("Installed");
    expect(cell("Gamma")).toBe("Not installed");
  });

  it("the list still loads when the installed-apps read fails, with no extra apps", async () => {
    await renderPage(response([tool({ id: "a", name: "Alpha" })]), async () => { throw new Error("down"); });
    expect(await screen.findByText("Alpha description")).toBeTruthy();
    expect(screen.getAllByRole("row").filter((r) => r.querySelector(":scope > [role=cell]"))).toHaveLength(1);
  });

  it("the uninstall note is shown under the list", async () => {
    await renderPage(response([tool({ id: "a", name: "Alpha" })]));
    expect(await screen.findByText("To remove a tool, uninstall it in Windows.")).toBeTruthy();
  });
});
