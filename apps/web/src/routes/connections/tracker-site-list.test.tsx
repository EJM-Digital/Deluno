import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { TrackerDefinitionSummary } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { TrackerPicker } from "./tracker-picker";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

beforeEach(() => vi.mocked(authedFetch).mockReset());

const site: TrackerDefinitionSummary = {
  id: "uindex",
  name: "Uindex",
  description: "",
  language: "en-US",
  privacy: "public",
  links: ["https://uindex.org/"],
  movies: true,
  tv: true,
  loginMethod: null,
  settings: [],
  mayNeedCloudflareHelp: false,
  unsupported: null,
  origin: "bundled"
};

describe("checking for newer site files (#986)", () => {
  it("lists the built-in sites straight away, with no download step, and only checks when the button is pressed", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(JSON.stringify({ status: "updated", error: null, updatedTrackerIds: ["a", "b"] }), { status: 200 }));
    const onRefresh = vi.fn();
    render(<TrackerPicker trackers={[site]} onPick={() => undefined} onRefresh={onRefresh} />);

    expect(screen.getByRole("option", { name: /Uindex/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Get the current tracker site list/ })).not.toBeInTheDocument();
    expect(screen.getByText(/Where they come from and under what terms/)).toBeInTheDocument();
    expect(authedFetch).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Check for newer site files" }));

    await waitFor(() => expect(onRefresh).toHaveBeenCalled());
    expect(vi.mocked(authedFetch).mock.calls[0][0]).toBe("/api/trackers/definitions/update-check/run");
    expect(screen.getByRole("status")).toHaveTextContent("Got 2 newer site files.");
  });

  it("says plainly when the repository could not be reached and changes nothing", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(JSON.stringify({ status: "failed", error: "Deluno could not reach the tracker definitions source. It will try again on its own schedule.", updatedTrackerIds: [] }), { status: 200 }));
    const onRefresh = vi.fn();
    render(<TrackerPicker trackers={[site]} onPick={() => undefined} onRefresh={onRefresh} />);

    fireEvent.click(screen.getByRole("button", { name: "Check for newer site files" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("could not reach"));
    expect(onRefresh).not.toHaveBeenCalled();
  });
});
