import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/** The Retention card on a title's page in its less common states, and when a request is refused or never answered. */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  authedFetch: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn() })
}));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast }));

const { RetentionTitleCard } = await import("./retention-title-card");

const mark = (over: Record<string, unknown> = {}) => ({
  ruleId: "r1", ruleName: "Old films", entityId: "m1", level: "movie", title: "Dune", reasons: ["Not watched"],
  removeAfterUtc: "2099-11-01T00:00:00Z", state: "leaving", stateReason: null, ...over
});
let status: unknown;

beforeEach(() => {
  vi.clearAllMocks();
  status = { marks: [mark()], keeps: [], episodeRemovals: [] };
  mocks.fetchJson.mockReset().mockImplementation(async () => status);
  mocks.authedFetch.mockReset().mockResolvedValue(new Response("{}", { status: 200 }));
});

describe("the Retention card, other states", () => {
  it("draws nothing when the status cannot be read", async () => {
    mocks.fetchJson.mockRejectedValue(new Error("offline"));
    const { container } = render(<RetentionTitleCard entityId="m1" />);
    await waitFor(() => expect(mocks.fetchJson).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it("shows a dash when a mark has no reason, names an episode by its own title, and marks a hand-added title as Manual", async () => {
    status = { marks: [mark({ reasons: [], level: "episode", title: "Pilot", manual: true })], keeps: [], episodeRemovals: [] };
    render(<RetentionTitleCard entityId="m1" />);
    expect(await screen.findByText("Pilot")).toBeTruthy();
    expect(screen.getByText("—")).toBeTruthy();
    expect(screen.getByText("Manual")).toBeTruthy();
    expect(screen.getByText("added by hand")).toBeTruthy();
  });

  it("gives a held title its reason and offers Postpone; a done one gives its reason and no Postpone", async () => {
    status = {
      marks: [mark({ state: "held", stateReason: "Waiting for the download to finish." }), mark({ ruleId: "r2", ruleName: "Other", state: "done", stateReason: null })],
      keeps: [], episodeRemovals: []
    };
    render(<RetentionTitleCard entityId="m1" />);
    expect(await screen.findByText("Waiting for the download to finish.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Postpone Dune on Old films" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Postpone Dune on Other" })).toBeNull();
  });

  it("shows keeps from every rule, from a rule since deleted, and without a note", async () => {
    status = {
      marks: [],
      keeps: [
        { id: "k1", entityId: "m1", title: "Dune", ruleId: null, ruleName: null, note: "Family night", createdUtc: "2026-10-01T00:00:00Z" },
        { id: "k2", entityId: "m1", title: "Dune", ruleId: "gone", ruleName: null, note: null, createdUtc: "2026-10-01T00:00:00Z" }
      ],
      episodeRemovals: []
    };
    render(<RetentionTitleCard entityId="m1" />);
    expect(await screen.findByText("Every rule")).toBeTruthy();
    expect(screen.getByText("“Deleted rule”")).toBeTruthy();
    expect(screen.getByText("Family night")).toBeTruthy();
    expect(screen.getByText("—")).toBeTruthy();
  });

  it("names a removed episode with no numbers as S00E00", async () => {
    status = { marks: [], keeps: [], episodeRemovals: [{ episodeId: "e1", seasonNumber: null, episodeNumber: null, ruleName: "Old shows", reason: "Watched", removedUtc: "2026-10-01T00:00:00Z" }] };
    render(<RetentionTitleCard entityId="s1" />);
    expect(await screen.findByText("S00E00")).toBeTruthy();
  });
});

describe("the Retention card, when a request goes wrong", () => {
  const open = async () => {
    render(<RetentionTitleCard entityId="m1" />);
    await screen.findByText("“Old films”");
  };

  it("says Keep could not be done when the server refuses, or cannot be reached", async () => {
    await open();
    mocks.authedFetch.mockResolvedValueOnce(new Response("{}", { status: 500 }));
    fireEvent.click(screen.getByRole("button", { name: "Keep Dune from Old films" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Keep" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Dune could not be kept."));

    mocks.authedFetch.mockRejectedValueOnce(new Error("offline"));
    fireEvent.click(screen.getByRole("button", { name: "Keep Dune from Old films" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Keep" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That could not be kept. Deluno could not be reached."));
  });

  it("says Postpone could not be done when the server refuses, or cannot be reached", async () => {
    await open();
    mocks.authedFetch.mockResolvedValueOnce(new Response("{}", { status: 500 }));
    fireEvent.click(screen.getByRole("button", { name: "Postpone Dune on Old films" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Postpone" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Dune could not be postponed."));

    mocks.authedFetch.mockRejectedValueOnce(new Error("offline"));
    fireEvent.click(screen.getByRole("button", { name: "Postpone Dune on Old films" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Postpone" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That could not be postponed. Deluno could not be reached."));
  });

  it("says Undo could not be done when the server refuses, or cannot be reached", async () => {
    status = { marks: [], keeps: [{ id: "k1", entityId: "m1", title: "Dune", ruleId: null, ruleName: null, note: null, createdUtc: "2026-10-01T00:00:00Z" }], episodeRemovals: [] };
    render(<RetentionTitleCard entityId="m1" />);
    const undo = await screen.findByRole("button", { name: "Undo" });
    mocks.authedFetch.mockResolvedValueOnce(new Response("{}", { status: 500 }));
    fireEvent.click(undo);
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That could not be undone."));

    mocks.authedFetch.mockRejectedValueOnce(new Error("offline"));
    fireEvent.click(screen.getByRole("button", { name: "Undo" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That could not be undone. Deluno could not be reached."));
  });

  it("closes the Keep and Postpone questions on Cancel without sending anything", async () => {
    await open();
    fireEvent.click(screen.getByRole("button", { name: "Keep Dune from Old films" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByRole("button", { name: "Postpone Dune on Old films" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(mocks.authedFetch).not.toHaveBeenCalled();
  });
});
