import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { BulkRenamePreviewItem } from "../../hooks/use-bulk-edit";
import { LibraryBulkToolsDialog } from "./library-bulk-tools-dialog";

// The tag picker reads the tag list from the server; the dialog only passes its value and change handler through.
vi.mock("./tag-picker", () => ({
  TagPicker: ({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) => (
    <input aria-label={label} value={value} onChange={(event) => onChange(event.target.value)} />
  )
}));

type Props = React.ComponentProps<typeof LibraryBulkToolsDialog>;

function props(overrides: Partial<Props> = {}): Props {
  return {
    open: true,
    selectedCount: 3,
    variant: "movies",
    isUpdating: false,
    operation: "monitoring",
    monitored: true,
    qualityProfileId: "",
    minimumAvailability: "released",
    targetLibraryId: "",
    moveFiles: false,
    tags: "",
    tagsMode: "add",
    renameTemplate: "",
    renamePreview: [],
    isApplyingRename: false,
    confirming: false,
    error: null,
    libraries: [{ id: "lib-1", name: "Films" }, { id: "lib-2", name: "Kids films" }],
    qualityProfiles: [{ id: "q-1", name: "HD 1080p" }, { id: "q-2", name: "Ultra HD" }],
    isOptionsLoading: false,
    undoCount: 2,
    redoCount: 1,
    onClose: vi.fn(),
    onOperationChange: vi.fn(),
    onMonitoredChange: vi.fn(),
    onQualityProfileChange: vi.fn(),
    onMinimumAvailabilityChange: vi.fn(),
    onTargetLibraryChange: vi.fn(),
    onMoveFilesChange: vi.fn(),
    onTagsChange: vi.fn(),
    onTagsModeChange: vi.fn(),
    onRenameTemplateChange: vi.fn(),
    onExecute: vi.fn(),
    onApplyRename: vi.fn(),
    ...overrides
  };
}

const optionNames = (select: HTMLElement) => within(select).getAllByRole("option").map((o) => o.textContent);

const PREVIEW: BulkRenamePreviewItem[] = [
  { itemId: "a", title: "Alien", year: 1979, template: "{Movie Title} ({Release Year})", proposedName: "Alien (1979)" },
  { itemId: "b", title: "Heat", year: 1995, template: "{Movie Title} ({Release Year})", proposedName: "Heat (1995)" }
];

describe("LibraryBulkToolsDialog", () => {
  it("renders nothing when closed", () => {
    const { container } = render(<LibraryBulkToolsDialog {...props({ open: false })} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("names the selection in the singular and the plural", () => {
    const { rerender } = render(<LibraryBulkToolsDialog {...props({ selectedCount: 1 })} />);
    expect(screen.getByRole("heading", { name: "1 selected title" })).toBeInTheDocument();
    rerender(<LibraryBulkToolsDialog {...props({ selectedCount: 12 })} />);
    expect(screen.getByRole("heading", { name: "12 selected titles" })).toBeInTheDocument();
  });

  it("offers minimum availability for movies only", () => {
    const { rerender } = render(<LibraryBulkToolsDialog {...props({ variant: "movies" })} />);
    expect(optionNames(screen.getByLabelText("Operation"))).toContain("Set minimum availability");
    rerender(<LibraryBulkToolsDialog {...props({ variant: "shows" })} />);
    expect(optionNames(screen.getByLabelText("Operation"))).toEqual([
      "Monitor or unmonitor", "Set quality profile", "Assign library/root", "Apply tags", "Search now", "Rename preview"
    ]);
  });

  it("reports the operation, the loading text and the undo counts", async () => {
    const p = props();
    render(<LibraryBulkToolsDialog {...p} />);
    await userEvent.selectOptions(screen.getByLabelText("Operation"), "quality");
    expect(p.onOperationChange).toHaveBeenCalledWith("quality");
    expect(screen.getByText("Undo stack: 2 · Redo stack: 1")).toBeInTheDocument();
  });

  it("shows loading instead of the stacks and cannot run while options load", () => {
    render(<LibraryBulkToolsDialog {...props({ isOptionsLoading: true })} />);
    expect(screen.getByText("Loading options...")).toBeInTheDocument();
    expect(screen.queryByText(/Undo stack/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Review and continue" })).toBeDisabled();
  });

  it("monitoring: sends true or false from the state picker", async () => {
    const p = props({ operation: "monitoring", monitored: true });
    render(<LibraryBulkToolsDialog {...p} />);
    const select = screen.getByLabelText("Monitoring state");
    expect(select).toHaveValue("true");
    await userEvent.selectOptions(select, "false");
    expect(p.onMonitoredChange).toHaveBeenCalledWith(false);
  });

  it("monitoring: shows unmonitored when the state is false", () => {
    render(<LibraryBulkToolsDialog {...props({ monitored: false })} />);
    expect(screen.getByLabelText("Monitoring state")).toHaveValue("false");
  });

  it("quality: lists the profiles and sends the chosen id", async () => {
    const p = props({ operation: "quality" });
    render(<LibraryBulkToolsDialog {...p} />);
    const select = screen.getByLabelText("Quality profile");
    expect(optionNames(select)).toEqual(["Choose a quality profile", "HD 1080p", "Ultra HD"]);
    await userEvent.selectOptions(select, "q-2");
    expect(p.onQualityProfileChange).toHaveBeenCalledWith("q-2");
  });

  it("minimum availability: lists the three points and sends the chosen one", async () => {
    const p = props({ operation: "minimumAvailability" });
    render(<LibraryBulkToolsDialog {...p} />);
    const select = screen.getByLabelText("Minimum availability");
    expect(optionNames(select)).toEqual(["Announced", "In cinemas", "Released"]);
    expect(select).toHaveValue("released");
    await userEvent.selectOptions(select, "inCinemas");
    expect(p.onMinimumAvailabilityChange).toHaveBeenCalledWith("inCinemas");
  });

  it("reassign library: lists libraries and sends the destination", async () => {
    const p = props({ operation: "reassignLibrary" });
    render(<LibraryBulkToolsDialog {...p} />);
    const select = screen.getByLabelText("Destination library");
    expect(optionNames(select)).toEqual(["Choose library", "Films", "Kids films"]);
    await userEvent.selectOptions(select, "lib-2");
    expect(p.onTargetLibraryChange).toHaveBeenCalledWith("lib-2");
  });

  it("reassign library with moveFiles off: says only the record moves, for movies and for series", () => {
    const { rerender } = render(<LibraryBulkToolsDialog {...props({ operation: "reassignLibrary", moveFiles: false })} />);
    expect(screen.getByLabelText("Move the files to the new root")).not.toBeChecked();
    expect(screen.getByText("Only the record moves; files stay under the old root.")).toBeInTheDocument();
    rerender(<LibraryBulkToolsDialog {...props({ operation: "reassignLibrary", moveFiles: false, variant: "shows" })} />);
    expect(screen.getByText("Only the record moves; files stay under the old root.")).toBeInTheDocument();
  });

  it("reassign library with moveFiles on: movies say files move, series say the whole show folder moves", () => {
    const { rerender } = render(<LibraryBulkToolsDialog {...props({ operation: "reassignLibrary", moveFiles: true, variant: "movies" })} />);
    expect(screen.getByLabelText("Move the files to the new root")).toBeChecked();
    expect(screen.getByText("Files physically move to the new library's root.")).toBeInTheDocument();
    rerender(<LibraryBulkToolsDialog {...props({ operation: "reassignLibrary", moveFiles: true, variant: "shows" })} />);
    expect(screen.getByText("Each show's whole folder moves to the new library's root.")).toBeInTheDocument();
    expect(screen.queryByText("Files physically move to the new library's root.")).not.toBeInTheDocument();
  });

  it("reassign library: ticking the box sends true, unticking sends false", async () => {
    const off = props({ operation: "reassignLibrary", moveFiles: false });
    const { unmount } = render(<LibraryBulkToolsDialog {...off} />);
    await userEvent.click(screen.getByLabelText("Move the files to the new root"));
    expect(off.onMoveFilesChange).toHaveBeenCalledWith(true);
    unmount();
    const on = props({ operation: "reassignLibrary", moveFiles: true });
    render(<LibraryBulkToolsDialog {...on} />);
    await userEvent.click(screen.getByLabelText("Move the files to the new root"));
    expect(on.onMoveFilesChange).toHaveBeenCalledWith(false);
  });

  it("tags: passes the tags and the mode through", async () => {
    const p = props({ operation: "tags", tags: "4k" });
    render(<LibraryBulkToolsDialog {...p} />);
    expect(screen.getByLabelText("Tags to apply")).toHaveValue("4k");
    await userEvent.type(screen.getByLabelText("Tags to apply"), "x");
    expect(p.onTagsChange).toHaveBeenCalledWith("4kx");
    await userEvent.selectOptions(screen.getByLabelText("How to apply the tags"), "replace");
    expect(p.onTagsModeChange).toHaveBeenCalledWith("replace");
  });

  it("search now: shows no extra field", () => {
    render(<LibraryBulkToolsDialog {...props({ operation: "search" })} />);
    expect(screen.getAllByRole("combobox")).toHaveLength(1);
  });

  it("rename preview: the template placeholder follows the variant and edits are sent", async () => {
    const p = props({ operation: "renamePreview", variant: "movies" });
    const { rerender } = render(<LibraryBulkToolsDialog {...p} />);
    const input = screen.getByLabelText("Template (optional)");
    expect(input).toHaveAttribute("placeholder", "{Movie Title} ({Release Year})");
    await userEvent.type(input, "a");
    expect(p.onRenameTemplateChange).toHaveBeenCalledWith("a");
    rerender(<LibraryBulkToolsDialog {...props({ operation: "renamePreview", variant: "shows" })} />);
    expect(screen.getByLabelText("Template (optional)")).toHaveAttribute("placeholder", "{Series Title} ({Series Year})");
  });

  it("rename preview: shows the proposed names, the safety note and applies them", async () => {
    const p = props({ operation: "renamePreview", renamePreview: PREVIEW });
    render(<LibraryBulkToolsDialog {...p} />);
    expect(screen.getByRole("columnheader", { name: "Proposed name" })).toBeInTheDocument();
    expect(screen.getByText("Alien (1979)")).toBeInTheDocument();
    expect(screen.getByText("Heat (1995)")).toBeInTheDocument();
    expect(screen.getByText("This only shows what would happen. Nothing on disk changes until you apply it.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run preview" })).toBeEnabled();
    await userEvent.click(screen.getByRole("button", { name: "Apply to 2 titles" }));
    expect(p.onApplyRename).toHaveBeenCalledTimes(1);
  });

  it("rename preview: a single result says title, and applying shows progress and cannot be repeated", () => {
    const { rerender } = render(<LibraryBulkToolsDialog {...props({ operation: "renamePreview", renamePreview: [PREVIEW[0]] })} />);
    expect(screen.getByRole("button", { name: "Apply to 1 title" })).toBeInTheDocument();
    rerender(<LibraryBulkToolsDialog {...props({ operation: "renamePreview", renamePreview: PREVIEW, isApplyingRename: true })} />);
    expect(screen.getByRole("button", { name: "Applying..." })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Run preview" })).toBeDisabled();
  });

  it("rename preview with no results shows no table and no apply button", () => {
    render(<LibraryBulkToolsDialog {...props({ operation: "renamePreview", renamePreview: [] })} />);
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Apply to/ })).not.toBeInTheDocument();
  });

  it("the preview table is not shown for another operation even when results are left over", () => {
    render(<LibraryBulkToolsDialog {...props({ operation: "quality", renamePreview: PREVIEW })} />);
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });

  it("the main button asks to review first, then to confirm, and runs the operation", async () => {
    const p = props({ confirming: false });
    const { rerender } = render(<LibraryBulkToolsDialog {...p} />);
    expect(screen.queryByText(/Confirming will run/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Review and continue" }));
    expect(p.onExecute).toHaveBeenCalledTimes(1);
    rerender(<LibraryBulkToolsDialog {...props({ confirming: true, selectedCount: 1, onExecute: p.onExecute })} />);
    expect(screen.getByText("Confirming will run this operation across 1 selected title.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Confirm and run" }));
    expect(p.onExecute).toHaveBeenCalledTimes(2);
  });

  it("no confirmation warning for rename preview, which never changes anything", () => {
    render(<LibraryBulkToolsDialog {...props({ operation: "renamePreview", confirming: true })} />);
    expect(screen.queryByText(/Confirming will run/)).not.toBeInTheDocument();
  });

  it("while updating: shows Running, and Close, Cancel and the run button are disabled", () => {
    render(<LibraryBulkToolsDialog {...props({ isUpdating: true })} />);
    expect(screen.getByRole("button", { name: "Running..." })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Close" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
  });

  it("shows the error text and closes from Close and Cancel", async () => {
    const p = props({ error: "Pick a library first." });
    render(<LibraryBulkToolsDialog {...p} />);
    expect(screen.getByText("Pick a library first.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(p.onClose).toHaveBeenCalledTimes(2);
  });

  it("shows no error box when there is no error", () => {
    render(<LibraryBulkToolsDialog {...props({ error: null })} />);
    expect(screen.queryByText("Pick a library first.")).not.toBeInTheDocument();
  });
});
