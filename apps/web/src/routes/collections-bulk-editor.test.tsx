import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { LibraryItem, QualityProfileItem } from "../lib/api";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../components/app/tag-picker", () => ({ TagPicker: () => null }));

const { CollectionBulkEditor } = await import("./collections-page");

const profiles = [{ id: "p1", name: "HD-1080p", mediaType: "movies" }] as QualityProfileItem[];
const libraries = [{ id: "l1", name: "Movies", mediaType: "movies", rootPath: "D:\\Movies" }] as LibraryItem[];

describe("collection bulk editor (#998)", () => {
  it("cannot apply until something is chosen, then sends only what was chosen", () => {
    const onApply = vi.fn();
    render(<CollectionBulkEditor count={3} qualityProfiles={profiles} libraries={libraries} disabled={false} onApply={onApply} />);

    const apply = screen.getByRole("button", { name: "Apply to 3 collections" });
    expect(apply).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Quality profile"), { target: { value: "p1" } });
    fireEvent.change(screen.getByLabelText("Look for movies"), { target: { value: "inCinemas" } });
    fireEvent.change(screen.getByLabelText("Movie folder"), { target: { value: "l1" } });
    fireEvent.change(screen.getByLabelText("Search when movies are added"), { target: { value: "yes" } });
    fireEvent.click(apply);

    expect(onApply).toHaveBeenCalledWith({ qualityProfileId: "p1", minimumAvailability: "inCinemas", libraryId: "l1", searchOnAdd: true });
  });

  it("can turn add-missing off", () => {
    const onApply = vi.fn();
    render(<CollectionBulkEditor count={1} qualityProfiles={profiles} libraries={libraries} disabled={false} onApply={onApply} />);
    fireEvent.change(screen.getByLabelText("Add missing movies"), { target: { value: "no" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply to 1 collection" }));
    expect(onApply).toHaveBeenCalledWith({ monitorMovies: false });
  });
});

describe("removing collections (#1056)", () => {
  it("says the movies stay as they are and only acts on the confirmation", async () => {
    const { RemoveCollectionsConfirm } = await import("./collections-page");
    const onConfirm = vi.fn();
    const onCancel = vi.fn();
    const two = [{ id: "a", name: "Nolan Collection" }, { id: "b", name: "Bond Collection" }] as never;
    render(<RemoveCollectionsConfirm collections={two} busy={false} onCancel={onCancel} onConfirm={onConfirm} />);

    expect(screen.getByText("Stop following 2 collections?")).toBeInTheDocument();
    expect(screen.getByText(/Your movies stay exactly as they are: none is deleted, unmonitored or changed/)).toBeInTheDocument();
    expect(onConfirm).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Stop following" }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });
});
