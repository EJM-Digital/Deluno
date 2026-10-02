import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ListCell, ListRow, ListTable, type ListColumn } from "./list-card";
import { useListColumns } from "../../lib/use-list-columns";
import { dropOrder } from "../../lib/list-column-drag";

interface Item {
  id: string;
  name: string;
  size: number | null;
  when: string;
}

const items: Item[] = [
  { id: "b", name: "Bravo", size: 30, when: "2026-01-02" },
  { id: "a", name: "alpha", size: 5, when: "2026-03-01" },
  { id: "c", name: "Charlie", size: null, when: "2026-02-01" }
];

const columns: Array<ListColumn<Item>> = [
  { id: "name", label: "Name", sort: (item) => item.name.toLowerCase() },
  { id: "size", label: "Size", sort: (item) => item.size },
  { id: "when", label: "Added", sort: (item) => new Date(item.when) },
  { id: "actions", label: "", sortable: false, width: "40px" }
];

function Demo({ listId = "demo" }: { listId?: string }) {
  const list = useListColumns(listId, columns, items);
  return (
    <ListTable list={list} chevron={false}>
      {list.items.map((item) => (
        <ListRow key={item.id} data-testid={`row-${item.id}`}>
          <ListCell primary={item.name} />
          <ListCell primary={String(item.size)} mobile />
          <ListCell primary={item.when} mobile />
          <ListCell primary="x" mobile />
        </ListRow>
      ))}
    </ListTable>
  );
}

const rowOrder = () => screen.getAllByTestId(/^row-/).map((row) => row.dataset.testid);
const headerOrder = () => screen.getAllByRole("columnheader").map((header) => header.dataset.colId);

/** Headers are 100px wide, laid out left to right in DOM order. */
function mockLayout() {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.dataset.colId) {
      const index = Array.from(this.parentElement?.children ?? []).indexOf(this);
      return { left: index * 100, right: index * 100 + 100, width: 100, top: 0, bottom: 36, height: 36, x: index * 100, y: 0, toJSON: () => ({}) } as DOMRect;
    }
    return { left: 0, right: 400, width: 400, top: 0, bottom: 36, height: 36, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  });
}

function pointer(type: string, x: number, target: Element | Window = window) {
  act(() => {
    target.dispatchEvent(new MouseEvent(type, { clientX: x, clientY: 10, button: 0, bubbles: true, cancelable: true }));
  });
}

function drag(header: Element, from: number, to: number) {
  pointer("pointerdown", from, header);
  pointer("pointermove", from + 6);
  pointer("pointermove", to);
  pointer("pointerup", to);
}

beforeEach(() => {
  window.localStorage.clear();
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    callback(0);
    return 0;
  });
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query.includes("reduce"), addEventListener() {}, removeEventListener() {} }));
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("ListTable column sorting (#1119)", () => {
  it("cycles ascending, descending, then back to the list's own order, and announces it with aria-sort", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    expect(header).toHaveAttribute("aria-sort", "none");
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);

    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "ascending");
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);

    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "descending");
    expect(rowOrder()).toEqual(["row-c", "row-b", "row-a"]);

    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "none");
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("sorts numbers by value and keeps rows with nothing to compare last in both directions", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Size/ });
    await user.click(within(header).getByRole("button"));
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);
    await user.click(within(header).getByRole("button"));
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("sorts dates by time", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    await user.click(within(screen.getByRole("columnheader", { name: /^Added/ })).getByRole("button"));
    expect(rowOrder()).toEqual(["row-b", "row-c", "row-a"]);
  });

  it("does not offer sorting on a column marked sortable: false", () => {
    render(<Demo />);
    const header = screen.getAllByRole("columnheader")[3];
    expect(header).not.toHaveAttribute("aria-sort");
    expect(within(header).queryByRole("button")).toBeNull();
  });

  it("remembers the sort per list in this browser, and Reset columns clears it", async () => {
    const user = userEvent.setup();
    const { unmount } = render(<Demo />);
    await user.click(within(screen.getByRole("columnheader", { name: /^Name/ })).getByRole("button"));
    expect(screen.getByRole("button", { name: "Reset columns" })).toBeInTheDocument();
    unmount();

    render(<Demo />);
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);
    await user.click(screen.getByRole("button", { name: "Reset columns" }));
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
    expect(screen.queryByRole("button", { name: "Reset columns" })).toBeNull();
    expect(window.localStorage.getItem("deluno:list-columns:demo")).toBeNull();
  });

  it("keeps different lists apart", async () => {
    const user = userEvent.setup();
    const { unmount } = render(<Demo listId="one" />);
    await user.click(within(screen.getByRole("columnheader", { name: /^Name/ })).getByRole("button"));
    unmount();
    render(<Demo listId="two" />);
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("falls back to the default when saved state is damaged or storage throws", () => {
    window.localStorage.setItem("deluno:list-columns:demo", "{nope");
    render(<Demo />);
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(() => render(<Demo listId="blocked" />)).not.toThrow();
  });
});

