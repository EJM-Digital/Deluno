import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * What retention says about one title, on its page (coverage register, areas movie-detail and show-detail): Leaving soon with Keep and
 * Postpone, a keep with Undo, and an episode a rule removed with Want it again. Drawn from the server's own status; each button is the
 * request it names and the toast says what happened.
 */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  authedFetch: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn() })
}));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast }));

const { RetentionTitleCard } = await import("./retention-title-card");

const mark = {
  ruleId: "r1", ruleName: "Unwatched for a year", entityId: "m1", level: "movie", title: "Big Buck Bunny", reasons: ["Not watched since 2025", "Larger than 10 GB"],
  removeAfterUtc: "2026-11-01T00:00:00Z", state: "leaving", stateReason: null
};

let status: unknown;
let calls: Array<{ url: string; method: string; body: any }>;

beforeEach(() => {
  status = { marks: [mark], keeps: [], episodeRemovals: [] };
  calls = [];
  vi.clearAllMocks();
  mocks.fetchJson.mockReset().mockImplementation(async () => status);
  mocks.authedFetch.mockReset().mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : undefined });
    return new Response("{}", { status: 200 });
  });
});

describe("the Retention card on a title's page", () => {
  it("draws nothing when retention has nothing to say about the title", async () => {
    status = { marks: [], keeps: [], episodeRemovals: [] };
    const { container } = render(<RetentionTitleCard entityId="m1" />);

    await waitFor(() => expect(mocks.fetchJson).toHaveBeenCalledWith("/api/retention/titles/m1"));
    expect(container).toBeEmptyDOMElement();
  });

  it("names the rule, the first reason and how many more, and offers Keep and Postpone", async () => {
    render(<RetentionTitleCard entityId="m1" />);

    expect(await screen.findByText("“Unwatched for a year”")).toBeInTheDocument();
    expect(screen.getByText("Not watched since 2025")).toBeInTheDocument();
    expect(screen.getByText("and 1 more")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Keep Big Buck Bunny from Unwatched for a year" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Postpone Big Buck Bunny on Unwatched for a year" })).toBeInTheDocument();
  });

  it("Keep asks, keeps it from that rule with the typed note, toasts and tells the page", async () => {
    const onChanged = vi.fn();
    render(<RetentionTitleCard entityId="m1" onChanged={onChanged} />);
    fireEvent.click(await screen.findByRole("button", { name: "Keep Big Buck Bunny from Unwatched for a year" }));
    const dialog = await screen.findByRole("dialog", { name: "Keep Big Buck Bunny?" });
    fireEvent.change(within(dialog).getByPlaceholderText("Kids' favourite"), { target: { value: "Family favourite" } });

    fireEvent.click(within(dialog).getByRole("button", { name: "Keep" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Big Buck Bunny is kept."));
    expect(calls.find((c) => c.url === "/api/retention/keeps")!.body).toEqual({ entityId: "m1", ruleId: "r1", note: "Family favourite" });
    expect(onChanged).toHaveBeenCalled();
  });

  it("Keep from every rule sends no rule", async () => {
    render(<RetentionTitleCard entityId="m1" />);
    fireEvent.click(await screen.findByRole("button", { name: "Keep Big Buck Bunny from Unwatched for a year" }));
    const dialog = await screen.findByRole("dialog", { name: "Keep Big Buck Bunny?" });
    fireEvent.click(within(dialog).getByRole("radio", { name: "Every rule" }));

    fireEvent.click(within(dialog).getByRole("button", { name: "Keep" }));

    await waitFor(() => expect(calls.some((c) => c.url === "/api/retention/keeps")).toBe(true));
    expect(calls.find((c) => c.url === "/api/retention/keeps")!.body).toMatchObject({ entityId: "m1", ruleId: null });
  });

  it("a keep the server refuses says the title could not be kept", async () => {
    mocks.authedFetch.mockResolvedValue(new Response("{}", { status: 400 }));
    render(<RetentionTitleCard entityId="m1" />);
    fireEvent.click(await screen.findByRole("button", { name: "Keep Big Buck Bunny from Unwatched for a year" }));

    fireEvent.click(within(await screen.findByRole("dialog", { name: "Keep Big Buck Bunny?" })).getByRole("button", { name: "Keep" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Big Buck Bunny could not be kept."));
  });

  it("Postpone moves its day back by the days typed and says it is not kept", async () => {
    render(<RetentionTitleCard entityId="m1" />);
    fireEvent.click(await screen.findByRole("button", { name: "Postpone Big Buck Bunny on Unwatched for a year" }));
    const dialog = await screen.findByRole("dialog", { name: "Postpone Big Buck Bunny?" });
    fireEvent.change(within(dialog).getByRole("spinbutton"), { target: { value: "14" } });

    fireEvent.click(within(dialog).getByRole("button", { name: "Postpone" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Big Buck Bunny is postponed by 14 days. It is not kept."));
    expect(calls.find((c) => c.url === "/api/retention/leaving-soon/postpone")!.body).toEqual({ ruleId: "r1", entityIds: ["m1"], days: 14 });
  });

  it("a kept title shows the note and Undo deletes the keep", async () => {
    status = { marks: [], keeps: [{ id: "k1", entityId: "m1", title: "Big Buck Bunny", ruleId: null, ruleName: null, note: "Family favourite", createdUtc: "2026-10-01T00:00:00Z" }], episodeRemovals: [] };
    render(<RetentionTitleCard entityId="m1" />);
    expect(await screen.findByText("Every rule")).toBeInTheDocument();
    expect(screen.getByText("Family favourite")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Undo" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Big Buck Bunny is no longer kept."));
    expect(calls.some((c) => c.url === "/api/retention/keeps/k1" && c.method === "DELETE")).toBe(true);
  });

  it("an episode a rule removed is listed with its code and Want it again puts it back", async () => {
    status = { marks: [], keeps: [], episodeRemovals: [{ episodeId: "e1", seasonNumber: 1, episodeNumber: 5, ruleName: "Watched and old", reason: "Watched 400 days ago", removedUtc: "2026-10-01T00:00:00Z" }] };
    render(<RetentionTitleCard entityId="s1" />);
    expect(await screen.findByText("S01E05")).toBeInTheDocument();
    expect(screen.getByText("Watched 400 days ago")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Want it again" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("S01E05 is wanted again."));
    expect(calls.some((c) => c.url === "/api/retention/episode-removals/e1" && c.method === "DELETE")).toBe(true);
  });

  it("an undo the server refuses says it could not be undone", async () => {
    status = { marks: [], keeps: [{ id: "k1", entityId: "m1", title: "Big Buck Bunny", ruleId: "r1", ruleName: "Unwatched for a year", note: null, createdUtc: "2026-10-01T00:00:00Z" }], episodeRemovals: [] };
    mocks.authedFetch.mockResolvedValue(new Response("{}", { status: 500 }));
    render(<RetentionTitleCard entityId="m1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Undo" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That could not be undone."));
  });
});
