import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { TrackerDefinitionSummary } from "../../lib/api";
import { emptyIndexerForm, type IndexerForm } from "./forms";

// The Tracker protocol's own fields (#606): refreshing the catalogue, the Cloudflare note, and each kind of setting a site asks.

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../../components/app/signed-api-link", () => ({ SignedApiLink: ({ children }: { children: React.ReactNode }) => <span>{children}</span> }));

const { TrackerFields } = await import("./tracker-fields");

const site = (over: Partial<TrackerDefinitionSummary>): TrackerDefinitionSummary => ({
  id: "demo", name: "Demo Site", description: "", language: "en-US", privacy: "public", links: ["https://demo.example/"], movies: true, tv: true,
  loginMethod: null, settings: [], mayNeedCloudflareHelp: false, unsupported: null, origin: "bundled", ...over
});

function Host({ initial, cloudflareExplained }: { initial?: Partial<IndexerForm>; cloudflareExplained?: boolean }) {
  const [form, setForm] = useState<IndexerForm>({ ...emptyIndexerForm(), protocol: "tracker", ...initial });
  return (
    <MemoryRouter>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <TrackerFields form={form} setForm={setForm} editing={null} errors={{}} clearError={() => undefined} cloudflareExplained={cloudflareExplained} />
        <output data-testid="form">{JSON.stringify(form)}</output>
      </QueryClientProvider>
    </MemoryRouter>
  );
}
const formNow = () => JSON.parse(screen.getByTestId("form").textContent!) as IndexerForm;

afterEach(() => { cleanup(); fetchJson.mockReset(); });

describe("the Tracker protocol's fields", () => {
  it("lists the sites again after a check for newer site files finds some", async () => {
    fetchJson.mockImplementation(async (path: string) => path === "/api/trackers" ? [site({ id: "a", name: "Alpha Site" })] : { status: "updated", error: null, updatedTrackerIds: ["a"] });
    render(<Host />);
    await screen.findByRole("option", { name: /Alpha Site/ });
    fireEvent.click(screen.getByRole("button", { name: "Check for newer site files" }));

    await waitFor(() => expect(fetchJson.mock.calls.filter(([path]) => path === "/api/trackers")).toHaveLength(2));
  });

  it("shows an empty picker when the catalogue answers nothing", async () => {
    fetchJson.mockResolvedValue(undefined);
    render(<Host />);
    expect(await screen.findByRole("listbox", { name: "Trackers" })).toBeTruthy();
  });

  it("picking a site with no address leaves the address empty", async () => {
    fetchJson.mockResolvedValue([site({ id: "bare", name: "Bare Site", links: [] })]);
    render(<Host />);
    fireEvent.click(await screen.findByRole("option", { name: /Bare Site/ }));
    expect(formNow().baseUrl).toBe("");
  });

  it("explains a Cloudflare check unless something beside it already does", async () => {
    fetchJson.mockResolvedValue([site({ mayNeedCloudflareHelp: true, description: "Behind a check." })]);
    render(<Host initial={{ definitionId: "demo" }} />);
    expect(await screen.findByText(/sometimes puts a Cloudflare check in front of searches/)).toBeTruthy();
    expect(screen.getByText("Behind a check.")).toBeTruthy();
    cleanup();

    render(<Host initial={{ definitionId: "demo" }} cloudflareExplained />);
    await screen.findByText("Demo Site");
    expect(screen.queryByText(/sometimes puts a Cloudflare check/)).toBeNull();
  });

  it("shows an info note only when it has words, a ticked switch, a select with options and a text box for a select with none", async () => {
    fetchJson.mockResolvedValue([site({
      settings: [
        { name: "note", type: "info", label: "Note", default: "<b>Mind</b> the ratio.", options: [] },
        { name: "silent", type: "info", label: "Silent", default: null, options: [] },
        { name: "free", type: "checkbox", label: "Free leech only", default: "true", options: [] },
        { name: "sort", type: "select", label: "Sort by", default: "a", options: [{ key: "a", value: "Alpha" }, { key: "b", value: "Beta" }] },
        { name: "bare", type: "select", label: "Bare select", default: null, options: [] },
        { name: "tags", type: "multi-select", label: "Hidden multi", default: null, options: [] }
      ] as never
    })]);
    render(<Host initial={{ definitionId: "demo", trackerSettings: { sort: "b" } }} />);

    expect(await screen.findByText("Mind the ratio.")).toBeTruthy();
    expect(screen.getByRole("switch", { name: "Free leech only" }).getAttribute("aria-checked")).toBe("true");
    expect((screen.getByLabelText("Sort by") as HTMLSelectElement).value).toBe("b");
    expect((screen.getByLabelText("Bare select") as HTMLInputElement).tagName).toBe("INPUT");
    expect(screen.queryByText("Hidden multi")).toBeNull();

    fireEvent.click(screen.getByRole("switch", { name: "Free leech only" }));
    expect(formNow().trackerSettings.free).toBe("false");
    fireEvent.click(screen.getByRole("switch", { name: "Free leech only" }));
    expect(formNow().trackerSettings.free).toBe("true");
    fireEvent.change(screen.getByLabelText("Sort by"), { target: { value: "a" } });
    expect(formNow().trackerSettings.sort).toBe("a");
    fireEvent.change(screen.getByLabelText("Bare select"), { target: { value: "typed" } });
    expect(formNow().trackerSettings.bare).toBe("typed");
  });
});