describe("ListTable column order (#1119)", () => {
  it("drags a header past its neighbour: headers and cells reorder together, once, on drop", () => {
    mockLayout();
    render(<Demo />);
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    // Name (0-100) dragged to the middle of Size (100-200).
    drag(screen.getByRole("columnheader", { name: /^Name/ }), 50, 160);
    expect(headerOrder()).toEqual(["size", "name", "when", "actions"]);
    const cells = within(screen.getByTestId("row-b")).getAllByRole("cell").map((cell) => cell.textContent);
    expect(cells).toEqual(["30", "Bravo", "2026-01-02", "x"]);
    expect(screen.getByRole("button", { name: "Reset columns" })).toBeInTheDocument();
  });

  it("does not take a click as a drag: a small movement still sorts", async () => {
    mockLayout();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    pointer("pointerdown", 50, header);
    pointer("pointermove", 52);
    pointer("pointerup", 52);
    await userEvent.setup().click(within(header).getByRole("button"));
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(header).toHaveAttribute("aria-sort", "ascending");
  });

  it("keeps a column that was dragged where it was put after a reload", () => {
    mockLayout();
    const { unmount } = render(<Demo />);
    drag(screen.getByRole("columnheader", { name: /^Name/ }), 50, 160);
    unmount();
    render(<Demo />);
    expect(headerOrder()).toEqual(["size", "name", "when", "actions"]);
  });

  it("Escape puts a column back and changes nothing", () => {
    mockLayout();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    pointer("pointerdown", 50, header);
    pointer("pointermove", 60);
    pointer("pointermove", 160);
    act(() => {
      window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    });
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(screen.queryByRole("button", { name: "Reset columns" })).toBeNull();
  });

  it("moves a column from the keyboard with Alt and the arrow keys, and says where it went", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Size/ });
    within(header).getByRole("button").focus();
    await user.keyboard("{Alt>}{ArrowLeft}{/Alt}");
    expect(headerOrder()).toEqual(["size", "name", "when", "actions"]);
    expect(screen.getByRole("status")).toHaveTextContent("Size moved to position 1 of 3");
  });

  it("never moves a pinned column and keeps its slot", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    within(screen.getByRole("columnheader", { name: /^Added/ })).getByRole("button").focus();
    await user.keyboard("{Alt>}{ArrowRight}{/Alt}");
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(screen.getAllByRole("columnheader")[3]).not.toHaveAttribute("tabindex");
  });

  it("Reset columns puts the order and the sort back", async () => {
    mockLayout();
    const user = userEvent.setup();
    render(<Demo />);
    drag(screen.getByRole("columnheader", { name: /^Name/ }), 50, 160);
    await user.click(within(screen.getByRole("columnheader", { name: /^Size/ })).getByRole("button"));
    await user.click(screen.getByRole("button", { name: "Reset columns" }));
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("does nothing special without a list id", () => {
    render(
      <ListTable columns={[{ label: "Name" }, { label: "Size" }]} chevron={false}>
        <ListRow>
          <ListCell primary="a" />
          <ListCell primary="b" />
        </ListRow>
      </ListTable>
    );
    expect(screen.getAllByRole("columnheader")[0]).not.toHaveAttribute("aria-sort");
    expect(screen.queryByRole("button")).toBeNull();
  });
});

describe("dropOrder", () => {
  const mids = { a: 50, b: 150, c: 250, d: 350 };
  it("puts the dragged column where its centre is", () => {
    expect(dropOrder(["a", "b", "c", "d"], ["a", "b", "c"], ["a", "b", "c", "d"], mids, "a", 260)).toEqual(["b", "c", "a", "d"]);
    expect(dropOrder(["a", "b", "c", "d"], ["a", "b", "c"], ["a", "b", "c", "d"], mids, "c", 10)).toEqual(["c", "a", "b", "d"]);
  });
});
