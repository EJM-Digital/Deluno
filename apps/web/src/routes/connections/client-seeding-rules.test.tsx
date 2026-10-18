import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyClientForm } from "./forms";

const api = vi.hoisted(() => ({ fetchJson: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: api.fetchJson }));
vi.mock("../../components/app/tag-picker", () => ({ TagPicker: ({ label, value, onChange }: { label?: string; value: string; onChange: (v: string) => void }) => <input aria-label={label} value={value} onChange={(event) => onChange(event.target.value)} /> }));

const { ClientDrawerBody } = await import("./client-drawer-body");

const rule = (over: Record<string, unknown> = {}) => ({ id: "r1", priority: 1, categoryPattern: "tv-anime", trackerPattern: null, tagsCsv: null, minSeeders: 3, maxInactiveDays: 5, isEnabled: true, ...over });

function renderEditing() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ClientDrawerBody
        form={{ ...emptyClientForm(), protocol: "qbittorrent" }}
        setForm={() => {}}
        editing={{ id: "c1", name: "Seedbox" } as never}
        errors={{}}
        clearError={() => {}}
        mappings={[]}
        newMapping={{ remotePath: "", localPath: "" }}
        setNewMapping={() => {}}
        busy={null}
        onAddMapping={() => {}}
        onRemoveMapping={() => {}}
        onTestMapping={() => {}}
        onRemove={() => {}}
      />
    </QueryClientProvider>
  );
  fireEvent.click(screen.getByRole("button", { name: /^Seeding rules/ }));
}

/** #820: seeding rules are exceptions to the sharing rule, by category, tracker or tag, each with its own seeder and inactivity guard. */
describe("the Seeding rules editor in a download client's drawer", () => {
  beforeEach(() => { api.fetchJson.mockReset(); });

  it("lists the saved rules in words, in priority order, and says when a rule matches nothing yet", async () => {
    api.fetchJson.mockResolvedValue([rule({ id: "r2", priority: 2, categoryPattern: null, trackerPattern: "*.example.com", minSeeders: null, maxInactiveDays: 10 }), rule(), rule({ id: "r3", priority: 3, categoryPattern: null, minSeeders: null, maxInactiveDays: null })]);
    renderEditing();

    expect(await screen.findByText("category tv-anime — hold below 3 seeders · hold past 5 inactive days")).toBeInTheDocument();
    expect(screen.getByText("tracker *.example.com — hold past 10 inactive days")).toBeInTheDocument();
    expect(screen.getByText("Matches nothing yet")).toBeInTheDocument();
    const texts = screen.getAllByText(/^\d\.$/).map((item) => item.textContent);
    expect(texts).toEqual(["1.", "2.", "3."]);
  });

  it("adds a rule by category with its two guards at the next priority, lists it and clears the form", async () => {
    api.fetchJson.mockImplementation(async (path: string, init?: RequestInit) => (init?.method === "POST" ? rule({ id: "r2", priority: 2, categoryPattern: "movies-4k", minSeeders: 2, maxInactiveDays: 7 }) : [rule()]));
    renderEditing();
    await screen.findByText(/tv-anime/);

    fireEvent.change(screen.getByPlaceholderText("tv-anime"), { target: { value: "movies-4k" } });
    fireEvent.change(screen.getByPlaceholderText("e.g. 5"), { target: { value: "2" } });
    fireEvent.change(screen.getByPlaceholderText("e.g. 10"), { target: { value: "7" } });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));

    expect(await screen.findByText("category movies-4k — hold below 2 seeders · hold past 7 inactive days")).toBeInTheDocument();
    const post = api.fetchJson.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "POST")!;
    expect(post[0]).toBe("/api/sharing-rules");
    expect(JSON.parse(String((post[1] as RequestInit).body))).toMatchObject({ priority: 2, categoryPattern: "movies-4k", trackerPattern: null, tagsCsv: null, minSeeders: 2, maxInactiveDays: 7, isEnabled: true });
    expect((screen.getByPlaceholderText("tv-anime") as HTMLInputElement).value).toBe("");
  });

  it("adds a rule by tracker domain, and by tag", async () => {
    api.fetchJson.mockImplementation(async (path: string, init?: RequestInit) => (init?.method === "POST" ? rule({ id: "n", priority: 1 }) : []));
    renderEditing();
    await waitFor(() => expect(api.fetchJson.mock.calls.some(([path]) => path === "/api/sharing-rules")).toBe(true));

    fireEvent.change(screen.getByPlaceholderText("*.example.com"), { target: { value: "*.tracker.test" } });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));
    await waitFor(() => expect(api.fetchJson.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "POST")).toHaveLength(1));
    fireEvent.change(screen.getByLabelText("Seeding rule tags"), { target: { value: "kids" } });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));
    await waitFor(() => expect(api.fetchJson.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "POST")).toHaveLength(2));

    const bodies = api.fetchJson.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "POST").map(([, init]) => JSON.parse(String((init as RequestInit).body)));
    expect(bodies[0]).toMatchObject({ categoryPattern: null, trackerPattern: "*.tracker.test", tagsCsv: null });
    expect(bodies[1]).toMatchObject({ categoryPattern: null, trackerPattern: null, tagsCsv: "kids" });
  });

  it("refuses a rule that matches nothing, in words, and sends nothing", async () => {
    api.fetchJson.mockResolvedValue([]);
    renderEditing();
    const ruleCalls = () => api.fetchJson.mock.calls.filter(([path]) => path === "/api/sharing-rules");
    await waitFor(() => expect(ruleCalls()).toHaveLength(1));

    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));

    expect(await screen.findByText("Match this rule against a category, a tracker, or a tag.")).toBeInTheDocument();
    expect(ruleCalls()).toHaveLength(1);
  });

  it("removes a rule and says so when it could not", async () => {
    api.fetchJson.mockImplementation(async (path: string, init?: RequestInit) => {
      if (init?.method === "DELETE") throw new Error("no");
      return [rule()];
    });
    renderEditing();
    await screen.findByText(/tv-anime/);

    fireEvent.click(screen.getByRole("button", { name: "Remove seeding rule 1" }));

    expect(await screen.findByText("This rule could not be removed.")).toBeInTheDocument();
    expect(screen.getByText(/tv-anime/)).toBeInTheDocument();

    api.fetchJson.mockImplementation(async () => undefined);
    fireEvent.click(screen.getByRole("button", { name: "Remove seeding rule 1" }));
    await waitFor(() => expect(screen.queryByText(/tv-anime/)).not.toBeInTheDocument());
    expect(api.fetchJson).toHaveBeenLastCalledWith("/api/sharing-rules/r1", { method: "DELETE" });
  });
});
